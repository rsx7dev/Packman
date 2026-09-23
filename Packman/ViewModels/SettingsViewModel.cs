using Microsoft.Identity.Client;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;

namespace Packman.ViewModels;

public class CertificateInfo
{
    public string FriendlyName { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Thumbprint { get; init; } = "";
    public string SubjectName { get; init; } = "";
    public string IssuerName { get; init; } = "";
    public DateTime NotAfter { get; init; }
    public bool HasPrivateKey { get; init; }
    public string DisplayName => !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName
        : !string.IsNullOrWhiteSpace(SubjectName) ? SubjectName
        : !string.IsNullOrWhiteSpace(Subject) ? Subject : "Unnamed certificate";
    public string Detail => $"{IssuerName} · expires {NotAfter:d MMM yyyy}"
        + (!HasPrivateKey ? " · no private key" : "")
        + (NotAfter < DateTime.Now ? " · expired" : "");
    public override string ToString() => DisplayName;
}

/// <summary>A single row in the connection-test results list (one required Graph scope).</summary>
public sealed class ConnectionCheckRow
{
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class GroupAssignmentRow : ObservableObject
{
    public string GroupName { get; }

    private AssignmentIntent _intent;
    public AssignmentIntent Intent
    {
        get => _intent;
        set
        {
            if (Set(ref _intent, value))
            {
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(IsRequired));
                OnPropertyChanged(nameof(IsUninstall));
            }
        }
    }

    public bool IsAvailable { get => _intent == AssignmentIntent.Available; set { if (value) Intent = AssignmentIntent.Available; } }
    public bool IsRequired { get => _intent == AssignmentIntent.Required; set { if (value) Intent = AssignmentIntent.Required; } }
    public bool IsUninstall { get => _intent == AssignmentIntent.Uninstall; set { if (value) Intent = AssignmentIntent.Uninstall; } }

    public RelayCommand RemoveCommand { get; }

    public GroupAssignmentRow(string name, AssignmentIntent intent, Action<GroupAssignmentRow> remove)
    {
        GroupName = name;
        _intent = intent;
        RemoveCommand = new RelayCommand(() => remove(this));
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _svc;
    private readonly IntuneAuthService _auth;

    // ── Interactive sign-in state ──────────────────────────────────────
    private bool _isSignedIn;
    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set { if (Set(ref _isSignedIn, value)) OnPropertyChanged(nameof(IsNotSignedIn)); }
    }
    public bool IsNotSignedIn => !IsSignedIn;

    private string _signedInUser = "";
    public string SignedInUser { get => _signedInUser; private set => Set(ref _signedInUser, value); }

    // ── Connection test ────────────────────────────────────────────────
    public ObservableCollection<ConnectionCheckRow> ConnectionChecks { get; } = new();

    private string _connectionStatus = "";
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set { if (Set(ref _connectionStatus, value)) OnPropertyChanged(nameof(HasConnectionStatus)); }
    }
    public bool HasConnectionStatus => !string.IsNullOrEmpty(_connectionStatus);

    private bool _connectionOk;
    public bool ConnectionOk { get => _connectionOk; private set => Set(ref _connectionOk, value); }

    /// <summary>Rail line from the last connection test: "Read and write verified", "Read only: publishing will fail"…</summary>
    private string _permissionSummary = "Not tested";
    public string PermissionSummary { get => _permissionSummary; private set => Set(ref _permissionSummary, value); }

    private bool _isTesting;
    public bool IsTesting
    {
        get => _isTesting;
        private set { if (Set(ref _isTesting, value)) TestConnectionCommand.RaiseCanExecuteChanged(); }
    }

    // ── Auth mode ──────────────────────────────────────────────────────
    private bool _isInteractive = true;
    public bool IsInteractive
    {
        get => _isInteractive;
        set { if (Set(ref _isInteractive, value)) OnPropertyChanged(nameof(IsAppRegistration)); }
    }
    public bool IsAppRegistration { get => !_isInteractive; set => IsInteractive = !value; }

    // ── Appearance ─────────────────────────────────────────────────────
    // Applied and saved on change: one you had to press Save for would revert on relaunch.
    private AppTheme _theme = AppTheme.Dark;
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (!Set(ref _theme, value)) return;
            OnPropertyChanged(nameof(IsThemeSystem));
            OnPropertyChanged(nameof(IsThemeDark));
            OnPropertyChanged(nameof(IsThemeLight));
            _svc.Settings.Theme = value;
            ThemeService.Apply(value);
            TryPersist();
        }
    }
    public bool IsThemeSystem { get => _theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }
    public bool IsThemeDark   { get => _theme == AppTheme.Dark;   set { if (value) Theme = AppTheme.Dark; } }
    public bool IsThemeLight  { get => _theme == AppTheme.Light;  set { if (value) Theme = AppTheme.Light; } }

    // ── App Registration fields ────────────────────────────────────────
    private string _tenantId = "";
    public string TenantId { get => _tenantId; set => Set(ref _tenantId, value, [nameof(TenantDisplay)]); }

    /// <summary>Tenant for the rail: the signed-in user's domain label, else the Tenant ID on screen.</summary>
    public string TenantDisplay => IsSignedIn && SignedInUser.Contains('@') ? _auth.TenantName
        : string.IsNullOrWhiteSpace(TenantId) ? "Not set" : TenantId.Trim();

    private string _clientId = "";
    public string ClientId { get => _clientId; set => Set(ref _clientId, value); }

    // Auth cert source
    private bool _authUseStoreCert = true;
    public bool AuthUseStoreCert
    {
        get => _authUseStoreCert;
        set { if (Set(ref _authUseStoreCert, value)) OnPropertyChanged(nameof(AuthUseManualThumbprint)); }
    }
    public bool AuthUseManualThumbprint { get => !_authUseStoreCert; set => AuthUseStoreCert = !value; }

    private string _authThumbprint = "";
    public string AuthThumbprint { get => _authThumbprint; set => Set(ref _authThumbprint, value); }

    private CertificateInfo? _selectedAuthCert;
    public CertificateInfo? SelectedAuthCert
    {
        get => _selectedAuthCert;
        set { if (Set(ref _selectedAuthCert, value) && value != null) AuthThumbprint = value.Thumbprint; }
    }

    // ── Code signing ───────────────────────────────────────────────────
    private bool _codeSigningEnabled;
    public bool CodeSigningEnabled
    {
        get => _codeSigningEnabled;
        set => Set(ref _codeSigningEnabled, value, [nameof(CodeSigningDisabled), nameof(CodeSignCertDisplay)]);
    }
    public bool CodeSigningDisabled { get => !_codeSigningEnabled; set => CodeSigningEnabled = !value; }

    private bool _codeSignUseStoreCert = true;
    public bool CodeSignUseStoreCert
    {
        get => _codeSignUseStoreCert;
        set { if (Set(ref _codeSignUseStoreCert, value)) OnPropertyChanged(nameof(CodeSignUseManualThumbprint)); }
    }
    public bool CodeSignUseManualThumbprint { get => !_codeSignUseStoreCert; set => CodeSignUseStoreCert = !value; }

    private string _codeSignThumbprint = "";
    public string CodeSignThumbprint { get => _codeSignThumbprint; set => Set(ref _codeSignThumbprint, value); }

    private string _codeSignCertName = "";
    public string CodeSignCertName { get => _codeSignCertName; set => Set(ref _codeSignCertName, value, [nameof(CodeSignCertDisplay)]); }

    /// <summary>Certificate named in the rail while signing is on.</summary>
    public string CodeSignCertDisplay => CodeSigningEnabled ? _selectedCodeSignCert?.DisplayName ?? CodeSignCertName : "";

    private string _codeSignCertSubject = "";
    public string CodeSignCertSubject { get => _codeSignCertSubject; set => Set(ref _codeSignCertSubject, value); }

    private string _codeSignTimestampServer = "http://timestamp.digicert.com";
    public string CodeSignTimestampServer { get => _codeSignTimestampServer; set => Set(ref _codeSignTimestampServer, value); }

    private CertificateInfo? _selectedCodeSignCert;
    public CertificateInfo? SelectedCodeSignCert
    {
        get => _selectedCodeSignCert;
        set { if (Set(ref _selectedCodeSignCert, value, [nameof(CodeSignCertDisplay)]) && value != null) CodeSignThumbprint = value.Thumbprint; }
    }

    // ── Network Paths ──────────────────────────────────────────────────
    private static readonly string[] PathDependents = [nameof(IsConfigured), nameof(ShowFirstRunBanner)];

    private string _intuneApplicationsPath = "";
    public string IntuneApplicationsPath { get => _intuneApplicationsPath; set => Set(ref _intuneApplicationsPath, value, PathDependents); }

    private string _psadtTemplatePath = "";
    public string PSADTTemplatePath { get => _psadtTemplatePath; set => Set(ref _psadtTemplatePath, value, PathDependents); }

    private string _intuneWinAppUtilPath = "";
    public string IntuneWinAppUtilPath { get => _intuneWinAppUtilPath; set => Set(ref _intuneWinAppUtilPath, value, PathDependents); }

    /// <summary>Where the settings file lives; shown under the page title.</summary>
    public string SettingsPath => _svc.SettingsPath;

    /// <summary>The three paths every flow needs are filled in.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_intuneApplicationsPath) &&
        !string.IsNullOrWhiteSpace(_psadtTemplatePath) &&
        !string.IsNullOrWhiteSpace(_intuneWinAppUtilPath);

    private bool _firstRunDismissed;
    public bool ShowFirstRunBanner => !_firstRunDismissed && !IsConfigured;
    public RelayCommand DismissFirstRunCommand { get; }

    // ── Group Assignment ───────────────────────────────────────────────
    private bool _createGroupPerPackage;
    public bool CreateGroupPerPackage
    {
        get => _createGroupPerPackage;
        set { if (Set(ref _createGroupPerPackage, value)) OnPropertyChanged(nameof(CreateGroupPerPackageDisabled)); }
    }
    public bool CreateGroupPerPackageDisabled { get => !_createGroupPerPackage; set => CreateGroupPerPackage = !value; }

    private string _groupNameTemplate = "%vendor%_%appName%_%appVersion%";
    public string GroupNameTemplate
    {
        get => _groupNameTemplate;
        set { if (Set(ref _groupNameTemplate, value)) OnPropertyChanged(nameof(GroupNamePreview)); }
    }

    // Sample values, so the token expansion is visible.
    public string GroupNamePreview => GroupAssignmentNamer.Build(GroupNameTemplate, "Contoso", "Acme Reader", "1.2.3");

    private AssignmentIntent _newGroupIntent = AssignmentIntent.Required;
    public AssignmentIntent NewGroupIntent
    {
        get => _newGroupIntent;
        set
        {
            if (Set(ref _newGroupIntent, value))
            {
                OnPropertyChanged(nameof(NewGroupAvailable));
                OnPropertyChanged(nameof(NewGroupRequired));
            }
        }
    }
    public bool NewGroupAvailable { get => _newGroupIntent == AssignmentIntent.Available; set { if (value) NewGroupIntent = AssignmentIntent.Available; } }
    public bool NewGroupRequired { get => _newGroupIntent == AssignmentIntent.Required; set { if (value) NewGroupIntent = AssignmentIntent.Required; } }

    private bool _createUninstallGroupPerPackage;
    public bool CreateUninstallGroupPerPackage
    {
        get => _createUninstallGroupPerPackage;
        set { if (Set(ref _createUninstallGroupPerPackage, value)) OnPropertyChanged(nameof(CreateUninstallGroupPerPackageDisabled)); }
    }
    public bool CreateUninstallGroupPerPackageDisabled { get => !_createUninstallGroupPerPackage; set => CreateUninstallGroupPerPackage = !value; }

    private string _uninstallGroupNameTemplate = "%vendor%_%appName%_%appVersion%_Uninstall";
    public string UninstallGroupNameTemplate
    {
        get => _uninstallGroupNameTemplate;
        set { if (Set(ref _uninstallGroupNameTemplate, value)) OnPropertyChanged(nameof(UninstallGroupNamePreview)); }
    }

    public string UninstallGroupNamePreview => GroupAssignmentNamer.Build(UninstallGroupNameTemplate, "Contoso", "Acme Reader", "1.2.3");

    // ── Intune Defaults ────────────────────────────────────────────────
    private string _defaultInstallCommand = AppSettings.IntuneDefaultsConfig.DefaultInstallCommand;
    public string DefaultInstallCommand { get => _defaultInstallCommand; set => Set(ref _defaultInstallCommand, value, [nameof(IntuneDefaultsConfigured)]); }

    private string _defaultUninstallCommand = AppSettings.IntuneDefaultsConfig.DefaultUninstallCommand;
    public string DefaultUninstallCommand { get => _defaultUninstallCommand; set => Set(ref _defaultUninstallCommand, value, [nameof(IntuneDefaultsConfigured)]); }

    public bool IntuneDefaultsConfigured =>
        !string.IsNullOrWhiteSpace(_defaultInstallCommand) && !string.IsNullOrWhiteSpace(_defaultUninstallCommand);

    public IReadOnlyList<AppSettings.RestartBehaviorOption> RestartBehaviors { get; } = AppSettings.IntuneDefaultsConfig.RestartBehaviors;

    private string _defaultRestartBehavior = AppSettings.IntuneDefaultsConfig.DefaultRestartBehavior;
    public string DefaultRestartBehavior { get => _defaultRestartBehavior; set => Set(ref _defaultRestartBehavior, value); }

    private string _defaultMaxRunTimeMinutes = AppSettings.IntuneDefaultsConfig.DefaultMaxRunTimeMinutes.ToString();
    public string DefaultMaxRunTimeMinutes { get => _defaultMaxRunTimeMinutes; set => Set(ref _defaultMaxRunTimeMinutes, value); }

    private string _defaultPrivacyUrl = "";
    public string DefaultPrivacyUrl { get => _defaultPrivacyUrl; set => Set(ref _defaultPrivacyUrl, value); }

    private string _defaultInformationUrl = "";
    public string DefaultInformationUrl { get => _defaultInformationUrl; set => Set(ref _defaultInformationUrl, value); }

    private string _displayNameTemplate = AppSettings.IntuneDefaultsConfig.DefaultDisplayNameTemplate;
    public string DisplayNameTemplate
    {
        get => _displayNameTemplate;
        set { if (Set(ref _displayNameTemplate, value)) OnPropertyChanged(nameof(DisplayNamePreview)); }
    }

    public string DisplayNamePreview => GroupAssignmentNamer.Build(DisplayNameTemplate, "Contoso", "Acme Reader", "1.2.3");

    private string _newGroupNameInput = "";
    public string NewGroupNameInput { get => _newGroupNameInput; set => Set(ref _newGroupNameInput, value); }

    public ObservableCollection<GroupAssignmentRow> ExistingGroups { get; } = new();

    public RelayCommand AddGroupCommand { get; }

    // ── Intune Defaults ────────────────────────────────────────────────
    public IReadOnlyList<string> OperatingSystems { get; } = RequirementInfo.SupportedOperatingSystems;

    private string _defaultOperatingSystem = "Windows 10 1607";
    public string DefaultOperatingSystem { get => _defaultOperatingSystem; set => Set(ref _defaultOperatingSystem, value); }

    private string _defaultMinFreeDiskSpaceMB = "";
    public string DefaultMinFreeDiskSpaceMB { get => _defaultMinFreeDiskSpaceMB; set => Set(ref _defaultMinFreeDiskSpaceMB, value); }

    private string _defaultMinMemoryMB = "";
    public string DefaultMinMemoryMB { get => _defaultMinMemoryMB; set => Set(ref _defaultMinMemoryMB, value); }

    private string _defaultMinProcessors = "";
    public string DefaultMinProcessors { get => _defaultMinProcessors; set => Set(ref _defaultMinProcessors, value); }

    private string _defaultMinCpuSpeedMHz = "";
    public string DefaultMinCpuSpeedMHz { get => _defaultMinCpuSpeedMHz; set => Set(ref _defaultMinCpuSpeedMHz, value); }

    private string _newReturnCodeInput = "";
    public string NewReturnCodeInput { get => _newReturnCodeInput; set => Set(ref _newReturnCodeInput, value); }

    private string _newReturnCodeDescription = "";
    public string NewReturnCodeDescription { get => _newReturnCodeDescription; set => Set(ref _newReturnCodeDescription, value); }

    public ObservableCollection<ReturnCodeRow> ReturnCodes { get; } = new();

    public RelayCommand AddReturnCodeCommand { get; }
    public RelayCommand RestoreDefaultReturnCodesCommand { get; }

    // ── Save feedback ──────────────────────────────────────────────────
    private string _saveStatus = "";
    public string SaveStatus { get => _saveStatus; set => Set(ref _saveStatus, value); }

    /// <summary>How the footer shows SaveStatus: ok after a save, warn for a failure, empty for a progress note.</summary>
    private string _saveStatusKind = "";
    public string SaveStatusKind { get => _saveStatusKind; private set => Set(ref _saveStatusKind, value); }

    public ObservableCollection<CertificateInfo> AvailableCertificates { get; } = new();

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public AsyncRelayCommand SignInCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public AsyncRelayCommand TestConnectionCommand { get; }

    public SettingsViewModel(SettingsService svc, IntuneAuthService auth)
    {
        DismissFirstRunCommand = new RelayCommand(() => { _firstRunDismissed = true; OnPropertyChanged(nameof(ShowFirstRunBanner)); });
        _svc = svc;
        _auth = auth;
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        SignInCommand = new AsyncRelayCommand(SignInAsync);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsTesting);
        AddGroupCommand = new RelayCommand(AddGroup);
        AddReturnCodeCommand = new RelayCommand(AddReturnCode);
        RestoreDefaultReturnCodesCommand = new RelayCommand(() => LoadReturnCodes(ReturnCodeInfo.Defaults()));
        LoadFromSettings();
        LoadCertificatesFromStore();

        // The view is built once but the sign-in lives in AppServices.Auth, so read the
        // current state and follow it rather than assuming this page did the signing in.
        SyncSignInState();
        _auth.StateChanged += SyncSignInState;

        // An unparseable file was set aside at startup; say so rather than looking fresh.
        if (_svc.LoadError != null)
        {
            SaveStatusKind = "warn";
            SaveStatus = _svc.LoadError;
        }
    }

    private void LoadFromSettings()
    {
        var s = _svc.Settings;
        _theme = s.Theme;
        IsInteractive = s.AuthMode == AuthMode.Interactive;
        TenantId = s.Authentication.TenantId;
        ClientId = s.Authentication.ClientId;
        AuthThumbprint = s.Authentication.CertificateThumbprint;
        AuthUseStoreCert = true;

        CodeSigningEnabled = s.CodeSigning.Enabled;
        CodeSignThumbprint = s.CodeSigning.CertificateThumbprint;
        CodeSignCertName = s.CodeSigning.CertificateName;
        CodeSignCertSubject = s.CodeSigning.CertificateSubject;
        CodeSignTimestampServer = s.CodeSigning.TimestampServer;
        CodeSignUseStoreCert = true;

        IntuneApplicationsPath = s.NetworkPaths.IntuneApplications;
        PSADTTemplatePath = s.NetworkPaths.PSADTTemplate;
        IntuneWinAppUtilPath = s.NetworkPaths.IntuneWinAppUtil;

        CreateGroupPerPackage = s.GroupAssignment.CreateGroupPerPackage;
        GroupNameTemplate = s.GroupAssignment.GroupNameTemplate;
        NewGroupIntent = s.GroupAssignment.NewGroupIntent;
        CreateUninstallGroupPerPackage = s.GroupAssignment.CreateUninstallGroupPerPackage;
        UninstallGroupNameTemplate = s.GroupAssignment.UninstallGroupNameTemplate;
        ExistingGroups.Clear();
        foreach (var g in s.GroupAssignment.ExistingGroups)
            ExistingGroups.Add(new GroupAssignmentRow(g.GroupName, g.Intent, RemoveGroup));

        var req = s.IntuneDefaults.Requirements;
        DefaultOperatingSystem = req.MinimumOperatingSystem;
        DefaultMinFreeDiskSpaceMB = req.MinimumFreeDiskSpaceMB?.ToString() ?? "";
        DefaultMinMemoryMB = req.MinimumMemoryMB?.ToString() ?? "";
        DefaultMinProcessors = req.MinimumNumberOfProcessors?.ToString() ?? "";
        DefaultMinCpuSpeedMHz = req.MinimumCpuSpeedMHz?.ToString() ?? "";
        LoadReturnCodes(s.IntuneDefaults.ReturnCodes);

        DefaultInstallCommand = s.IntuneDefaults.InstallCommand;
        DefaultUninstallCommand = s.IntuneDefaults.UninstallCommand;
        DefaultRestartBehavior = RestartBehaviors.Any(o => o.Value == s.IntuneDefaults.RestartBehavior)
            ? s.IntuneDefaults.RestartBehavior
            : AppSettings.IntuneDefaultsConfig.DefaultRestartBehavior;
        DefaultMaxRunTimeMinutes = s.IntuneDefaults.MaxRunTimeMinutes.ToString();
        DefaultPrivacyUrl = s.IntuneDefaults.PrivacyUrl;
        DefaultInformationUrl = s.IntuneDefaults.InformationUrl;
        DisplayNameTemplate = s.IntuneDefaults.DisplayNameTemplate;
    }

    private void LoadReturnCodes(IEnumerable<ReturnCodeInfo> codes)
    {
        ReturnCodes.Clear();
        foreach (var c in codes)
            ReturnCodes.Add(new ReturnCodeRow(c.Code, c.Type, c.Description, RemoveReturnCode));
    }

    private void AddReturnCode()
    {
        if (!int.TryParse(NewReturnCodeInput.Trim(), out var code)) return;
        if (ReturnCodes.Any(r => r.Code == code.ToString())) return;
        ReturnCodes.Add(new ReturnCodeRow(code, ReturnCodeType.Success, NewReturnCodeDescription.Trim(), RemoveReturnCode));
        NewReturnCodeInput = "";
        NewReturnCodeDescription = "";
    }

    private void RemoveReturnCode(ReturnCodeRow row) => ReturnCodes.Remove(row);

    private void AddGroup()
    {
        var name = NewGroupNameInput.Trim();
        if (string.IsNullOrEmpty(name)) return;
        ExistingGroups.Add(new GroupAssignmentRow(name, AssignmentIntent.Required, RemoveGroup));
        NewGroupNameInput = "";
    }

    private void RemoveGroup(GroupAssignmentRow row) => ExistingGroups.Remove(row);

    private void LoadCertificatesFromStore()
    {
        AvailableCertificates.Clear();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                foreach (var cert in store.Certificates)
                {
                    using (cert)
                    {
                        AvailableCertificates.Add(new CertificateInfo
                        {
                            FriendlyName = cert.FriendlyName,
                            Subject = cert.Subject,
                            SubjectName = cert.GetNameInfo(X509NameType.SimpleName, false),
                            IssuerName = cert.GetNameInfo(X509NameType.SimpleName, true),
                            NotAfter = cert.NotAfter,
                            HasPrivateKey = cert.HasPrivateKey,
                            Thumbprint = cert.Thumbprint
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                SaveStatusKind = "warn";
                SaveStatus = $"Could not read the {location} certificate store: {ex.Message}";
            }
        }
        if (!string.IsNullOrEmpty(AuthThumbprint))
            SelectedAuthCert = AvailableCertificates.FirstOrDefault(c => c.Thumbprint == AuthThumbprint);
        if (!string.IsNullOrEmpty(CodeSignThumbprint))
            SelectedCodeSignCert = AvailableCertificates.FirstOrDefault(c => c.Thumbprint == CodeSignThumbprint);
    }

    private async Task SignInAsync()
    {
        SaveStatusKind = "";
        SaveStatus = "Signing in…";
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(
                System.Windows.Application.Current.MainWindow).Handle;
            var mode = IsInteractive ? AuthMode.Interactive : AuthMode.AppRegistration;
            await _auth.SignInAsync(mode, CurrentAuthConfig(), hwnd);
            SaveStatus = "";
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            SaveStatus = "";
        }
        catch (Exception ex)
        {
            SaveStatusKind = "warn";
            SaveStatus = $"Sign-in failed: {ex.Message}";
        }
    }

    private async Task SignOutAsync()
    {
        await _auth.SignOutAsync();
        ConnectionChecks.Clear();
        ConnectionStatus = "";
        ConnectionOk = false;
    }

    private void SyncSignInState()
    {
        IsSignedIn = _auth.IsSignedIn;
        SignedInUser = _auth.SignedInUser ?? "";
        OnPropertyChanged(nameof(TenantDisplay));
        // A verdict belongs to the sign-in it was tested with.
        PermissionSummary = "Not tested";
    }

    private async Task TestConnectionAsync()
    {
        ConnectionChecks.Clear();
        ConnectionOk = false;
        PermissionSummary = "Not tested";
        IsTesting = true;
        try
        {
            // App-only auth has no sign-in button of its own, so the test signs in with the
            // tenant, client and certificate currently on screen. That also means editing a
            // field and testing again uses the new values instead of the earlier sign-in.
            if (IsAppRegistration)
            {
                var missing = AppRegistrationProblem();
                if (missing != null)
                {
                    ConnectionStatus = missing;
                    return;
                }

                ConnectionStatus = "Signing in with the app registration…";
                try
                {
                    await _auth.SignInAsync(AuthMode.AppRegistration, CurrentAuthConfig(), nint.Zero);
                }
                catch (Exception ex)
                {
                    ConnectionStatus = $"App registration sign-in failed: {ex.Message}";
                    return;
                }
            }
            else if (!_auth.IsSignedIn)
            {
                ConnectionStatus = "Sign in first, then test the connection.";
                return;
            }

            ConnectionStatus = "Testing connection to Microsoft Intune…";
            // Group creation is checked against the toggles on screen, like the sign-in fields.
            var result = await AppServices.Apps.TestConnectionAsync(CreateGroupPerPackage || CreateUninstallGroupPerPackage);
            foreach (var c in result.Checks)
                ConnectionChecks.Add(new ConnectionCheckRow { Name = c.Name, Ok = c.Ok, Detail = c.Detail });
            ConnectionOk = result.Success;
            ConnectionStatus = result.Message;
            PermissionSummary = result.PermissionSummary;
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Connection test failed: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private AppSettings.AuthConfig CurrentAuthConfig() => new()
    {
        TenantId = TenantId.Trim(),
        ClientId = ClientId.Trim(),
        CertificateThumbprint = IsAppRegistration ? AuthThumbprint.Trim() : ""
    };

    /// <summary>The first app-registration field still missing, phrased for the user; null when complete.</summary>
    private string? AppRegistrationProblem()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
            return "Enter a Tenant ID before testing the connection.";
        if (string.IsNullOrWhiteSpace(ClientId))
            return "Enter an Application (Client) ID before testing the connection.";
        if (string.IsNullOrWhiteSpace(AuthThumbprint))
            return AuthUseStoreCert
                ? "Select a certificate before testing the connection."
                : "Enter a certificate thumbprint before testing the connection.";
        return null;
    }

    private void Reset()
    {
        LoadFromSettings();
        SaveStatus = "";
    }

    private static string Fallback(string text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    private void Save()
    {
        var invalidCode = ReturnCodes.FirstOrDefault(r => r.ToInfo() == null);
        if (invalidCode != null)
        {
            SaveStatusKind = "warn";
            SaveStatus = $"Return code '{invalidCode.Code}' is not a number. Nothing was saved.";
            return;
        }

        var s = _svc.Settings;
        s.AuthMode = IsInteractive ? AuthMode.Interactive : AuthMode.AppRegistration;
        s.Authentication.TenantId = TenantId;
        s.Authentication.ClientId = ClientId;
        s.Authentication.CertificateThumbprint = AuthThumbprint;

        s.CodeSigning.Enabled = CodeSigningEnabled;
        s.CodeSigning.CertificateThumbprint = CodeSignThumbprint;
        s.CodeSigning.TimestampServer = CodeSignTimestampServer;
        s.CodeSigning.CertificateName = _selectedCodeSignCert?.FriendlyName ?? CodeSignCertName;
        s.CodeSigning.CertificateSubject = _selectedCodeSignCert?.Subject ?? CodeSignCertSubject;

        s.NetworkPaths.IntuneApplications = IntuneApplicationsPath;
        s.NetworkPaths.PSADTTemplate = PSADTTemplatePath;
        s.NetworkPaths.IntuneWinAppUtil = IntuneWinAppUtilPath;

        s.GroupAssignment.CreateGroupPerPackage = CreateGroupPerPackage;
        s.GroupAssignment.GroupNameTemplate = GroupNameTemplate;
        s.GroupAssignment.NewGroupIntent = NewGroupIntent;
        s.GroupAssignment.CreateUninstallGroupPerPackage = CreateUninstallGroupPerPackage;
        s.GroupAssignment.UninstallGroupNameTemplate = UninstallGroupNameTemplate;
        s.GroupAssignment.ExistingGroups = ExistingGroups
            .Select(g => new AppSettings.ExistingGroupAssignment
            {
                GroupName = g.GroupName,
                Intent = g.Intent
            })
            .ToList();

        s.IntuneDefaults.Requirements = RequirementInfo.Parse(
            DefaultOperatingSystem, DefaultMinFreeDiskSpaceMB, DefaultMinMemoryMB, DefaultMinProcessors, DefaultMinCpuSpeedMHz);
        s.IntuneDefaults.ReturnCodes = ReturnCodes.Select(r => r.ToInfo()).OfType<ReturnCodeInfo>().ToList();

        s.IntuneDefaults.InstallCommand = Fallback(DefaultInstallCommand, AppSettings.IntuneDefaultsConfig.DefaultInstallCommand);
        s.IntuneDefaults.UninstallCommand = Fallback(DefaultUninstallCommand, AppSettings.IntuneDefaultsConfig.DefaultUninstallCommand);
        s.IntuneDefaults.RestartBehavior = DefaultRestartBehavior;
        // Intune accepts 1–1440 minutes; anything else falls back to the portal default.
        s.IntuneDefaults.MaxRunTimeMinutes = int.TryParse(DefaultMaxRunTimeMinutes.Trim(), out var minutes) && minutes is >= 1 and <= 1440
            ? minutes
            : AppSettings.IntuneDefaultsConfig.DefaultMaxRunTimeMinutes;
        DefaultMaxRunTimeMinutes = s.IntuneDefaults.MaxRunTimeMinutes.ToString();
        s.IntuneDefaults.PrivacyUrl = DefaultPrivacyUrl.Trim();
        s.IntuneDefaults.InformationUrl = DefaultInformationUrl.Trim();
        s.IntuneDefaults.DisplayNameTemplate = Fallback(DisplayNameTemplate, AppSettings.IntuneDefaultsConfig.DefaultDisplayNameTemplate);

        TryPersist();
    }

    /// <summary>Writes the settings file and reports what actually happened.</summary>
    private void TryPersist()
    {
        try
        {
            _svc.Save();
            SaveStatusKind = "ok";
            SaveStatus = $"Settings saved at {DateTime.Now:t}";
        }
        catch (Exception ex)
        {
            SaveStatusKind = "warn";
            SaveStatus = $"Settings could not be saved: {ex.Message}";
        }
    }
}
