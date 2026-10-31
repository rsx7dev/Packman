using Microsoft.Identity.Client;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;

namespace Packman.ViewModels;

/// <summary>One line of the setup summary, with the step that changes it.</summary>
public sealed record SummaryRow(string Label, string Value, int StepIndex)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// The first-run setup: connection, paths and package layout, in five steps. Run setup again
/// uses the same view model, prefilled from the saved settings. Nothing is saved before Finish.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    public const int WelcomeStep = 0, ConnectionStep = 1, PathsStep = 2, LayoutStep = 3, SummaryStep = 4;

    public const string PsadtReleasesUrl = "https://github.com/PSAppDeployToolkit/PSAppDeployToolkit/releases";
    public const string IntuneWinAppUtilUrl = "https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool";

    private readonly SettingsService _svc;
    private readonly IntuneAuthService _auth;
    private CancellationTokenSource? _cts;

    public ObservableCollection<StepViewModel> Steps { get; }

    /// <summary>Raised after Finish has saved the settings.</summary>
    public event Action? Completed;

    /// <summary>Handle of the setup window, for the interactive sign-in prompt.</summary>
    public nint WindowHandle { get; set; }

    // ── Steps ──────────────────────────────────────────────────────────
    private int _currentStepIndex;
    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        private set
        {
            if (!Set(ref _currentStepIndex, value)) return;
            for (var i = 0; i < Steps.Count; i++)
            {
                Steps[i].IsCurrent = i == value;
                Steps[i].IsDone = i < value;
            }
            if (value == SummaryStep) BuildSummary();
            RaiseAll(nameof(NextLabel), nameof(StepHint), nameof(IsSummaryStep), nameof(StepCaption));
            RaiseCommandStates();
        }
    }

    public bool IsSummaryStep => CurrentStepIndex == SummaryStep;
    public string NextLabel => CurrentStepIndex switch { WelcomeStep => "Get started", SummaryStep => "Finish", _ => "Next" };

    public string StepHint => CurrentStepIndex switch
    {
        WelcomeStep => "Four short steps. Run setup again any time from Settings.",
        ConnectionStep => ConnectionOk || ConnectLater
            ? "Ready to continue."
            : "Test the connection, or choose Connect later, to continue.",
        PathsStep => "Paths are checked when you press Next.",
        LayoutStep => "Applies to new packages only. Existing packages are never changed.",
        _ => "Finish saves these settings and opens Packman.",
    };

    /// <summary>"Step 2 of 5": the page subtitle.</summary>
    public string StepCaption => $"Step {CurrentStepIndex + 1} of {Steps.Count} · {Steps[CurrentStepIndex].Title}";

    // The rail's running summary of what is set so far.
    public string RailConnection => ConnectionOk ? "Tested" : ConnectLater ? "Connect later" : "Not tested";
    public string RailConnectionKind => ConnectionOk ? "ok" : ConnectLater ? "info" : "pending";

    private SetupCheck?[] PathChecks => [ApplicationsRootCheck, TemplateCheck, IntuneWinAppUtilCheck];
    public string RailPaths => PathChecks.Any(c => c == null) ? "Not checked" : PathChecks.Any(c => c!.Kind == "bad") ? "Needs a fix" : "Checked";
    public string RailPathsKind => PathChecks.Any(c => c == null) ? "pending" : PathChecks.Any(c => c!.Kind == "bad") ? "bad" : PathChecks.Any(c => c!.Kind == "warn") ? "warn" : "ok";

    public string RailLayout => $"{2 + PsadtLayout.OptionalFolders(Layout.IconFolder, Layout.ExtraFolders).Count} folders";

    private void RaiseRail() =>
        RaiseAll(nameof(RailConnection), nameof(RailConnectionKind), nameof(RailPaths), nameof(RailPathsKind), nameof(RailLayout));

    public AsyncRelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand<int> GoToStepCommand { get; }
    public RelayCommand CancelCommand { get; }

    private bool CanNext => !IsBusy && (CurrentStepIndex != ConnectionStep || ConnectionOk || ConnectLater);

    private void RaiseCommandStates()
    {
        NextCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        GoToStepCommand.RaiseCanExecuteChanged();
        TestConnectionCommand.RaiseCanExecuteChanged();
        ConnectLaterCommand.RaiseCanExecuteChanged();
    }

    // ── Busy ───────────────────────────────────────────────────────────
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) RaiseCommandStates(); }
    }

    private string _busyText = "";
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    // ── Connection ─────────────────────────────────────────────────────
    private bool _isInteractive = true;
    public bool IsInteractive
    {
        get => _isInteractive;
        set
        {
            if (!Set(ref _isInteractive, value)) return;
            OnPropertyChanged(nameof(IsAppRegistration));
            ConnectionInputChanged();
        }
    }
    public bool IsAppRegistration { get => !_isInteractive; set => IsInteractive = !value; }

    private string _tenantId = "";
    public string TenantId { get => _tenantId; set { if (Set(ref _tenantId, value)) ConnectionInputChanged(); } }

    private string _clientId = "";
    public string ClientId { get => _clientId; set { if (Set(ref _clientId, value)) ConnectionInputChanged(); } }

    private string _thumbprint = "";
    public string Thumbprint
    {
        get => _thumbprint;
        set { if (Set(ref _thumbprint, value)) { OnPropertyChanged(nameof(SavedCertMissing)); ConnectionInputChanged(); } }
    }

    public ObservableCollection<CertificateInfo> AvailableCertificates { get; } = new();

    private CertificateInfo? _selectedCert;
    public CertificateInfo? SelectedCert
    {
        get => _selectedCert;
        set { if (Set(ref _selectedCert, value) && value != null) Thumbprint = value.Thumbprint; }
    }

    private string _certificateError = "";
    /// <summary>Why the store could not be read, or "".</summary>
    public string CertificateError
    {
        get => _certificateError;
        private set { if (Set(ref _certificateError, value)) OnPropertyChanged(nameof(HasCertificateError)); }
    }
    public bool HasCertificateError => CertificateError.Length > 0;

    /// <summary>The store holds no certificate with a private key (and could be read).</summary>
    public bool NoCertificates => AvailableCertificates.Count == 0 && !HasCertificateError;

    /// <summary>A saved thumbprint that is not in the store; kept as is.</summary>
    public bool SavedCertMissing =>
        Thumbprint.Length > 0 && !AvailableCertificates.Any(c => c.Thumbprint.Equals(Thumbprint, StringComparison.OrdinalIgnoreCase));

    public RelayCommand RefreshCertificatesCommand { get; }

    public string[] RequiredPermissions => IntuneAuthService.AppPermissions;
    public string RequiredPermissionsText => string.Join(Environment.NewLine, RequiredPermissions);

    public ObservableCollection<ConnectionCheckRow> ConnectionChecks { get; } = new();

    private const string NotTested = "Not tested yet. Test the connection, or choose Connect later.";

    private string _connectionStatus = NotTested;
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }

    private string _connectionKind = "neutral";
    /// <summary>How the status shows: neutral, ok, warn or bad.</summary>
    public string ConnectionKind { get => _connectionKind; private set => Set(ref _connectionKind, value); }

    private bool _connectionOk;
    public bool ConnectionOk
    {
        get => _connectionOk;
        private set { if (Set(ref _connectionOk, value)) { RaiseAll(nameof(StepHint)); RaiseRail(); } NextCommand?.RaiseCanExecuteChanged(); }
    }

    private bool _connectLater;
    public bool ConnectLater
    {
        get => _connectLater;
        private set { if (Set(ref _connectLater, value)) { RaiseAll(nameof(StepHint)); RaiseRail(); } NextCommand?.RaiseCanExecuteChanged(); }
    }

    public AsyncRelayCommand TestConnectionCommand { get; }
    public RelayCommand ConnectLaterCommand { get; }

    /// <summary>A pass belongs to the values it was tested with; editing one clears it.</summary>
    private void ConnectionInputChanged()
    {
        if (!ConnectionOk && ConnectionChecks.Count == 0 && ConnectionKind == "neutral") return;
        ConnectionOk = false;
        ConnectionChecks.Clear();
        SetConnection("neutral", NotTested);
    }

    /// <summary>The status as a check, for the shared callout.</summary>
    public SetupCheck ConnectionResult => new(ConnectionKind, ConnectionStatus);

    private void SetConnection(string kind, string text)
    {
        ConnectionKind = kind;
        ConnectionStatus = text;
        OnPropertyChanged(nameof(ConnectionResult));
    }

    public async Task TestConnectionAsync()
    {
        if (IsBusy) return;

        ConnectionOk = false;
        ConnectionChecks.Clear();
        if (IsAppRegistration && AppRegistrationProblem() is { } missing)
        {
            SetConnection("warn", missing);
            return;
        }

        _cts = new CancellationTokenSource();
        BusyText = "Testing connection…";
        IsBusy = true;
        try
        {
            var cfg = new AppSettings.AuthConfig
            {
                TenantId = TenantId.Trim(),
                ClientId = ClientId.Trim(),
                CertificateThumbprint = IsAppRegistration ? Thumbprint.Trim() : "",
            };
            if (IsAppRegistration)
                await _auth.SignInAsync(AuthMode.AppRegistration, cfg, nint.Zero);
            else if (!_auth.IsSignedIn)
                await _auth.SignInAsync(AuthMode.Interactive, cfg, WindowHandle);

            var groups = _svc.Settings.GroupAssignment;
            var result = await AppServices.Apps.TestConnectionAsync(
                groups.CreateGroupPerPackage || groups.CreateUninstallGroupPerPackage, _cts.Token);
            foreach (var c in result.Checks)
                ConnectionChecks.Add(new ConnectionCheckRow { Name = c.Name, Ok = c.Ok, Detail = c.Detail });
            ConnectionOk = result.Success;
            if (result.Success) ConnectLater = false;
            SetConnection(result.Success ? "ok" : "bad", result.Message);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            SetConnection("neutral", "Sign-in was cancelled.");
        }
        catch (OperationCanceledException)
        {
            SetConnection("neutral", "Connection test cancelled.");
        }
        catch (Exception ex)
        {
            SetConnection("bad", $"Could not connect: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The first app registration field still missing, phrased for the user; null when complete.</summary>
    private string? AppRegistrationProblem()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) return "Enter the Tenant ID before testing the connection.";
        if (string.IsNullOrWhiteSpace(ClientId)) return "Enter the Application (Client) ID before testing the connection.";
        if (string.IsNullOrWhiteSpace(Thumbprint)) return "Select a certificate before testing the connection.";
        return null;
    }

    private void LoadCertificates()
    {
        AvailableCertificates.Clear();
        CertificateError = "";
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                using (cert)
                {
                    if (!cert.HasPrivateKey) continue;
                    AvailableCertificates.Add(new CertificateInfo
                    {
                        FriendlyName = cert.FriendlyName,
                        Subject = cert.Subject,
                        SubjectName = cert.GetNameInfo(X509NameType.SimpleName, false),
                        IssuerName = cert.GetNameInfo(X509NameType.SimpleName, true),
                        NotAfter = cert.NotAfter,
                        HasPrivateKey = true,
                        Thumbprint = cert.Thumbprint,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            CertificateError = $"Could not read the certificate store: {ex.Message}";
        }

        _selectedCert = AvailableCertificates.FirstOrDefault(c => c.Thumbprint.Equals(Thumbprint, StringComparison.OrdinalIgnoreCase));
        RaiseAll(nameof(SelectedCert), nameof(NoCertificates), nameof(SavedCertMissing));
    }

    // ── Paths ──────────────────────────────────────────────────────────
    private string _applicationsRoot = "";
    public string ApplicationsRoot
    {
        get => _applicationsRoot;
        set { if (Set(ref _applicationsRoot, value)) ApplicationsRootCheck = null; }
    }

    private string _templatePath = "";
    public string TemplatePath
    {
        get => _templatePath;
        set { if (Set(ref _templatePath, value)) TemplateCheck = null; }
    }

    private string _intuneWinAppUtilPath = "";
    public string IntuneWinAppUtilPath
    {
        get => _intuneWinAppUtilPath;
        set { if (Set(ref _intuneWinAppUtilPath, value)) IntuneWinAppUtilCheck = null; }
    }

    private SetupCheck? _applicationsRootCheck;
    public SetupCheck? ApplicationsRootCheck { get => _applicationsRootCheck; private set { if (Set(ref _applicationsRootCheck, value)) RaiseRail(); } }

    private SetupCheck? _templateCheck;
    public SetupCheck? TemplateCheck { get => _templateCheck; private set { if (Set(ref _templateCheck, value)) RaiseRail(); } }

    private SetupCheck? _intuneWinAppUtilCheck;
    public SetupCheck? IntuneWinAppUtilCheck { get => _intuneWinAppUtilCheck; private set { if (Set(ref _intuneWinAppUtilCheck, value)) RaiseRail(); } }

    /// <summary>Runs the three path checks off the UI thread. True when none is bad.</summary>
    private async Task<bool> CheckPathsAsync()
    {
        _cts = new CancellationTokenSource();
        BusyText = "Checking paths…";
        IsBusy = true;
        try
        {
            var root = ApplicationsRoot.Trim();
            var template = TemplatePath.Trim();
            var tool = IntuneWinAppUtilPath.Trim();
            // WaitAsync lets Cancel free the page while a dead share is still timing out.
            var checks = await Task.Run(() => (SetupChecks.ApplicationsRoot(root), SetupChecks.Template(template), SetupChecks.IntuneWinAppUtil(tool)))
                .WaitAsync(_cts.Token);
            (ApplicationsRootCheck, TemplateCheck, IntuneWinAppUtilCheck) = checks;
            return new[] { checks.Item1, checks.Item2, checks.Item3 }.All(c => c.Kind != "bad");
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Package layout ─────────────────────────────────────────────────
    public PackageLayoutViewModel Layout { get; } = new();

    // ── Summary and save ───────────────────────────────────────────────
    public IReadOnlyList<SummaryRow> SummaryRows { get; private set; } = [];

    private string _saveError = "";
    public string SaveError
    {
        get => _saveError;
        private set { if (Set(ref _saveError, value)) OnPropertyChanged(nameof(HasSaveError)); }
    }
    public bool HasSaveError => SaveError.Length > 0;

    private void BuildSummary()
    {
        var connection = IsInteractive ? "Interactive sign-in" : "App registration";
        var certificate = AvailableCertificates.FirstOrDefault(c => c.Thumbprint.Equals(Thumbprint, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? Thumbprint;
        var certificateText = certificate.Length > 0 ? $", certificate {certificate}" : ", no certificate chosen";
        var tested = ConnectionOk ? "Connection tested" : "Connect later: packaging works offline";
        var folders = PsadtLayout.OptionalFolders(Layout.IconFolder, Layout.ExtraFolders);

        SummaryRows =
        [
            new("Connection", IsInteractive
                ? $"{connection}. {tested}."
                : $"{connection}{certificateText}. {tested}.", ConnectionStep),
            new("Packages folder", ApplicationsRoot.Trim(), PathsStep),
            new("PSADT template", TemplatePath.Trim(), PathsStep),
            new("IntuneWinAppUtil", IntuneWinAppUtilPath.Trim(), PathsStep),
            new("Each new package", string.Join(", ", new[] { PsadtLayout.ApplicationFolderName, PsadtLayout.IntuneFolderName }.Concat(folders)), LayoutStep),
        ];
        OnPropertyChanged(nameof(SummaryRows));
    }

    /// <summary>Writes only the blocks setup owns. A failed save stays on the page with the reason.</summary>
    private void Finish()
    {
        var s = _svc.Settings;
        s.AuthMode = IsInteractive ? AuthMode.Interactive : AuthMode.AppRegistration;
        // As typed: sign-in trims them, and an unedited setup must write back exactly what it read.
        s.Authentication.TenantId = TenantId;
        s.Authentication.ClientId = ClientId;
        s.Authentication.CertificateThumbprint = Thumbprint;
        s.NetworkPaths.IntuneApplications = ApplicationsRoot.Trim();
        s.NetworkPaths.PSADTTemplate = TemplatePath.Trim();
        s.NetworkPaths.IntuneWinAppUtil = IntuneWinAppUtilPath.Trim();
        s.PackageLayout = Layout.ToConfig();

        try
        {
            _svc.Save();
        }
        catch (Exception ex)
        {
            SaveError = $"Settings could not be saved: {ex.Message}";
            return;
        }
        SaveError = "";
        Completed?.Invoke();
    }

    /// <summary>What Next runs: validates the step, then moves on or, on the summary, saves.</summary>
    public async Task NextAsync()
    {
        if (!CanNext) return;

        if (CurrentStepIndex == PathsStep && !await CheckPathsAsync()) return;
        if (CurrentStepIndex == SummaryStep)
        {
            Finish();
            return;
        }
        CurrentStepIndex++;
    }

    public OnboardingViewModel(SettingsService svc, IntuneAuthService auth)
    {
        _svc = svc;
        _auth = auth;

        Steps = new ObservableCollection<StepViewModel>
        {
            new(WelcomeStep, "Welcome"),
            new(ConnectionStep, "Connection"),
            new(PathsStep, "Paths"),
            new(LayoutStep, "Package layout"),
            new(SummaryStep, "Summary") { IsLast = true },
        };
        Steps[WelcomeStep].IsCurrent = true;
        foreach (var step in Steps.Skip(1)) step.LockReason = "Use Next to reach this step.";

        NextCommand = new AsyncRelayCommand(NextAsync, () => CanNext);
        BackCommand = new RelayCommand(() => CurrentStepIndex--, () => !IsBusy && CurrentStepIndex > 0);
        GoToStepCommand = new RelayCommand<int>(i => CurrentStepIndex = i, i => !IsBusy && i < CurrentStepIndex);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsBusy);
        ConnectLaterCommand = new RelayCommand(() =>
        {
            ConnectLater = true;
            if (!ConnectionOk) SetConnection("neutral", "Connect later chosen. Packaging works offline; Intune pages ask you to sign in.");
        }, () => !IsBusy);
        RefreshCertificatesCommand = new RelayCommand(LoadCertificates);
        Layout.PropertyChanged += (_, _) => RaiseRail();
        Layout.ExtraFolders.CollectionChanged += (_, _) => RaiseRail();

        // First run and Run setup again are the same path: the saved settings, defaults when new.
        var s = svc.Settings;
        _isInteractive = s.AuthMode == AuthMode.Interactive;
        _tenantId = s.Authentication.TenantId;
        _clientId = s.Authentication.ClientId;
        _thumbprint = s.Authentication.CertificateThumbprint;
        _applicationsRoot = s.NetworkPaths.IntuneApplications;
        _templatePath = s.NetworkPaths.PSADTTemplate;
        _intuneWinAppUtilPath = s.NetworkPaths.IntuneWinAppUtil;
        Layout.Load(s.PackageLayout);
        LoadCertificates();
    }
}
