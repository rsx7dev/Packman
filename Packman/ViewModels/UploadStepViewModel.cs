using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace Packman.ViewModels;

/// <summary>
/// The wizard's Upload and Review steps: detection, requirements, return codes and groups
/// for the package Create/Upgrade produced, then the publish itself.
/// </summary>
public class UploadStepViewModel : ObservableObject
{
    private readonly CreatePackageViewModel _create;
    private readonly SettingsService _settingsService;
    private readonly IntuneAuthService _auth;

    private string _appSummaryName = "";
    private string _appSummaryDetail = "";
    private string _detectionSummary = "";
    private string _statusText = "";

    private string _selectedDetectionMethod = DetectionMethod.FileExists;
    private string _detectionPath = "";
    private string _detectionName = "";
    private string _detectionValue = "";
    private string _selectedRegistryHive = RegistryHiveNames.LocalMachine;
    private string _registryKeyPath = "";
    private string _registryValueName = "";
    private string _detectionProductCode = "";

    private string _selectedOperatingSystem = "Windows 10 1607";
    private string _minFreeDiskSpaceMB = "";
    private string _minMemoryMB = "";
    private string _minProcessors = "";
    private string _minCpuSpeedMHz = "";
    private string _newReturnCodeInput = "";
    private string _defaultsAppliedFor = "";
    private string _groupsSeededFor = "";

    /// <summary>In-flight group seeding. Awaited before publishing.</summary>
    private Task? _seeding;

    private string _selectedDeployMode = DeployModeDefault;

    private string _reviewName = "";
    private string _reviewVendor = "";
    private string _reviewVersion = "";
    private string _reviewAuthor = "";
    private string _reviewArchitecture = "";
    private string _reviewContext = "";
    private string _reviewPackageType = "";
    private string _reviewSize = "";

    private static readonly string[] DetectionDependents =
    [
        nameof(IsFileDetection), nameof(IsFileVersionDetection), nameof(IsRegistryDetection),
        nameof(IsMsiDetection), nameof(HasNoMsiProductCode),
    ];

    public UploadStepViewModel(CreatePackageViewModel create, SettingsService settingsService, IntuneAuthService auth)
    {
        _create = create;
        _settingsService = settingsService;
        _auth = auth;

        GroupPicker.SelectedGroups.CollectionChanged += (_, _) =>
            RaiseAll(nameof(AssignmentCount), nameof(HasAssignment), nameof(AssignmentState), nameof(AssignmentDetail));
        _auth.StateChanged += RaiseReadiness;

        AddReturnCodeCommand = new RelayCommand(AddReturnCode);
        RestoreDefaultsCommand = new RelayCommand(ApplyIntuneDefaults);
        ApplyIntuneDefaults();

        // The overlay's status line doubles as this step's status line.
        Publish.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PublishRunViewModel.StatusText))
                StatusText = Publish.StatusText;
        };
    }

    public string AppSummaryName { get => _appSummaryName; private set => Set(ref _appSummaryName, value); }
    public string AppSummaryDetail { get => _appSummaryDetail; private set => Set(ref _appSummaryDetail, value); }
    public string DetectionSummary { get => _detectionSummary; private set => Set(ref _detectionSummary, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public bool IsSignedIn => _auth.IsSignedIn;
    public bool IsNotSignedIn => !_auth.IsSignedIn;
    public string SignedInUser => _auth.SignedInUser ?? "";
    public string TenantName => _auth.TenantName;

    /// <summary>
    /// Publishing can start: signed in, or app-registration mode, which connects on its own
    /// when the publish starts. Drives the disabled Publish button and the sign-in callout.
    /// </summary>
    public bool CanAttemptPublish => _auth.IsSignedIn || _settingsService.Settings.AuthMode == AuthMode.AppRegistration;
    public bool ShowSignInCallout => !CanAttemptPublish;

    // ── Readiness, for the rail beside Configure and Review ─────────────
    public bool CommandsReady => !string.IsNullOrWhiteSpace(_settingsService.Settings.IntuneDefaults.InstallCommand)
                              && !string.IsNullOrWhiteSpace(_settingsService.Settings.IntuneDefaults.UninstallCommand);
    public string CommandsState => CommandsReady ? "Ready" : "Set in Settings";

    public bool DetectionReady => DescribeDetectionProblem() == null;
    public string DetectionState => DetectionReady ? "Ready" : "Incomplete";
    public string DetectionIssue => DescribeDetectionProblem() ?? "";

    /// <summary>What the Detection line says about the chosen method, e.g. "MSI product code".</summary>
    public string DetectionMethodLabel => SelectedDetectionMethod;

    public int AssignmentCount => GroupPicker.SelectedGroups.Count;
    public bool HasAssignment => AssignmentCount > 0 || _settingsService.Settings.GroupAssignment.CreateGroupPerPackage;
    public string AssignmentState => AssignmentCount switch
    {
        0 when _settingsService.Settings.GroupAssignment.CreateGroupPerPackage => "Per-package group",
        0 => "Unassigned",
        1 => "1 group",
        var n => $"{n} groups",
    };
    public string AssignmentDetail => HasAssignment ? "" : "Publishes without an assignment. Add a group to deploy it.";

    /// <summary>Upgrade only: the Intune app this version replaces.</summary>
    public bool HasSupersedence => !string.IsNullOrEmpty(_create.PredecessorAppId);
    public string SupersedenceSummary => HasSupersedence
        ? $"Supersedes app {_create.PredecessorAppId} (update: the previous version is replaced, not uninstalled first)"
        : "";

    /// <summary>The "Publishing…" overlay: steps, progress, cancel and done.</summary>
    public PublishRunViewModel Publish { get; } = new();

    // ── Detection method ───────────────────────────────────────────────
    public List<string> DetectionMethods { get; } = DetectionMethod.All;
    public IReadOnlyList<string> RegistryHives { get; } = RegistryHiveNames.All;

    public string SelectedDetectionMethod
    {
        get => _selectedDetectionMethod;
        set
        {
            if (!Set(ref _selectedDetectionMethod, value)) return;
            if (IsMsiDetection && string.IsNullOrWhiteSpace(_detectionProductCode))
            {
                // Read the product code off the staged MSI.
                _detectionProductCode = FindMsiProductCode();
                OnPropertyChanged(nameof(DetectionProductCode));
            }
            RaiseAll(DetectionDependents);
            RefreshDetectionSummary();
        }
    }

    public bool IsFileDetection => _selectedDetectionMethod is DetectionMethod.FileExists or DetectionMethod.FileVersion;
    public bool IsFileVersionDetection => _selectedDetectionMethod == DetectionMethod.FileVersion;
    public bool IsRegistryDetection => _selectedDetectionMethod == DetectionMethod.RegistryKey;
    public bool IsMsiDetection => _selectedDetectionMethod == DetectionMethod.MsiProductCode;

    /// <summary>MSI detection selected but no product code was found.</summary>
    public bool HasNoMsiProductCode => IsMsiDetection && string.IsNullOrWhiteSpace(_detectionProductCode);

    public string DetectionPath
    {
        get => _detectionPath;
        set { if (Set(ref _detectionPath, value)) RefreshDetectionSummary(); }
    }

    public string DetectionName
    {
        get => _detectionName;
        set { if (Set(ref _detectionName, value)) RefreshDetectionSummary(); }
    }

    public string DetectionValue
    {
        get => _detectionValue;
        set { if (Set(ref _detectionValue, value)) RefreshDetectionSummary(); }
    }

    public string SelectedRegistryHive
    {
        get => _selectedRegistryHive;
        set { if (Set(ref _selectedRegistryHive, value)) RefreshDetectionSummary(); }
    }

    public string RegistryKeyPath
    {
        get => _registryKeyPath;
        set { if (Set(ref _registryKeyPath, value)) RefreshDetectionSummary(); }
    }

    public string RegistryValueName
    {
        get => _registryValueName;
        set { if (Set(ref _registryValueName, value)) RefreshDetectionSummary(); }
    }

    public string DetectionProductCode
    {
        get => _detectionProductCode;
        set
        {
            if (!Set(ref _detectionProductCode, value)) return;
            OnPropertyChanged(nameof(HasNoMsiProductCode));
            RefreshDetectionSummary();
        }
    }

    /// <summary>Takes a rule the Remote Test tool discovered on a device.</summary>
    public void ApplyDiscoveredRule(DetectionRule rule)
    {
        if (rule.Type != DetectionRuleType.File) return;

        _detectionPath = rule.Path;
        _detectionName = rule.FileOrFolderName;
        _detectionValue = rule.DetectionValue;
        _selectedDetectionMethod = rule.CheckVersion ? DetectionMethod.FileVersion : DetectionMethod.FileExists;
        RaiseAll(nameof(DetectionPath), nameof(DetectionName), nameof(DetectionValue), nameof(SelectedDetectionMethod));
        RaiseAll(DetectionDependents);
        RefreshDetectionSummary();
    }

    // ── Requirements & return codes (seeded from Settings ▸ Intune Defaults) ──
    public IReadOnlyList<string> OperatingSystems { get; } = RequirementInfo.SupportedOperatingSystems;

    public string SelectedOperatingSystem { get => _selectedOperatingSystem; set => Set(ref _selectedOperatingSystem, value); }
    public string MinFreeDiskSpaceMB { get => _minFreeDiskSpaceMB; set => Set(ref _minFreeDiskSpaceMB, value); }
    public string MinMemoryMB { get => _minMemoryMB; set => Set(ref _minMemoryMB, value); }
    public string MinProcessors { get => _minProcessors; set => Set(ref _minProcessors, value); }
    public string MinCpuSpeedMHz { get => _minCpuSpeedMHz; set => Set(ref _minCpuSpeedMHz, value); }
    public string NewReturnCodeInput { get => _newReturnCodeInput; set => Set(ref _newReturnCodeInput, value); }

    public ObservableCollection<ReturnCodeRow> ReturnCodes { get; } = new();

    public RelayCommand AddReturnCodeCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }

    /// <summary>Re-seeds requirements and return codes from the saved defaults.</summary>
    private void ApplyIntuneDefaults()
    {
        var defaults = _settingsService.Settings.IntuneDefaults;
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

    // ── Deploy mode ────────────────────────────────────────────────────
    public const string DeployModeDefault = PsadtLayout.DeployModeDefault;

    public IReadOnlyList<string> DeployModes => PsadtLayout.DeployModes;

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

    public string InstallCommandPreview => PsadtLayout.WithDeployMode(_settingsService.Settings.IntuneDefaults.InstallCommand, _selectedDeployMode);
    public string UninstallCommandPreview => PsadtLayout.WithDeployMode(_settingsService.Settings.IntuneDefaults.UninstallCommand, _selectedDeployMode);

    // ── Assignment groups ──────────────────────────────────────────────
    /// <summary>Seeded from Settings, then editable for this package.</summary>
    public GroupPickerViewModel GroupPicker { get; } = new();

    // ── Review ─────────────────────────────────────────────────────────
    public string ReviewName { get => _reviewName; private set => Set(ref _reviewName, value); }
    public string ReviewVendor { get => _reviewVendor; private set => Set(ref _reviewVendor, value); }
    public string ReviewVersion { get => _reviewVersion; private set => Set(ref _reviewVersion, value); }
    public string ReviewAuthor { get => _reviewAuthor; private set => Set(ref _reviewAuthor, value); }
    public string ReviewArchitecture { get => _reviewArchitecture; private set => Set(ref _reviewArchitecture, value); }
    public string ReviewContext { get => _reviewContext; private set => Set(ref _reviewContext, value); }
    public string ReviewPackageType { get => _reviewPackageType; private set => Set(ref _reviewPackageType, value); }
    public string ReviewSize { get => _reviewSize; private set => Set(ref _reviewSize, value); }

    private string _intuneDisplayName = "";
    /// <summary>Title in Intune. Seeded from the package metadata, editable before upload.</summary>
    public string IntuneDisplayName { get => _intuneDisplayName; set => Set(ref _intuneDisplayName, value); }

    /// <summary>Refreshes the summary from the package produced earlier in the wizard.</summary>
    public void RefreshFromPackage()
    {
        RaiseReadiness();

        if (string.IsNullOrEmpty(_create.CurrentPackagePath))
        {
            AppSummaryName = "No package generated yet";
            AppSummaryDetail = "Complete the Generate step first.";
            DetectionSummary = "";
            return;
        }

        var isNewPackage = _defaultsAppliedFor != _create.CurrentPackagePath;
        if (isNewPackage)
        {
            ApplyIntuneDefaults();
            SelectedDeployMode = DeployModeDefault;
            _defaultsAppliedFor = _create.CurrentPackagePath;
        }

        // Seeding needs a sign-in; when the user arrives signed out and signs in later,
        // the defaults are still owed to this package.
        if (_groupsSeededFor != _create.CurrentPackagePath && (isNewPackage || _auth.IsSignedIn))
            _seeding = SeedGroupsAsync(_create.CurrentPackagePath);

        var appInfo = _create.BuildApplicationInfo();
        if (isNewPackage)
            IntuneDisplayName = GroupAssignmentNamer.Build(
                _settingsService.Settings.IntuneDefaults.DisplayNameTemplate,
                appInfo.Manufacturer, appInfo.Name, appInfo.Version);
        AppSummaryName = $"{appInfo.Manufacturer} {appInfo.Name}".Trim();
        AppSummaryDetail = $"v{appInfo.Version} · {appInfo.InstallContext} context · Win32";

        // New package only: re-seeding would discard edits made before stepping back.
        if (isNewPackage)
            SeedDetectionFromPackage(appInfo);

        RefreshDetectionSummary();

        // Review panel.
        ReviewName = appInfo.Name;
        ReviewVendor = appInfo.Manufacturer;
        ReviewVersion = appInfo.Version;
        ReviewAuthor = string.IsNullOrWhiteSpace(appInfo.Author) ? Environment.UserName : appInfo.Author;
        ReviewArchitecture = appInfo.Architecture;
        ReviewContext = appInfo.InstallContext;
        ReviewPackageType = appInfo.PackageType;
        RaiseAll(nameof(InstallCommandPreview), nameof(UninstallCommandPreview));

        // The share can be slow; the size arrives when it arrives.
        ReviewSize = "…";
        var packagePath = _create.CurrentPackagePath;
        ErrorReporter.FireAndForget(async () =>
        {
            var size = await Task.Run(() => GetSourceSizeBytes(packagePath));
            if (_create.CurrentPackagePath == packagePath) ReviewSize = ByteSize.Format(size);
        });
    }

    private async Task SeedGroupsAsync(string packagePath)
    {
        if (await GroupPicker.SeedFromSettingsAsync(_settingsService.Settings.GroupAssignment))
            _groupsSeededFor = packagePath;
    }

    public string PerPackageGroupsSummary
    {
        get
        {
            var config = _settingsService.Settings.GroupAssignment;
            var app = _create.BuildApplicationInfo();
            var groups = new List<string>();
            if (config.CreateGroupPerPackage)
                groups.Add($"{GroupAssignmentNamer.Build(config.GroupNameTemplate, app.Manufacturer, app.Name, app.Version)} · {config.NewGroupIntent}");
            if (config.CreateUninstallGroupPerPackage)
                groups.Add($"{GroupAssignmentNamer.Build(config.UninstallGroupNameTemplate, app.Manufacturer, app.Name, app.Version)} · Uninstall");
            return groups.Count == 0 ? "No per-package groups will be created." : "Create or reuse on publish:\n" + string.Join("\n", groups);
        }
    }

    public string RequirementsSummary =>
        $"Disk: {Constraint(MinFreeDiskSpaceMB, "MB")} · Memory: {Constraint(MinMemoryMB, "MB")} · " +
        $"Processors: {Constraint(MinProcessors, "")} · CPU: {Constraint(MinCpuSpeedMHz, "MHz")}\n" +
        $"Restart: {RestartBehaviorLabel} · Max install time: {_settingsService.Settings.IntuneDefaults.MaxRunTimeMinutes} min (Settings ▸ Intune Defaults)";

    private string RestartBehaviorLabel
    {
        get
        {
            var value = _settingsService.Settings.IntuneDefaults.RestartBehavior;
            return AppSettings.IntuneDefaultsConfig.RestartBehaviors.FirstOrDefault(o => o.Value == value)?.Label ?? value;
        }
    }

    private static string Constraint(string value, string unit) =>
        string.IsNullOrWhiteSpace(value) ? "No minimum" : $"{value} {unit}".Trim();

    /// <summary>Refreshes the Review step without touching the edited fields.</summary>
    public void RefreshReview()
    {
        RaiseReadiness();
        RaiseAll(nameof(InstallCommandPreview), nameof(UninstallCommandPreview), nameof(PerPackageGroupsSummary), nameof(RequirementsSummary));
        RefreshDetectionSummary();
    }

    private void RefreshDetectionSummary()
    {
        RaiseAll(nameof(DetectionReady), nameof(DetectionState), nameof(DetectionIssue), nameof(DetectionMethodLabel));
        if (string.IsNullOrEmpty(_create.CurrentPackagePath)) return;
        DetectionSummary = DescribeDetectionProblem() ?? (BuildSelectedDetectionRule()?.Title ?? "No detection rule");
    }

    /// <summary>Settings or sign-in changed underneath the wizard.</summary>
    public void RaiseReadiness() => RaiseAll(
        nameof(IsSignedIn), nameof(IsNotSignedIn), nameof(SignedInUser), nameof(TenantName),
        nameof(CanAttemptPublish), nameof(ShowSignInCallout), nameof(CommandsReady), nameof(CommandsState),
        nameof(AssignmentCount), nameof(HasAssignment), nameof(AssignmentState), nameof(AssignmentDetail),
        nameof(HasSupersedence), nameof(SupersedenceSummary));

    /// <summary>
    /// Why the detection settings can't produce a usable rule, or null when they can.
    /// Intune accepts an incomplete rule and then never detects the app.
    /// </summary>
    public string? DescribeDetectionProblem() => SelectedDetectionMethod switch
    {
        DetectionMethod.FileExists when Blank(DetectionPath) || Blank(DetectionName)
            => "Detection needs a path and a file or folder name.",
        DetectionMethod.FileVersion when Blank(DetectionPath) || Blank(DetectionName)
            => "Detection needs a path and a file name.",
        DetectionMethod.FileVersion when Blank(DetectionValue)
            => "Detection needs the version to compare against.",
        DetectionMethod.RegistryKey when Blank(RegistryKeyPath)
            => "Detection needs a registry key path.",
        DetectionMethod.MsiProductCode when Blank(DetectionProductCode)
            => "Detection needs an MSI product code.",
        _ => null,
    };

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private DetectionRule? BuildSelectedDetectionRule() => DetectionRuleFactory.FromMethod(
        SelectedDetectionMethod,
        DetectionPath, DetectionName, DetectionValue, null,
        SelectedRegistryHive, RegistryKeyPath, RegistryValueName,
        DetectionProductCode);

    /// <summary>Product code of the first MSI staged in the package, if any.</summary>
    private string FindMsiProductCode()
    {
        if (_create.CurrentMsiInfo?.IsValid == true)
            return _create.CurrentMsiInfo.ProductCode;

        try
        {
            var filesFolder = Path.Combine(_create.CurrentPackagePath, "Application", "Files");
            if (!Directory.Exists(filesFolder)) return "";
            var msi = Directory.GetFiles(filesFolder, "*.msi", SearchOption.TopDirectoryOnly).FirstOrDefault();
            return msi == null ? "" : MsiInfoService.ExtractMsiInfo(msi).ProductCode;
        }
        catch { return ""; }
    }

    private static long GetSourceSizeBytes(string packagePath)
    {
        try { return DirectoryCopy.TotalSize(Path.Combine(packagePath, "Application", "Files")); }
        catch { return 0; }
    }

    public async Task UploadAsync()
    {
        var packagePath = _create.CurrentPackagePath;
        if (string.IsNullOrEmpty(packagePath) || !Directory.Exists(packagePath))
        {
            StatusText = "Generate a package first.";
            return;
        }

        if (!_auth.IsSignedIn && _settingsService.Settings.AuthMode == AuthMode.AppRegistration)
        {
            try
            {
                await _auth.SignInAsync(AuthMode.AppRegistration, _settingsService.Settings.Authentication, nint.Zero);
            }
            catch (Exception ex)
            {
                StatusText = $"Certificate connection failed: {ex.Message}";
                return;
            }
        }

        if (!_auth.IsSignedIn)
        {
            StatusText = "Sign in to Intune on the Settings page first.";
            return;
        }

        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.NetworkPaths.IntuneWinAppUtil))
        {
            StatusText = "Set the IntuneWinAppUtil path on the Settings page first.";
            return;
        }

        var detectionProblem = DescribeDetectionProblem();
        if (detectionProblem != null)
        {
            StatusText = detectionProblem;
            return;
        }

        var invalidCode = ReturnCodes.FirstOrDefault(r => r.ToInfo() == null);
        if (invalidCode != null)
        {
            StatusText = $"Return code '{invalidCode.Code}' is not a number.";
            return;
        }

        // Wait for the background seeding so a fast click can't publish before the default
        // groups resolve. A failure there only means fewer groups, so it can't block.
        if (_seeding != null)
        {
            try { await _seeding; } catch { /* the picker already shows what resolved */ }
        }

        var appInfo = _create.BuildApplicationInfo();
        appInfo.DisplayName = IntuneDisplayName;

        // The picker already resolved the named groups, so drop them here. Both
        // per-package options still apply.
        var groupAssignment = settings.GroupAssignment.Clone();
        groupAssignment.ExistingGroups.Clear();

        var assignedGroups = GroupPicker.AssignableGroups;
        var detectionRules = new List<DetectionRule> { BuildSelectedDetectionRule()! };
        var requirements = RequirementInfo.Parse(SelectedOperatingSystem, MinFreeDiskSpaceMB, MinMemoryMB, MinProcessors, MinCpuSpeedMHz);
        var returnCodes = ReturnCodes.Select(r => r.ToInfo()).OfType<ReturnCodeInfo>().ToList();
        var installCommand = InstallCommandPreview;
        var uninstallCommand = UninstallCommandPreview;
        var iconPath = string.IsNullOrEmpty(_create.ExtractedIconPath) ? null : _create.ExtractedIconPath;
        var predecessorAppId = string.IsNullOrEmpty(_create.PredecessorAppId) ? null : _create.PredecessorAppId;

        NativeCodeSigner? signer = settings.CodeSigning.Enabled
            ? new NativeCodeSigner(settings.CodeSigning.CertificateThumbprint, settings.CodeSigning.TimestampServer)
            : null;

        var uploadService = new IntuneUploadService(_auth.CreateSessionTokenProvider(), signer, settings.NetworkPaths.IntuneWinAppUtil);

        await Publish.RunAsync(
            $"Publishing {appInfo.Manufacturer} {appInfo.Name}…".Trim(),
            TenantName,
            (progress, ct) => uploadService.UploadWin32ApplicationAsync(
                appInfo, packagePath, detectionRules, installCommand, uninstallCommand,
                appInfo.DisplayName, appInfo.InstallContext, iconPath, progress, predecessorAppId,
                groupAssignment, requirements, returnCodes,
                settings.IntuneDefaults.PrivacyUrl, settings.IntuneDefaults.InformationUrl,
                assignedGroups, settings.IntuneDefaults.RestartBehavior, settings.IntuneDefaults.MaxRunTimeMinutes, ct),
            appId => assignedGroups.Count > 0
                ? $"Published and assigned to {assignedGroups.Count} group(s). App ID {appId}"
                : $"Published successfully. App ID {appId}",
            () => uploadService.RollbackSucceeded switch
            {
                true => "Upload cancelled. The partially created app was removed from Intune.",
                false => "Upload cancelled. The partially created app could not be removed; delete it in the Intune admin center.",
                null => "Upload cancelled. Check Intune for any app created before cancellation; no app ID was confirmed.",
            });
    }

    /// <summary>
    /// Pre-fills detection from what the package actually carries. An MSI has a product
    /// code; anything else gets the version only, and the packager supplies the path.
    /// </summary>
    private void SeedDetectionFromPackage(ApplicationInfo appInfo)
    {
        // Everything from the previous package goes; otherwise an EXE that follows an MSI
        // would be published with the MSI's product code.
        _detectionPath = "";
        _detectionName = "";
        _detectionValue = "";
        _registryKeyPath = "";
        _registryValueName = "";
        _selectedRegistryHive = RegistryHiveNames.LocalMachine;
        _detectionProductCode = "";

        var productCode = FindMsiProductCode();
        if (!string.IsNullOrEmpty(productCode))
        {
            _detectionProductCode = productCode;
            _selectedDetectionMethod = DetectionMethod.MsiProductCode;
        }
        else
        {
            _detectionValue = appInfo.Version;
            _selectedDetectionMethod = string.IsNullOrWhiteSpace(appInfo.Version)
                ? DetectionMethod.FileExists
                : DetectionMethod.FileVersion;
        }

        RaiseAll(nameof(SelectedDetectionMethod), nameof(DetectionPath), nameof(DetectionName), nameof(DetectionValue),
                 nameof(RegistryKeyPath), nameof(RegistryValueName), nameof(SelectedRegistryHive), nameof(DetectionProductCode));
        RaiseAll(DetectionDependents);
    }
}
