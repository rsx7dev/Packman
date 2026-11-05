using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Packman.Models;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Packman.Services;

public class IntuneAuthService
{
    // "Microsoft Graph Command Line Tools", the client Connect-MgGraph uses. Present in
    // virtually every tenant, so it works when no Client ID is configured.
    private const string DefaultInteractiveClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    // Sign-in is read-only: browsing apps. Writes ask for their scopes when first used
    // (EnsureScopesAsync), and group and device access is asked for only when the Advanced
    // screen opens, so the shared client never receives consent the user did not need.
    public static readonly string[] InteractiveScopes =
    [
        "User.Read",
        "DeviceManagementApps.Read.All",
    ];

    /// <summary>Publishing and changing Win32 apps, their content and assignments.</summary>
    public static readonly string[] PublishScopes =
    [
        "DeviceManagementApps.ReadWrite.All",
    ];

    /// <summary>Asked for when the Advanced screen opens: group search and membership, PC and user lookup.</summary>
    public static readonly string[] DirectoryScopes =
    [
        "Group.ReadWrite.All",
        "GroupMember.ReadWrite.All",
        "Device.Read.All",
        "User.ReadBasic.All",
    ];

    /// <summary>"Create a group per package": POST /groups needs ReadWrite, granted through Advanced.</summary>
    public static readonly string[] GroupCreateScopes =
    [
        "Group.ReadWrite.All",
    ];

    /// <summary>
    /// The application permissions an app registration needs, with admin consent. The same
    /// access Packman asks of a signed-in user above, all of it up front since an app has no
    /// consent prompt; the first-run setup lists and copies them.
    /// </summary>
    public static readonly string[] AppPermissions =
    [
        "DeviceManagementApps.ReadWrite.All",
        "Group.ReadWrite.All",
        "GroupMember.ReadWrite.All",
        "User.ReadBasic.All",
        "Device.Read.All",
    ];

    private static readonly string[] AppOnlyScopes = ["https://graph.microsoft.com/.default"];

    private readonly object _stateLock = new();
    private AuthSession? _session;
    private long _sessionGeneration;
    private long _signInAttempt;

    // A retired client may still be signing an in-flight token request. Keep its
    // certificate alive until that request finishes, then release our owned copy.
    private sealed class AuthSession
    {
        public IPublicClientApplication? PublicClient { get; init; }
        public IConfidentialClientApplication? ConfidentialClient { get; init; }
        public IAccount? Account { get; init; }
        public X509Certificate2? Certificate { get; init; }
        public required string User { get; init; }
        public required string TenantId { get; init; }
        public string? TestToken { get; init; }
        // Replaced, never mutated, under _stateLock as consent widens.
        public string[] Scopes { get; set; } = InteractiveScopes;
        public int ActiveRequests { get; set; }
        public bool Retired { get; set; }
    }

    public string? SignedInUser { get { lock (_stateLock) return _session?.User; } }

    /// <summary>Directory (tenant) id of the current sign-in; null when signed out.</summary>
    public string? TenantId { get { lock (_stateLock) return _session?.TenantId; } }

    /// <summary>Tenant label from the signed-in UPN's domain ("contoso" for user@contoso.com); "your" when unknown.</summary>
    public string TenantName
    {
        get
        {
            var upn = SignedInUser ?? "";
            var at = upn.IndexOf('@');
            if (at < 0 || at == upn.Length - 1) return "your";
            var domain = upn[(at + 1)..];
            var dot = domain.IndexOf('.');
            return dot > 0 ? domain[..dot] : domain;
        }
    }

    /// <summary>Raised on sign-in state changes so screens can refresh.</summary>
    public event Action? StateChanged;

    public async Task SignInAsync(AuthMode mode, AppSettings.AuthConfig cfg, nint hwnd)
    {
        long attempt;
        lock (_stateLock) attempt = ++_signInAttempt;

        var candidate = mode == AuthMode.AppRegistration
            ? await SignInWithCertificateAsync(cfg)
            : await SignInInteractiveAsync(cfg, hwnd);

        lock (_stateLock)
        {
            // Signing out or starting another sign-in supersedes this attempt.
            if (attempt != _signInAttempt)
            {
                candidate.Certificate?.Dispose();
                throw new InvalidOperationException("This sign-in was superseded by a newer sign-in or sign-out.");
            }
            ReplaceSession(candidate);
        }

        StateChanged?.Invoke();
    }

    private static async Task<AuthSession> SignInInteractiveAsync(AppSettings.AuthConfig cfg, nint hwnd)
    {
        var clientId = string.IsNullOrWhiteSpace(cfg.ClientId)
            ? DefaultInteractiveClientId
            : cfg.ClientId.Trim();

        var authority = string.IsNullOrWhiteSpace(cfg.TenantId)
            ? "https://login.microsoftonline.com/organizations"
            : $"https://login.microsoftonline.com/{cfg.TenantId.Trim()}";

        var client = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(authority)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .Build();

        AuthenticationResult result;
        try
        {
            var accounts = await client.GetAccountsAsync();
            result = await client.AcquireTokenSilent(InteractiveScopes, accounts.FirstOrDefault()).ExecuteAsync();
        }
        catch (MsalUiRequiredException)
        {
            result = await client.AcquireTokenInteractive(InteractiveScopes)
                .WithParentActivityOrWindow(hwnd)
                .ExecuteAsync();
        }

        var account = result.Account
            ?? throw new InvalidOperationException("Interactive sign-in returned no account.");
        return new AuthSession { PublicClient = client, Account = account, User = account.Username, TenantId = TenantIdOf(result.AccessToken) };
    }

    private static async Task<AuthSession> SignInWithCertificateAsync(AppSettings.AuthConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ClientId))
            throw new InvalidOperationException("App registration mode requires a Client ID.");
        if (string.IsNullOrWhiteSpace(cfg.TenantId))
            throw new InvalidOperationException("App registration mode requires a Tenant ID.");
        if (string.IsNullOrWhiteSpace(cfg.CertificateThumbprint))
            throw new InvalidOperationException("App registration mode requires a certificate thumbprint.");

        var certificate = FindCertificate(cfg.CertificateThumbprint);
        try
        {
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException("The selected certificate has no private key. Import it with its private key first.");
            var authority = $"https://login.microsoftonline.com/{cfg.TenantId.Trim()}";
            var client = ConfidentialClientApplicationBuilder
                .Create(cfg.ClientId.Trim())
                .WithAuthority(authority)
                .WithCertificate(certificate)
                .Build();

            // A bad certificate or missing consent must leave the previous session intact.
            var result = await client.AcquireTokenForClient(AppOnlyScopes).ExecuteAsync();
            return new AuthSession
            {
                ConfidentialClient = client,
                Certificate = certificate,
                User = $"App registration {cfg.ClientId.Trim()}",
                TenantId = TenantIdOf(result.AccessToken),
            };
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The token's tid claim: the tenant GUID for both sign-in modes, even when Settings
    /// names the tenant by domain.
    /// </summary>
    private static string TenantIdOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
        return claims.RootElement.GetProperty("tid").GetString() ?? "";
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        var clean = thumbprint.Replace(" ", "").Trim();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var certificates = store.Certificates;
            try
            {
                foreach (var certificate in certificates)
                    if (string.Equals(certificate.Thumbprint, clean, StringComparison.OrdinalIgnoreCase))
                        return new X509Certificate2(certificate);
            }
            finally
            {
                foreach (var certificate in certificates) certificate.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Certificate with thumbprint '{thumbprint}' was not found in the CurrentUser or LocalMachine store.");
    }

    public async Task SignOutAsync()
    {
        AuthSession? previous;
        lock (_stateLock)
        {
            ++_signInAttempt;
            previous = _session;
            ReplaceSession(null);
        }
        StateChanged?.Invoke();

        if (previous is { PublicClient: { } client, Account: { } account })
            await client.RemoveAsync(account);
    }

    public bool IsSignedIn { get { lock (_stateLock) return _session != null; } }

    /// <summary>
    /// Asks the signed-in user to consent to <paramref name="extra"/> on top of what the
    /// session already holds, silently when the tenant already granted it. Returns false
    /// only when the user cancels or declines the consent. With no interactive session to
    /// widen (signed out, app registration, tests) it returns true and leaves the existing
    /// sign-in checks to fail. The session stays the same one, so running operations go on.
    /// </summary>
    public async Task<bool> EnsureScopesAsync(IReadOnlyList<string> extra, nint hwnd)
    {
        AuthSession? session;
        IPublicClientApplication? client;
        string[] wanted;
        lock (_stateLock)
        {
            session = _session;
            client = session?.PublicClient;
            if (session == null || client == null) return true;
            wanted = session.Scopes.Union(extra, StringComparer.OrdinalIgnoreCase).ToArray();
            if (wanted.Length == session.Scopes.Length) return true;
        }

        try
        {
            try
            {
                await client.AcquireTokenSilent(wanted, session.Account).ExecuteAsync();
            }
            catch (MsalUiRequiredException)
            {
                await client.AcquireTokenInteractive(wanted)
                    .WithAccount(session.Account)
                    .WithParentActivityOrWindow(hwnd)
                    .ExecuteAsync();
            }
        }
        catch (MsalException ex) when (ex.ErrorCode is MsalError.AuthenticationCanceledError or "access_denied")
        {
            return false;
        }

        lock (_stateLock)
        {
            // A sign-out or another sign-in meanwhile owns the state now.
            if (_session == session)
                session.Scopes = session.Scopes.Union(extra, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return true;
    }

    /// <summary>
    /// True when the session already holds <paramref name="scopes"/>, or when there is no
    /// interactive session to widen. Group and device features outside Advanced check this
    /// instead of prompting, so their consent is only ever asked for on that screen.
    /// </summary>
    public bool HasScopes(IReadOnlyList<string> scopes)
    {
        lock (_stateLock)
            return _session?.PublicClient == null
                || !scopes.Except(_session.Scopes, StringComparer.OrdinalIgnoreCase).Any();
    }

    /// <summary>Shown where a group or device feature needs <see cref="DirectoryScopes"/> not yet granted.</summary>
    public const string DirectoryAccessHint =
        "Group and device access is granted on the Advanced screen. Open Advanced once, allow access, then try again.";

    /// <summary>The message shown when <see cref="EnsureScopesAsync"/> returns false.</summary>
    public static string ConsentDeclinedMessage(IReadOnlyList<string> scopes) =>
        $"Permission not granted: {string.Join(", ", scopes)}. Nothing was sent to Intune.";

    /// <summary>The main window, the parent of a consent prompt; zero when there is none (tests).</summary>
    public static nint MainWindowHandle() =>
        System.Windows.Application.Current?.MainWindow is { } window
            ? new System.Windows.Interop.WindowInteropHelper(window).Handle
            : 0;

    /// <summary>Tests only: a signed-in session with a fixed token and tenant, without MSAL.</summary>
    internal void UseTestSession(string tenantId, string accessToken)
    {
        lock (_stateLock) ReplaceSession(new AuthSession { User = "test", TenantId = tenantId, TestToken = accessToken });
        StateChanged?.Invoke();
    }

    /// <summary>Graph access token for the current sign-in. Throws when not signed in.</summary>
    public Task<string> GetAccessTokenAsync() => CreateSessionTokenProvider()();

    /// <summary>
    /// Captures this sign-in for a multi-request operation. Its requests fail if the
    /// user signs out or switches accounts, even when a token request is already running.
    /// </summary>
    public Func<Task<string>> CreateSessionTokenProvider()
    {
        long generation;
        lock (_stateLock)
        {
            if (_session == null)
                throw new InvalidOperationException("Not signed in. Sign in on the Settings page before uploading.");
            generation = _sessionGeneration;
        }
        return () => GetAccessTokenAsync(generation);
    }

    private async Task<string> GetAccessTokenAsync(long generation)
    {
        AuthSession session;
        string[] scopes;
        lock (_stateLock)
        {
            EnsureSession(generation);
            session = _session!;
            scopes = session.Scopes;
            session.ActiveRequests++;
        }

        try
        {
            var token = session.TestToken ?? (session.ConfidentialClient is { } confidential
                ? await confidential.AcquireTokenForClient(AppOnlyScopes).ExecuteAsync()
                : await session.PublicClient!.AcquireTokenSilent(scopes, session.Account).ExecuteAsync()).AccessToken;
            lock (_stateLock) EnsureSession(generation);
            return token;
        }
        catch (MsalUiRequiredException ex) when (session.PublicClient != null)
        {
            lock (_stateLock)
            {
                // An old request must never clear a newer successful sign-in.
                EnsureSession(generation);
                ReplaceSession(null);
            }
            StateChanged?.Invoke();
            throw new InvalidOperationException(
                "The Intune sign-in has expired. Sign in again on the Settings page.", ex);
        }
        finally
        {
            lock (_stateLock)
            {
                session.ActiveRequests--;
                if (session.Retired && session.ActiveRequests == 0) session.Certificate?.Dispose();
            }
        }
    }

    // Called only while holding _stateLock.
    private void EnsureSession(long generation)
    {
        if (_session == null || generation != _sessionGeneration)
            throw new InvalidOperationException(
                "The Intune sign-in changed during this operation. Start the operation again after signing in.");
    }

    private void ReplaceSession(AuthSession? next)
    {
        var previous = _session;
        _session = next;
        _sessionGeneration++;
        if (previous == null) return;
        previous.Retired = true;
        if (previous.ActiveRequests == 0) previous.Certificate?.Dispose();
    }
}
