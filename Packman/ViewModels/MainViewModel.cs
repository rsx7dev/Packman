using Packman.Helpers;
using Packman.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace Packman.ViewModels;

/// <summary>Which optional tool page is covering the wizard, if any.</summary>
public enum PackageTool { None, EditScript, RemoteTest }

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = AppServices.Settings;
    private readonly IntuneAuthService _auth = AppServices.Auth;

    public SettingsService SettingsService => _settingsService;

    public ObservableCollection<StepViewModel> Steps { get; }
    public CreatePackageViewModel CreatePackage { get; } = new();
    public UpgradePackageViewModel Upgrade { get; } = new();
    public UploadStepViewModel Upload { get; }
    public RemoteTestViewModel RemoteTest { get; }

    /// <summary>State of the in-app script editor (tabs, dirty files, search).</summary>
    public EditorSessionViewModel Editor { get; }

    private readonly IDialogService _dialogs = AppServices.Dialogs;

    private string _screenTitle = "Create Package";
    /// <summary>The page shown in the header and the window title.</summary>
    public string ScreenTitle { get => _screenTitle; set => Set(ref _screenTitle, value); }

    /// <summary>Footer stamps: the runtime the app is running on and its own version.</summary>
    public string RuntimeStamp => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string AppVersion => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private const int GenerateStep = 0;
    private const int UploadStep = 1;
    private const int ReviewStep = 2;

    private bool _isUpgradeMode;
    public bool IsUpgradeMode
    {
        get => _isUpgradeMode;
        set
        {
            if (!Set(ref _isUpgradeMode, value)) return;
            RaiseAll(nameof(PrimaryLabel), nameof(StepHint), nameof(ShowNoSupersedenceNote));
        }
    }

    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand PrimaryCommand { get; }
    public RelayCommand<int> GoToStepCommand { get; }
    public RelayCommand OpenEditToolCommand { get; }
    public RelayCommand OpenTestToolCommand { get; }
    public RelayCommand CloseToolCommand { get; }
    public RelayCommand ContinueToUploadCommand { get; }
    public RelayCommand OpenPackageFolderCommand { get; }

    /// <summary>Clears the wizard so a second package can be built without restarting.</summary>
    public AsyncRelayCommand NewPackageCommand { get; }

    private int _currentStepIndex;
    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        set
        {
            if (value < 0 || value >= Steps.Count) return;
            if (!Set(ref _currentStepIndex, value)) return;
            for (int i = 0; i < Steps.Count; i++)
            {
                Steps[i].IsCurrent = i == value;
                Steps[i].IsDone = i < value;
            }
            if (value == UploadStep) Upload.RefreshFromPackage();
            if (value == ReviewStep) Upload.RefreshReview();
            OnPropertyChanged(nameof(PrimaryLabel));
            OnPropertyChanged(nameof(StepHint));
            OnPropertyChanged(nameof(IsLastStep));
            OnPropertyChanged(nameof(StepPosition));
            OnPropertyChanged(nameof(ShowPrimaryKeyHint));
            BackCommand.RaiseCanExecuteChanged();
            PrimaryCommand.RaiseCanExecuteChanged();
        }
    }

    // ── Optional tool pages (Edit Script / Remote Test) ─────────────────
    private PackageTool _activeTool = PackageTool.None;
    public PackageTool ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (!Set(ref _activeTool, value)) return;
            OnPropertyChanged(nameof(IsWizard));
            OnPropertyChanged(nameof(IsToolOpen));
            OnPropertyChanged(nameof(IsEditToolOpen));
            OnPropertyChanged(nameof(IsTestToolOpen));
            OnPropertyChanged(nameof(ToolTitle));
            OnPropertyChanged(nameof(ToolSubtitle));
        }
    }

    public bool IsWizard => _activeTool == PackageTool.None;
    public bool IsToolOpen => _activeTool != PackageTool.None;

    public bool IsEditToolOpen => _activeTool == PackageTool.EditScript;
    public bool IsTestToolOpen => _activeTool == PackageTool.RemoteTest;

    public string ToolTitle => _activeTool switch
    {
        PackageTool.EditScript => "Edit script",
        PackageTool.RemoteTest => "Remote test",
        _ => "",
    };

    public string ToolSubtitle => _activeTool switch
    {
        PackageTool.EditScript => "Review and customize the generated PSADT deployment files",
        PackageTool.RemoteTest => "Test the generated package on a remote Windows device",
        _ => "",
    };


    // ── Package state surfaced to the Generate screen ───────────────────
    public bool HasPackage => !string.IsNullOrEmpty(CreatePackage.CurrentPackagePath);
    public bool CanEditPackageInputs => !HasPackage && !CreatePackage.IsGenerating && !Upgrade.IsBusy;

    /// <summary>Trailing folder name, for the tool breadcrumb.</summary>
    public string PackageName
    {
        get
        {
            var path = CreatePackage.CurrentPackagePath;
            return string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    /// <summary>An upgrade whose previous version has no recorded Intune app: it publishes as a new app.</summary>
    public bool ShowNoSupersedenceNote => IsUpgradeMode && HasPackage && !Upload.HasSupersedence;

    public string PackagePathShort => HasPackage ? CreatePackage.CurrentPackagePath : "no package yet";

    public string PrimaryLabel
    {
        get
        {
            if (CurrentStepIndex == GenerateStep)
            {
                if (HasPackage) return "Continue to configure";
                return IsUpgradeMode ? "Upgrade package" : "Generate package";
            }
            if (CurrentStepIndex == UploadStep) return "Continue to review";
            return "Publish to Intune";
        }
    }

    public string StepHint => CurrentStepIndex switch
    {
        GenerateStep => HasPackage ? "Package ready. Edit or test it before you configure." : "Creates files on your share. Nothing is uploaded yet.",
        UploadStep => "Configuration only. Nothing has been uploaded.",
        _ => "Publishing builds the .intunewin and creates the app and assignment in Microsoft Intune."
    };

    public bool IsLastStep => CurrentStepIndex == Steps.Count - 1;

    /// <summary>The ↵ hint hides while an upload is in flight.</summary>
    public bool ShowPrimaryKeyHint => !Upload.Publish.IsRunning;

    /// <summary>Swaps the primary action for a status line while an upload runs.</summary>
    public bool IsUploadRunning => Upload.Publish.IsRunning;

    public string StepPosition => $"step {CurrentStepIndex + 1} of {Steps.Count}";

    // ── Intune connection (title bar pill and rails) ────────────────────
    public bool IsConnected => _auth.IsSignedIn;
    public string ConnectionLabel => _auth.IsSignedIn ? "Connected" : "Not connected";
    public string TenantName => _auth.IsSignedIn ? _auth.TenantName : "Not signed in";
    public string ConnectionStatusText => _auth.IsSignedIn
        ? $"Connected to Microsoft Intune · {_auth.SignedInUser}"
        : "Not connected. Sign in on the Settings page.";

    // ── Package preview (rail beside the Package step) ──────────────────
    public bool OutputShareReady => !string.IsNullOrWhiteSpace(_settingsService.Settings.NetworkPaths.IntuneApplications);
    public bool TemplateReady => !string.IsNullOrWhiteSpace(_settingsService.Settings.NetworkPaths.PSADTTemplate);
    public string OutputShare => OutputShareReady ? _settingsService.Settings.NetworkPaths.IntuneApplications : "Not set";

    /// <summary>
    /// Where the package lands: the real folder once generated, otherwise the folder the
    /// current name and version resolve to on the output share.
    /// </summary>
    public string PackageFolderPreview
    {
        get
        {
            if (HasPackage) return CreatePackage.CurrentPackagePath;
            if (!OutputShareReady) return "Set the output share in Settings";
            if (string.IsNullOrWhiteSpace(CreatePackage.AppName)) return "Enter the application name";
            try
            {
                var app = CreatePackage.BuildApplicationInfo();
                return PackagePaths.VersionFolder(_settingsService.Settings.NetworkPaths.IntuneApplications,
                    PackagePaths.AppFolderName(app.Manufacturer, app.Name), app.Version);
            }
            catch (Exception)
            {
                return "Check the name and version";
            }
        }
    }

    /// <summary>Settings saved: paths and defaults the rails summarise may have changed.</summary>
    public void RefreshFromSettings()
    {
        RaiseAll(nameof(OutputShareReady), nameof(TemplateReady), nameof(OutputShare), nameof(PackageFolderPreview));
        Upload.RaiseReadiness();
        PrimaryCommand.RaiseCanExecuteChanged();
    }

    public MainViewModel()
    {
        Upload = new UploadStepViewModel(CreatePackage, _settingsService, _auth);
        RemoteTest = new RemoteTestViewModel(_settingsService, CreatePackage, hasPublishStep: true);
        Editor = new EditorSessionViewModel(CreatePackage, _dialogs);
        RemoteTest.ApplyDetectionRequested += Upload.ApplyDiscoveredRule;

        Steps = new ObservableCollection<StepViewModel>
        {
            new(GenerateStep, "Package"),
            new(UploadStep,   "Configure"),
            new(ReviewStep,   "Review & publish"),
        };

        BackCommand     = new RelayCommand(() => CurrentStepIndex--, () => CurrentStepIndex > 0);
        PrimaryCommand  = new AsyncRelayCommand(OnPrimaryAsync, () => !CreatePackage.IsGenerating && !Upgrade.IsBusy && !Upload.Publish.IsPublishing
                                                                    && (CurrentStepIndex != ReviewStep || Upload.CanAttemptPublish));
        GoToStepCommand = new RelayCommand<int>(i => CurrentStepIndex = i);

        OpenEditToolCommand      = new RelayCommand(() => ActiveTool = PackageTool.EditScript, () => HasPackage);
        OpenTestToolCommand      = new RelayCommand(() => ActiveTool = PackageTool.RemoteTest, () => HasPackage);
        CloseToolCommand         = new RelayCommand(() => ActiveTool = PackageTool.None);
        OpenPackageFolderCommand = new RelayCommand(OpenPackageFolder, () => HasPackage);
        NewPackageCommand        = new AsyncRelayCommand(StartNewPackageAsync, () => HasPackage && !Upload.Publish.IsRunning);

        ContinueToUploadCommand = new RelayCommand(() =>
        {
            ActiveTool = PackageTool.None;
            CurrentStepIndex = UploadStep;
        });

        Steps[GenerateStep].IsCurrent = true;

        _settingsService.Saved += RefreshFromSettings;

        _auth.StateChanged += () =>
        {
            RaiseAll(nameof(IsConnected), nameof(ConnectionLabel), nameof(TenantName), nameof(ConnectionStatusText));
            PrimaryCommand.RaiseCanExecuteChanged();
        };

        CreatePackage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreatePackageViewModel.IsGenerating))
            {
                PrimaryCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanEditPackageInputs));
            }
            if (e.PropertyName == nameof(CreatePackageViewModel.CurrentPackagePath))
                RaisePackageDependents();
            if (e.PropertyName is nameof(CreatePackageViewModel.AppName) or nameof(CreatePackageViewModel.Manufacturer)
                                or nameof(CreatePackageViewModel.Version))
                OnPropertyChanged(nameof(PackageFolderPreview));
        };
        Upgrade.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpgradePackageViewModel.IsBusy))
            {
                PrimaryCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanEditPackageInputs));
            }
        };
        Upload.Publish.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PublishRunViewModel.IsPublishing)
                              or nameof(PublishRunViewModel.IsRunning)
                              or nameof(PublishRunViewModel.IsComplete))
            {
                PrimaryCommand.RaiseCanExecuteChanged();
                NewPackageCommand.RaiseCanExecuteChanged();
                RaiseAll(nameof(ShowPrimaryKeyHint), nameof(IsUploadRunning));
            }
        };
    }

    private void RaisePackageDependents()
    {
        OnPropertyChanged(nameof(HasPackage));
        OnPropertyChanged(nameof(CanEditPackageInputs));
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(PackagePathShort));
        OnPropertyChanged(nameof(PackageFolderPreview));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(StepHint));
        OpenEditToolCommand.RaiseCanExecuteChanged();
        OpenTestToolCommand.RaiseCanExecuteChanged();
        OpenPackageFolderCommand.RaiseCanExecuteChanged();
        NewPackageCommand.RaiseCanExecuteChanged();
    }

    private async Task StartNewPackageAsync()
    {
        if (!await Editor.PromptSaveAllAsync()) return;

        ActiveTool = PackageTool.None;
        CreatePackage.Reset();
        Upgrade.Reset();
        CurrentStepIndex = GenerateStep;
        RaisePackageDependents();
    }

    private async Task OnPrimaryAsync()
    {
        if (CurrentStepIndex == GenerateStep)
        {
            // With a package present the button advances instead of regenerating.
            if (HasPackage)
            {
                CurrentStepIndex = UploadStep;
                return;
            }

            if (IsUpgradeMode)
                await RunUpgradeAsync();
            else
                await RunCreateAsync();
            return;
        }

        if (CurrentStepIndex == UploadStep)
        {
            CurrentStepIndex = ReviewStep;
            return;
        }

        if (!await Editor.PromptSaveAllAsync()) return;
        await Upload.UploadAsync();
    }

    private async Task RunCreateAsync()
    {
        // Ask before replacing, rather than reporting the collision after the fact.
        var existing = CreatePackage.FindExistingPackage(_settingsService.Settings);
        var overwrite = false;
        if (existing != null)
        {
            if (!_dialogs.Confirm($"A package for this version already exists:\n\n{existing}\n\nReplace it?", "Package already exists"))
                return;
            overwrite = true;
        }

        var packagePath = await CreatePackage.GenerateAsync(_settingsService.Settings, overwrite);
        if (!string.IsNullOrEmpty(packagePath))
            RaisePackageDependents();
        else if (!string.IsNullOrEmpty(CreatePackage.StatusText))
            _dialogs.Warn(CreatePackage.StatusText, "Package Generation");
    }

    private async Task RunUpgradeAsync()
    {
        var newPackagePath = await Upgrade.UpgradeAsync(_settingsService.Settings);
        if (string.IsNullOrEmpty(newPackagePath))
        {
            if (!string.IsNullOrEmpty(Upgrade.StatusText))
                _dialogs.Warn(Upgrade.StatusText, "Package Upgrade");
            return;
        }

        // Carry the upgraded metadata across so the tools and publish step work.
        var meta = Upgrade.LoadedMetadata;
        if (meta != null)
        {
            CreatePackage.AppName = meta.AppName;
            CreatePackage.Manufacturer = meta.Manufacturer;
            CreatePackage.UserInstall = meta.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase);
        }
        CreatePackage.Version = Upgrade.NewVersion;
        CreatePackage.SourcesPath = Upgrade.NewSourcePath;
        CreatePackage.CurrentPackagePath = newPackagePath;
        CreatePackage.PredecessorAppId = PackageMarker.GetMarkerAppId(Upgrade.ExistingPackagePath) ?? "";

        RaisePackageDependents();
    }

    private void OpenPackageFolder()
    {
        var path = CreatePackage.CurrentPackagePath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open package folder: {ex.Message}");
        }
    }

}
