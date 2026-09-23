using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace Packman.ViewModels;

/// <summary>
/// The standalone "Upload to Intune" page: pick a built package, set its deploy mode,
/// detection rules, requirements, return codes and assignment groups, and publish.
/// </summary>
public sealed class UploadToIntuneViewModel : ObservableObject
{
    private readonly SettingsService _settings = AppServices.Settings;
    private readonly IntuneAuthService _auth = AppServices.Auth;

    private static readonly string[] NewRuleDependents =
    [
        nameof(IsFileMethod), nameof(IsFileVersionMethod), nameof(IsRegistryMethod),
        nameof(IsMsiMethod), nameof(HasNoMsiProductCode),
    ];

    public UploadToIntuneViewModel()
    {
        AddRuleCommand = new RelayCommand(AddDetectionRule, () => CanAddRule);
        RemoveRuleCommand = new RelayCommand<DetectionRule>(r => { if (r != null) DetectionRules.Remove(r); });
        UploadCommand = new AsyncRelayCommand(UploadAsync, () => UploadEnabled);
        AddReturnCodeCommand = new RelayCommand(AddReturnCode);
        RestoreDefaultsCommand = new RelayCommand(ApplyIntuneDefaults);
        ApplyIntuneDefaults();

        DetectionRules.CollectionChanged += (_, _) =>
            RaiseAll(nameof(HasDetectionRule), nameof(DetectionState), nameof(DetectionDetail));
        GroupPicker.SelectedGroups.CollectionChanged += (_, _) =>
            RaiseAll(nameof(HasAssignment), nameof(AssignmentState), nameof(AssignmentDetail));
        _auth.StateChanged += RaiseConnection;

        Publish.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PublishRunViewModel.IsPublishing))
                UploadCommand.RaiseCanExecuteChanged();
            if (e.PropertyName is nameof(PublishRunViewModel.IsPublishing) or nameof(PublishRunViewModel.StatusText))
                OnPropertyChanged(nameof(FooterText));
        };
        Publish.Dismissed += succeeded =>
        {
            if (succeeded) ClearForm();
            UploadCommand.RaiseCanExecuteChanged();
        };
    }

    // ── Sign-in state ───────────────────────────────────
    public bool IsSignedIn => _auth.IsSignedIn;
    public bool IsNotSignedIn => !_auth.IsSignedIn;
    public string SignedInUser => _auth.SignedInUser ?? "";
    public string TenantName => _auth.TenantName;
    public string ConnectionLabel => _auth.IsSignedIn ? "Connected" : "Not connected";
    public string ConnectionDetail => _auth.IsSignedIn ? _auth.TenantName : "";

    /// <summary>Signed in, or app-registration mode, which connects on its own when the publish starts.</summary>
    public bool CanAttemptPublish => _auth.IsSignedIn || _settings.Settings.AuthMode == AuthMode.AppRegistration;
    public bool ShowSignInCallout => !CanAttemptPublish;

    /// <summary>Refreshes sign-in and settings dependent text.</summary>
    public void Refresh()
    {
        RaiseConnection();
        RaiseAll(nameof(InstallCommandPreview), nameof(UninstallCommandPreview), nameof(CommandsReady), nameof(CommandsState));
        UploadCommand.RaiseCanExecuteChanged();
    }

    // Property changes only: the auth service can raise this off the UI thread.
    private void RaiseConnection() => RaiseAll(
        nameof(IsSignedIn), nameof(IsNotSignedIn), nameof(SignedInUser), nameof(TenantName),
        nameof(ConnectionLabel), nameof(ConnectionDetail), nameof(CanAttemptPublish), nameof(ShowSignInCallout));

    /// <summary>Centre of the footer: the publish status while publishing, else the last problem, else the idle note.</summary>
    public string FooterText =>
        Publish.IsPublishing ? Publish.StatusText
        : HasValidationError ? ValidationError
        : "Nothing has been uploaded yet.";

    // ── Package selection ───────────────────────────────
    private string _packageRoot = "";
    public string PackageRoot { get => _packageRoot; private set => Set(ref _packageRoot, value); }

    private string _packageFolderName = "";
    public string PackageFolderName { get => _packageFolderName; private set => Set(ref _packageFolderName, value); }

    private bool _isValidated;
    public bool IsValidated
    {
        get => _isValidated;
        private set { if (Set(ref _isValidated, value)) UploadCommand.RaiseCanExecuteChanged(); }
    }

    private string _validationError = "";
    public string ValidationError
    {
        get => _validationError;
        private set => Set(ref _validationError, value, [nameof(HasValidationError), nameof(FooterText)]);
    }
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    private string _appName = "";
    public string AppName { get => _appName; private set => Set(ref _appName, value, [nameof(DisplayTitle)]); }

    private string _manufacturer = "";
    public string Manufacturer { get => _manufacturer; private set => Set(ref _manufacturer, value, [nameof(DisplayTitle)]); }

    private string _version = "";
    public string Version { get => _version; private set => Set(ref _version, value); }

    private string _installContext = "System";
    public string InstallContext { get => _installContext; private set => Set(ref _installContext, value); }

    // AppArch from the script; empty when it has none, which lets the upload allow every architecture.
    private string _architecture = "";

    private string _packageMeta = "";
    /// <summary>"version · arch · context" for the identity tile.</summary>
    public string PackageMeta { get => _packageMeta; private set => Set(ref _packageMeta, value); }

    private string _packageIconPath = "";
    /// <summary>First file in the package's Icon folder, if any.</summary>
    public string PackageIconPath { get => _packageIconPath; private set => Set(ref _packageIconPath, value); }

    private string _sizeText = "";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    public string DisplayTitle => $"{Manufacturer} {AppName}".Trim();

    private string _intuneDisplayName = "";
    /// <summary>Title in Intune. Seeded from the package metadata, editable before upload.</summary>
    public string IntuneDisplayName { get => _intuneDisplayName; set => Set(ref _intuneDisplayName, value); }

    /// <summary>
    /// Validates a folder as a v4 package and pulls metadata, install context and an
    /// MSI detection rule out of it.
    /// </summary>
    public void ProcessSelectedFolder(string selectedPath)
    {
        ValidationError = "";
        try
        {
            var root = FolderBrowserHelper.GetPackageRootPath(selectedPath);
            if (!FolderBrowserHelper.ValidatePackageStructure(root))
            {
                Fail($"{PsadtLayout.SetupFileName} not found. Select the package folder that contains the Application folder.");
                return;
            }

            var scriptPath = PsadtScript.Find(root);
            if (scriptPath == null)
            {
                Fail($"{PsadtLayout.ScriptName} not found in the Application folder.");
                return;
            }

            var script = PsadtScript.Load(scriptPath);
            Manufacturer = script.Vendor;
            AppName = script.AppName;
            Version = script.AppVersion;
            InstallContext = script.InstallContext;
            _architecture = script.AppArch;
            PackageMeta = string.Join(" · ", new[] { Version, _architecture, InstallContext }.Where(v => !string.IsNullOrWhiteSpace(v)));

            var iconDir = Path.Combine(root, "Icon");
            PackageIconPath = Directory.Exists(iconDir) ? Directory.EnumerateFiles(iconDir).FirstOrDefault() ?? "" : "";

            // A different package starts from the saved defaults, as the wizard does.
            if (root != PackageRoot)
            {
                ApplyIntuneDefaults();
                SelectedDeployMode = PsadtLayout.DeployModeDefault;
            }

            IntuneDisplayName = GroupAssignmentNamer.Build(
                _settings.Settings.IntuneDefaults.DisplayNameTemplate, Manufacturer, AppName, Version);

            PackageRoot = root;
            PackageFolderName = new DirectoryInfo(root).Name;

            BuildDetectionRules(root);
            IsValidated = true;

            // The share can be slow; the size arrives when it arrives.
            SizeText = "…";
            ErrorReporter.FireAndForget(async () =>
            {
                var size = await Task.Run(() => DirectoryCopy.TotalSize(root));
                if (PackageRoot == root) SizeText = ByteSize.Format(size);
            });
        }
        catch (Exception ex)
        {
            Fail($"Could not read package: {ex.Message}");
        }
    }

    private void Fail(string message)
    {
        IsValidated = false;
        ValidationError = message;
    }

    // ── Detection rules ─────────────────────────────────
    public ObservableCollection<DetectionRule> DetectionRules { get; } = new();

    /// <summary>Product code of the staged MSI, used to pre-fill MSI detection.</summary>
    private string _msiProductCode = "";

    // An MSI package gets its product code as the rule: it is exact and needs no guessing
    // at an install path. Anything else starts empty; the packager adds a rule.
    private void BuildDetectionRules(string root)
    {
        DetectionRules.Clear();
        _msiProductCode = "";
        try
        {
            var filesFolder = Path.Combine(root, "Application", "Files");
            var msiFile = Directory.Exists(filesFolder)
                ? Directory.GetFiles(filesFolder, "*.msi", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
            if (msiFile != null)
            {
                var msi = MsiInfoService.ExtractMsiInfo(msiFile);
                if (msi.IsValid && !string.IsNullOrEmpty(msi.ProductCode))
                {
                    _msiProductCode = msi.ProductCode;
                    DetectionRules.Add(DetectionRuleFactory.Msi(msi.ProductCode));
                }
            }
        }
        catch { /* leave the list empty; the packager adds a rule by hand */ }

        NewRuleProductCode = _msiProductCode;
        OnPropertyChanged(nameof(HasNoMsiProductCode));
    }

    public List<string> DetectionMethods { get; } = DetectionMethod.All;
    public IReadOnlyList<string> RegistryHives { get; } = RegistryHiveNames.All;
    public ObservableCollection<string> Operators { get; } =
        new() { "greaterThanOrEqual", "equal", "greaterThan", "lessThan", "lessThanOrEqual" };

    private string _newRuleMethod = DetectionMethod.FileExists;
    public string NewRuleMethod
    {
        get => _newRuleMethod;
        set
        {
            if (!Set(ref _newRuleMethod, value)) return;
            if (IsMsiMethod && string.IsNullOrWhiteSpace(_newRuleProductCode))
                NewRuleProductCode = _msiProductCode;
            RaiseAll(NewRuleDependents);
            AddRuleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsFileMethod => _newRuleMethod is DetectionMethod.FileExists or DetectionMethod.FileVersion;
    public bool IsFileVersionMethod => _newRuleMethod == DetectionMethod.FileVersion;
    public bool IsRegistryMethod => _newRuleMethod == DetectionMethod.RegistryKey;
    public bool IsMsiMethod => _newRuleMethod == DetectionMethod.MsiProductCode;

    /// <summary>MSI detection selected but no product code was found.</summary>
    public bool HasNoMsiProductCode => IsMsiMethod && string.IsNullOrWhiteSpace(_msiProductCode);

    private string _newRulePath = "";
    public string NewRulePath { get => _newRulePath; set { if (Set(ref _newRulePath, value)) AddRuleCommand.RaiseCanExecuteChanged(); } }

    private string _newRuleName = "";
    public string NewRuleName { get => _newRuleName; set => Set(ref _newRuleName, value); }

    private string _newRuleOperator = DetectionRuleFactory.DefaultVersionOperator;
    public string NewRuleOperator { get => _newRuleOperator; set => Set(ref _newRuleOperator, value); }

    private string _newRuleValue = "";
    public string NewRuleValue { get => _newRuleValue; set => Set(ref _newRuleValue, value); }

    private string _newRuleHive = RegistryHiveNames.LocalMachine;
    public string NewRuleHive { get => _newRuleHive; set => Set(ref _newRuleHive, value); }

    private string _newRuleKeyPath = "";
    public string NewRuleKeyPath { get => _newRuleKeyPath; set { if (Set(ref _newRuleKeyPath, value)) AddRuleCommand.RaiseCanExecuteChanged(); } }

    private string _newRuleValueName = "";
    public string NewRuleValueName { get => _newRuleValueName; set => Set(ref _newRuleValueName, value); }

    private string _newRuleProductCode = "";
    public string NewRuleProductCode
    {
        get => _newRuleProductCode;
        set { if (Set(ref _newRuleProductCode, value)) AddRuleCommand.RaiseCanExecuteChanged(); }
    }

    public bool CanAddRule => _newRuleMethod switch
    {
        DetectionMethod.RegistryKey => !string.IsNullOrWhiteSpace(NewRuleKeyPath),
        DetectionMethod.MsiProductCode => !string.IsNullOrWhiteSpace(NewRuleProductCode),
        _ => !string.IsNullOrWhiteSpace(NewRulePath),
    };

    private void AddDetectionRule()
    {
        var rule = DetectionRuleFactory.FromMethod(
            _newRuleMethod,
            NewRulePath, NewRuleName, NewRuleValue, NewRuleOperator,
            NewRuleHive, NewRuleKeyPath, NewRuleValueName,
            NewRuleProductCode);
        if (rule == null) return;

        DetectionRules.Add(rule);
        switch (_newRuleMethod)
        {
            case DetectionMethod.RegistryKey:
                NewRuleKeyPath = "";
                NewRuleValueName = "";
                break;
            case DetectionMethod.MsiProductCode:
                break;
            default:
                NewRulePath = "";
                NewRuleName = "";
                NewRuleValue = "";
                break;
        }
    }

    public bool HasDetectionRule => DetectionRules.Count > 0;
    public string DetectionState => HasDetectionRule ? "Ready" : "Incomplete";
    public string DetectionDetail => DetectionRules.Count switch
    {
        0 => "Add a detection rule",
        1 => "1 rule",
        var n => $"{n} rules",
    };

    // ── Install behavior ────────────────────────────────
    public IReadOnlyList<string> DeployModes => PsadtLayout.DeployModes;

    private string _selectedDeployMode = PsadtLayout.DeployModeDefault;
    /// <summary>Deploy mode baked into the install and uninstall command lines.</summary>
    public string SelectedDeployMode
    {
        get => _selectedDeployMode;
        set => Set(ref _selectedDeployMode, value, [nameof(DeployModeHint), nameof(InstallCommandPreview), nameof(UninstallCommandPreview)]);
    }

    public string DeployModeHint => _selectedDeployMode switch
    {
        "Interactive" => "For attended testing. Intune deployments must not require user interaction.",
        "NonInteractive" => "Does not wait for user input; PSADT may show progress when a user session is available.",
        "Silent" => "No dialogs at all.",
        _ => "PSADT selects the mode from the session and toolkit configuration. Use Silent for unattended Intune deployment.",
    };

    /// <summary>The command lines the upload sends: Settings ▸ Intune Defaults with the deploy mode applied.</summary>
    public string InstallCommandPreview => PsadtLayout.WithDeployMode(_settings.Settings.IntuneDefaults.InstallCommand, _selectedDeployMode);
    public string UninstallCommandPreview => PsadtLayout.WithDeployMode(_settings.Settings.IntuneDefaults.UninstallCommand, _selectedDeployMode);

    public bool CommandsReady => !string.IsNullOrWhiteSpace(_settings.Settings.IntuneDefaults.InstallCommand)
                              && !string.IsNullOrWhiteSpace(_settings.Settings.IntuneDefaults.UninstallCommand);
    public string CommandsState => CommandsReady ? "Ready" : "Set in Settings";

    // ── Requirements & return codes (seeded from Settings ▸ Intune Defaults) ──
    public IReadOnlyList<string> OperatingSystems { get; } = RequirementInfo.SupportedOperatingSystems;

    private string _selectedOperatingSystem = RequirementInfo.SupportedOperatingSystems[0];
    public string SelectedOperatingSystem { get => _selectedOperatingSystem; set => Set(ref _selectedOperatingSystem, value); }

    private string _minFreeDiskSpaceMB = "";
    public string MinFreeDiskSpaceMB { get => _minFreeDiskSpaceMB; set => Set(ref _minFreeDiskSpaceMB, value); }

    private string _minMemoryMB = "";
    public string MinMemoryMB { get => _minMemoryMB; set => Set(ref _minMemoryMB, value); }

    private string _minProcessors = "";
    public string MinProcessors { get => _minProcessors; set => Set(ref _minProcessors, value); }

    private string _minCpuSpeedMHz = "";
    public string MinCpuSpeedMHz { get => _minCpuSpeedMHz; set => Set(ref _minCpuSpeedMHz, value); }

    private string _newReturnCodeInput = "";
    public string NewReturnCodeInput { get => _newReturnCodeInput; set => Set(ref _newReturnCodeInput, value); }

    public ObservableCollection<ReturnCodeRow> ReturnCodes { get; } = new();

    /// <summary>Re-seeds requirements and return codes from the saved defaults.</summary>
    private void ApplyIntuneDefaults()
    {
        var defaults = _settings.Settings.IntuneDefaults;
        var req = defaults.Requirements;
        SelectedOperatingSystem = req.MinimumOperatingSystem;
        MinFreeDiskSpaceMB = req.MinimumFreeDiskSpaceMB?.ToString() ?? "";
        MinMemoryMB = req.MinimumMemoryMB?.ToString() ?? "";
        MinProcessors = req.MinimumNumberOfProcessors?.ToString() ?? "";
        MinCpuSpeedMHz = req.MinimumCpuSpeedMHz?.ToString() ?? "";

        ReturnCodes.Clear();
        foreach (var c in defaults.ReturnCodes)
            ReturnCodes.Add(new ReturnCodeRow(c.Code, c.Type, c.Description, r => ReturnCodes.Remove(r)));
    }

    private void AddReturnCode()
    {
        if (!int.TryParse(NewReturnCodeInput.Trim(), out var code)) return;
        if (ReturnCodes.Any(r => r.Code == code.ToString())) return;
        ReturnCodes.Add(new ReturnCodeRow(code, ReturnCodeType.Success, "", r => ReturnCodes.Remove(r)));
        NewReturnCodeInput = "";
    }

    // ── Assignment groups ───────────────────────────────
    /// <summary>Shared group picker; each group carries its own intent.</summary>
    public GroupPickerViewModel GroupPicker { get; } = new();

    public bool HasAssignment => GroupPicker.SelectedGroups.Count > 0;
    public string AssignmentState => GroupPicker.SelectedGroups.Count switch
    {
        0 => "Unassigned",
        1 => "1 group",
        var n => $"{n} groups",
    };
    public string AssignmentDetail => HasAssignment ? "" : "Publishes without an assignment.";

    // ── Publishing ──────────────────────────────────────
    /// <summary>The "Publishing…" overlay: steps, progress, cancel and done.</summary>
    public PublishRunViewModel Publish { get; } = new();

    public bool UploadEnabled =>
        IsValidated && CanAttemptPublish && !Publish.IsPublishing &&
        !string.IsNullOrWhiteSpace(_settings.Settings.NetworkPaths.IntuneWinAppUtil);

    private async Task UploadAsync()
    {
        if (!UploadEnabled) return;

        if (DetectionRules.Count == 0)
        {
            ValidationError = "Add at least one detection rule before publishing; Intune would accept the app and never detect it.";
            return;
        }

        var invalidCode = ReturnCodes.FirstOrDefault(r => r.ToInfo() == null);
        if (invalidCode != null)
        {
            ValidationError = $"Return code '{invalidCode.Code}' is not a number.";
            return;
        }
        ValidationError = "";

        var settings = _settings.Settings;
        if (!_auth.IsSignedIn && settings.AuthMode == AuthMode.AppRegistration)
        {
            try
            {
                await _auth.SignInAsync(AuthMode.AppRegistration, settings.Authentication, nint.Zero);
            }
            catch (Exception ex)
            {
                ValidationError = $"Certificate connection failed: {ex.Message}";
                return;
            }
        }
        var appInfo = new ApplicationInfo
        {
            Name = AppName.Trim(),
            Manufacturer = string.IsNullOrWhiteSpace(Manufacturer) ? "Unknown" : Manufacturer.Trim(),
            Version = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim(),
            SourcesPath = PackageRoot,
            InstallContext = InstallContext,
            Architecture = _architecture,
            DisplayName = IntuneDisplayName,
        };

        NativeCodeSigner? signer = settings.CodeSigning.Enabled
            ? new NativeCodeSigner(settings.CodeSigning.CertificateThumbprint, settings.CodeSigning.TimestampServer)
            : null;

        var uploadService = new IntuneUploadService(_auth.CreateSessionTokenProvider(), signer, settings.NetworkPaths.IntuneWinAppUtil);
        var packageRoot = PackageRoot;
        var rules = DetectionRules.ToList();
        var groups = GroupPicker.AssignableGroups;
        var installCommand = InstallCommandPreview;
        var uninstallCommand = UninstallCommandPreview;
        var requirements = RequirementInfo.Parse(SelectedOperatingSystem, MinFreeDiskSpaceMB, MinMemoryMB, MinProcessors, MinCpuSpeedMHz);
        var returnCodes = ReturnCodes.Select(r => r.ToInfo()).OfType<ReturnCodeInfo>().ToList();

        await Publish.RunAsync(
            $"Publishing {DisplayTitle}…",
            TenantName,
            (progress, ct) => uploadService.UploadWin32ApplicationAsync(
                appInfo, packageRoot, rules,
                installCommand, uninstallCommand,
                appInfo.DisplayName, appInfo.InstallContext, null, progress, null, null,
                requirements, returnCodes,
                settings.IntuneDefaults.PrivacyUrl, settings.IntuneDefaults.InformationUrl,
                groups, settings.IntuneDefaults.RestartBehavior, settings.IntuneDefaults.MaxRunTimeMinutes, ct),
            appId => groups.Count > 0
                ? $"Published and assigned to {groups.Count} group(s). App ID {appId}"
                : $"Published successfully. App ID {appId}",
            () => uploadService.RollbackSucceeded switch
            {
                true => "Upload cancelled. The partially created app was removed from Intune.",
                false => "Upload cancelled. The partially created app could not be removed; delete it in the Intune admin center.",
                null => "Upload cancelled. Check Intune for any app created before cancellation; no app ID was confirmed.",
            });
    }

    /// <summary>Clears every package-derived field for the next package.</summary>
    private void ClearForm()
    {
        IsValidated = false;
        ValidationError = "";
        PackageRoot = "";
        PackageFolderName = "";
        AppName = "";
        Manufacturer = "";
        Version = "";
        InstallContext = "System";
        _architecture = "";
        PackageMeta = "";
        PackageIconPath = "";
        SizeText = "";
        IntuneDisplayName = "";
        DetectionRules.Clear();
        GroupPicker.SelectedGroups.Clear();
        _msiProductCode = "";
        NewRuleMethod = DetectionMethod.FileExists;
        NewRulePath = NewRuleName = NewRuleValue = NewRuleKeyPath = NewRuleValueName = NewRuleProductCode = "";
        NewRuleHive = RegistryHiveNames.LocalMachine;
        NewRuleOperator = DetectionRuleFactory.DefaultVersionOperator;
        OnPropertyChanged(nameof(HasNoMsiProductCode));
        ApplyIntuneDefaults();
        SelectedDeployMode = PsadtLayout.DeployModeDefault;
    }

    // ── Commands ────────────────────────────────────────
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand<DetectionRule> RemoveRuleCommand { get; }
    public AsyncRelayCommand UploadCommand { get; }
    public RelayCommand AddReturnCodeCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
}
