using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation.Remoting;
using System.Windows;
using System.Windows.Threading;

namespace Packman.ViewModels;

public enum LineKind { Info, Good, Warn, Bad, Dim }

public sealed class RemoteTestLine
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    public LineKind Kind { get; init; }
}

/// <summary>
/// The Remote Test tool: stages a package on a test machine (over WinRM or PsExec) or in a
/// Windows Sandbox on this computer, runs PSADT as SYSTEM or as the logged-on user, then discovers the detection rule the install
/// produced and offers it to the publish step.
/// </summary>
public sealed class RemoteTestViewModel : ObservableObject
{
    private const int MaxRecentComputers = 8;

    // Both null on the standalone page: no wizard to pre-fill the package or feed.
    private readonly CreatePackageViewModel? _create;
    private readonly bool _hasPublishStep;
    private readonly SettingsService _settingsService;

    // PSADT is chatty: buffer and flush on a timer, or the console re-renders per line.
    private readonly List<RemoteTestLine> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    private string _packagePath = "";
    private string _packageLabel = "";
    private string _packageAppName = "";
    private string _packageVersion = "";
    private string _packageSourcePath = "";
    private bool _isGeneratedPackage;
    private string _targetComputer = "";
    private bool _runAsUser;
    private string _selectedDeployMode = PsadtLayout.DeployModeDefault;
    private bool _isRunning;
    private bool _isOnline;
    private string _statusText = "no target selected";
    private int? _copyPercent;
    private bool? _lastRunSucceeded;
    private string _lastRunSummary = "";
    private DetectionRule? _discoveredRule;
    private string _discoveredSummary = "";
    private int _contextVersion;
    private CancellationTokenSource? _runCts;

    private sealed record DetectionContext(int Revision, string Computer, string AppName, string Version, string SourcePath);

    private DetectionContext CaptureDetectionContext() =>
        new(_contextVersion, _targetComputer, _packageAppName, _packageVersion, _packageSourcePath);

    public ObservableCollection<RemoteTestLine> Lines { get; } = new();
    public ObservableCollection<string> RecentComputers { get; } = new();

    /// <summary>Picks a computer from the recent list in the rail.</summary>
    public RelayCommand<string> UseComputerCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand DetectCommand { get; }
    public AsyncRelayCommand CheckOnlineCommand { get; }
    public RelayCommand ApplyDetectionCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>Raised with the discovered rule when the packager applies it; the host routes it to the publish step.</summary>
    public event Action<DetectionRule>? ApplyDetectionRequested;

    public RemoteTestViewModel(SettingsService settingsService,
        CreatePackageViewModel? create = null, bool hasPublishStep = false)
    {
        _create = create;
        _hasPublishStep = hasPublishStep;
        _settingsService = settingsService;

        RefreshRecentComputers();
        _targetComputer = RecentComputers.FirstOrDefault() ?? "";

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _flushTimer.Tick += (_, _) => Flush();

        InstallCommand        = new AsyncRelayCommand(() => RunAsync("Install"), CanRun);
        UninstallCommand      = new AsyncRelayCommand(() => RunAsync("Uninstall"), CanRun);
        DetectCommand         = new AsyncRelayCommand(DiscoverAsync, () => !_isRunning && NeedsComputer && IsValidTarget);
        CheckOnlineCommand    = new AsyncRelayCommand(CheckOnlineAsync, () => !_isRunning && NeedsComputer && IsValidTarget);
        ApplyDetectionCommand = new RelayCommand(ApplyDetection, () => !_isRunning && _discoveredRule != null && _isGeneratedPackage);
        ClearLogCommand       = new RelayCommand(() => { Lines.Clear(); lock (_pending) _pending.Clear(); });
        CancelCommand         = new RelayCommand(CancelRun, () => _isRunning && _runCts is { IsCancellationRequested: false });
        UseComputerCommand = new RelayCommand<string>(c => { if (!string.IsNullOrWhiteSpace(c)) TargetComputer = c; });

        // Nothing to follow on the standalone page.
        if (_create == null) return;
        UseGeneratedPackage();
        _create.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreatePackageViewModel.CurrentPackagePath))
                UseGeneratedPackage();
        };
    }

    // ── Package under test ─────────────────────────────────────────────
    /// <summary>
    /// The package staged on the target. Pre-filled from the wizard; opened from the rail
    /// there is none, so the user browses to one on the share.
    /// </summary>
    public string PackagePath => _packagePath;

    /// <summary>Vendor, name and version of the package under test.</summary>
    public string PackageLabel { get => _packageLabel; private set => Set(ref _packageLabel, value); }

    public bool HasPackage => _packagePath.Length > 0;
    public bool NeedsPackage => _packagePath.Length == 0;

    /// <summary>The publish step only accepts the wizard's own package.</summary>
    public bool HasPublishStep => _hasPublishStep;
    public bool IsGeneratedPackage => _isGeneratedPackage;
    public bool IsSelectedPackage => HasPackage && !_isGeneratedPackage;

    public string PackageSourceCaption =>
        NeedsPackage ? "none selected" : _isGeneratedPackage ? "generated by the wizard" : "selected from the share";

    /// <summary>Generating a package pre-fills this page; resetting clears it.</summary>
    private void UseGeneratedPackage()
    {
        string path = _create!.CurrentPackagePath;
        if (string.IsNullOrEmpty(path))
        {
            // A hand-picked package stands on its own; only a wizard one clears.
            if (!_isGeneratedPackage) return;
            _packageAppName = _packageVersion = _packageSourcePath = "";
            PackageLabel = "";
            SetPackage("", fromWizard: false);
            return;
        }

        var appInfo = _create!.BuildApplicationInfo();
        _packageAppName = appInfo.Name;
        _packageVersion = appInfo.Version;
        _packageSourcePath = appInfo.SourcesPath;
        PackageLabel = $"{appInfo.Manufacturer} {appInfo.Name} {appInfo.Version}".Trim();
        SetPackage(path, fromWizard: true);
    }

    private void SetPackage(string path, bool fromWizard)
    {
        _packagePath = path;
        _isGeneratedPackage = fromWizard;
        InvalidateDetection();
        OnPropertyChanged(nameof(PackagePath));
        OnPropertyChanged(nameof(HasPackage));
        OnPropertyChanged(nameof(NeedsPackage));
        OnPropertyChanged(nameof(IsGeneratedPackage));
        OnPropertyChanged(nameof(IsSelectedPackage));
        OnPropertyChanged(nameof(PackageSourceCaption));
        RaiseCommandStates();
    }

    /// <summary>
    /// Takes a package built earlier, reading name and version back from the script.
    /// With no installer to inspect, detection discovery works from those alone.
    /// </summary>
    public void SelectPackage(string selectedPath)
    {
        string root = FolderBrowserHelper.GetPackageRootPath(selectedPath);
        string? scriptPath = FolderBrowserHelper.GetPSADTScriptPath(PsadtLayout.ApplicationFolder(root))
            ?? FolderBrowserHelper.GetPSADTScriptPath(root);

        if (scriptPath == null)
        {
            Append($"ERROR: {root} is not a PSADT package folder");
            Flush();
            return;
        }

        var metadata = MetadataExtractor.ExtractMetadataFromScript(scriptPath);
        _packageAppName = metadata.GetValueOrDefault("AppName", "");
        _packageVersion = metadata.GetValueOrDefault("Version", "");
        _packageSourcePath = "";
        PackageLabel = $"{metadata.GetValueOrDefault("Vendor", "")} {_packageAppName} {_packageVersion}".Trim();
        SetPackage(root, fromWizard: false);

        Append($"[OK] Package selected: {root}");
        Flush();
    }

    // ── Target ─────────────────────────────────────────────────────────
    public string TargetComputer
    {
        get => _targetComputer;
        set
        {
            if (!Set(ref _targetComputer, value)) return;
            InvalidateDetection();
            IsOnline = false;
            if (NeedsComputer) StatusText = IsValidTarget ? "not checked" : "enter a computer name";
            OnPropertyChanged(nameof(TargetLabel));
            RaiseCommandStates();
        }
    }

    public bool IsValidTarget => RemoteTestService.IsValidComputerName(_targetComputer);

    // ── Method ─────────────────────────────────────────────────────────
    /// <summary>How the test reaches the computer it runs on. Persisted between tests.</summary>
    public RemoteTestMethod Method
    {
        get => _settingsService.Settings.RemoteTest.Method;
        set
        {
            if (_settingsService.Settings.RemoteTest.Method == value) return;
            _settingsService.Settings.RemoteTest.Method = value;
            SaveSettingsQuietly();
            InvalidateDetection();
            IsOnline = false;
            StatusText = IsSandbox ? "Windows Sandbox" : IsValidTarget ? "not checked" : "enter a computer name";
            RaiseAll(nameof(Method), nameof(IsWinRm), nameof(IsPsExec), nameof(IsSandbox), nameof(NeedsComputer), nameof(TargetLabel),
                nameof(MethodHelpText), nameof(ContextHelpText), nameof(CleanupLabel), nameof(CleanupToolTip));
            RaiseCommandStates();
        }
    }

    // A radio button only ever sets its own property to true; unchecking the others is the group's job.
    public bool IsWinRm { get => Method == RemoteTestMethod.WinRM; set { if (value) Method = RemoteTestMethod.WinRM; } }
    public bool IsPsExec { get => Method == RemoteTestMethod.PsExec; set { if (value) Method = RemoteTestMethod.PsExec; } }
    public bool IsSandbox { get => Method == RemoteTestMethod.Sandbox; set { if (value) Method = RemoteTestMethod.Sandbox; } }

    /// <summary>A sandbox run has no computer to name.</summary>
    public bool NeedsComputer => !IsSandbox;

    /// <summary>What the rail and the log call the place the test runs.</summary>
    public string TargetLabel => IsSandbox ? "Windows Sandbox" : _targetComputer;

    public string MethodHelpText => Method switch
    {
        RemoteTestMethod.PsExec =>
            "PsExec: for computers without WinRM. Needs the admin shares (File and Printer Sharing) on the target and your own PsExec " +
            "from Microsoft Sysinternals (Settings ▸ Paths, or on PATH). A run accepts the Sysinternals license terms for your account.",
        RemoteTestMethod.Sandbox =>
            "Windows Sandbox: a clean, disposable Windows on this computer. Nothing is installed on a real device, and closing the sandbox " +
            "discards the test. Needs the Windows Sandbox feature (Windows Pro, Enterprise or Education).",
        _ => "WinRM: PowerShell remoting must be enabled on the target (Enable-PSRemoting). Enter localhost to test on this computer " +
             "instead: no WinRM, one administrator prompt per run.",
    };

    /// <summary>Whether the sandbox can reach the network. Persisted between tests.</summary>
    public bool SandboxNetworking
    {
        get => _settingsService.Settings.RemoteTest.SandboxNetworking;
        set
        {
            if (_settingsService.Settings.RemoteTest.SandboxNetworking == value) return;
            _settingsService.Settings.RemoteTest.SandboxNetworking = value;
            SaveSettingsQuietly();
            OnPropertyChanged();
        }
    }

    public string CleanupLabel => IsSandbox
        ? "Close the sandbox after the run"
        : "Delete the staged package from the target after the run";

    public string CleanupToolTip => IsSandbox
        ? "Off by default, so the sandbox stays open and you can look at what the install did."
        : "Off by default, so a re-run only copies the files that changed.";

    public bool IsOnline
    {
        get => _isOnline;
        private set { if (Set(ref _isOnline, value)) RaiseCommandStates(); }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    // ── Run context ────────────────────────────────────────────────────
    /// <summary>False runs as SYSTEM, matching Intune.</summary>
    public bool RunAsUser
    {
        get => _runAsUser;
        set { if (Set(ref _runAsUser, value)) { OnPropertyChanged(nameof(IsSystemContext)); OnPropertyChanged(nameof(ContextHelpText)); } }
    }

    public bool IsSystemContext
    {
        get => !_runAsUser;
        set => RunAsUser = !value;
    }

    public string ContextHelpText => _runAsUser
        ? IsSandbox
            ? "Runs in the sandbox's signed-in session (WDAGUtilityAccount) — its profile and HKCU, PSADT dialogs visible."
            : "Runs in the logged-on user's session — their profile and HKCU, PSADT dialogs visible."
        : "Runs as NT AUTHORITY\\SYSTEM via a scheduled task, the same identity the Intune Management Extension uses.";

    // ── Deploy mode ────────────────────────────────────────────────────
    public IReadOnlyList<string> DeployModes => PsadtLayout.DeployModes;

    /// <summary>Appended to the command line as -DeployMode, exactly as the publish step does.</summary>
    public string SelectedDeployMode
    {
        get => _selectedDeployMode;
        set { if (Set(ref _selectedDeployMode, value)) OnPropertyChanged(nameof(CommandPreview)); }
    }

    /// <summary>The install and uninstall command lines the test will run, as Intune would.</summary>
    public string CommandPreview =>
        $"{CommandLine("Install")}   ·   {CommandLine("Uninstall")}";

    // The same defaults the publish step uses, so a test exercises what ships.
    private string CommandLine(string deploymentType)
    {
        var defaults = _settingsService.Settings.IntuneDefaults;
        return PsadtLayout.WithDeployMode(
            deploymentType == "Install" ? defaults.InstallCommand : defaults.UninstallCommand, _selectedDeployMode);
    }

    // ── Run state ──────────────────────────────────────────────────────
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) { OnPropertyChanged(nameof(IsIdle)); RaiseCommandStates(); } }
    }

    public bool IsIdle => !_isRunning;

    public int? CopyPercent
    {
        get => _copyPercent;
        private set { if (Set(ref _copyPercent, value)) OnPropertyChanged(nameof(IsCopying)); }
    }

    public bool IsCopying => _copyPercent.HasValue;

    /// <summary>Outcome of the last install or uninstall; null before a run and while one runs.</summary>
    public bool? LastRunSucceeded { get => _lastRunSucceeded; private set => Set(ref _lastRunSucceeded, value); }

    /// <summary>"Install succeeded", "Uninstall failed (exit 1603)"; empty when there is no result.</summary>
    public string LastRunSummary { get => _lastRunSummary; private set => Set(ref _lastRunSummary, value); }

    // ── Discovered detection ───────────────────────────────────────────
    public bool HasDiscoveredRule => _discoveredRule != null;
    public string DiscoveredSummary { get => _discoveredSummary; private set => Set(ref _discoveredSummary, value); }

    private void InvalidateDetection()
    {
        _contextVersion++;
        _discoveredRule = null;
        DiscoveredSummary = "";
        OnPropertyChanged(nameof(HasDiscoveredRule));
        ApplyDetectionCommand.RaiseCanExecuteChanged();
    }

    private bool CanRun() => !_isRunning && HasPackage && (IsSandbox || IsValidTarget);

    private void RaiseCommandStates()
    {
        InstallCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
        DetectCommand.RaiseCanExecuteChanged();
        CheckOnlineCommand.RaiseCanExecuteChanged();
        ApplyDetectionCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsValidTarget));
    }

    // ── Actions ────────────────────────────────────────────────────────
    private async Task CheckOnlineAsync()
    {
        var context = CaptureDetectionContext();
        StatusText = "checking…";
        ActivityLog.Shared.Info($"Checking whether {context.Computer} is online…");
        bool online = await Task.Run(() =>
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                return ping.Send(context.Computer, 2000).Status == System.Net.NetworkInformation.IPStatus.Success;
            }
            catch { return false; }
        });

        if (context.Revision != _contextVersion || IsRunning) return;
        IsOnline = online;
        StatusText = online ? "online" : "unreachable";
        if (online) ActivityLog.Shared.Success($"{context.Computer} is online");
        else ActivityLog.Shared.Warning($"{context.Computer} is unreachable (no ping reply)");
        if (online) RememberComputer(context.Computer);
    }

    private async Task RunAsync(string deploymentType)
    {
        if (!CanRun()) return;

        Lines.Clear();
        lock (_pending) _pending.Clear();
        _discoveredRule = null;
        DiscoveredSummary = "";
        OnPropertyChanged(nameof(HasDiscoveredRule));

        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        LastRunSucceeded = null;
        LastRunSummary = "";
        StatusText = $"{deploymentType.ToLowerInvariant()} running…";
        ActivityLog.Shared.Section($"Remote test: {deploymentType.ToLowerInvariant()} on {TargetLabel}");
        _flushTimer.Start();
        var context = CaptureDetectionContext();

        string packagePath = _packagePath;
        string commandLine = CommandLine(deploymentType);
        bool runAsUser = _runAsUser;
        bool cleanup = CleanupAfterRun;
        var method = Method;
        bool sandbox = method == RemoteTestMethod.Sandbox;
        bool sandboxNetworking = SandboxNetworking;

        int exitCode = -1;
        var cancelled = false;
        try
        {
            if (!sandbox) RememberComputer(context.Computer);
            try
            {
                // Warn rather than block: a root-layout package can still be tested.
                foreach (var issue in await Task.Run(() => PackagePreflight.Check(packagePath)))
                    Append($"WARNING: {issue}");

                var paths = _settingsService.Settings.NetworkPaths;
                exitCode = await Task.Run(() => new RemoteTestService().DeployAsync(
                    context.Computer, packagePath, deploymentType, commandLine, cleanup, runAsUser,
                    Append, percent => CopyPercent = percent, cts.Token, paths.RemoteStagingFolder,
                    method, paths.PsExec, sandboxNetworking));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                Append(sandbox
                    ? "WARNING: Cancelled. If the sandbox window is still open, close it; that discards whatever the installer had done."
                    : "WARNING: Cancelled. The scheduled task was stopped; an installer it had already started may still be running or half-applied on the target.");
            }
            catch (PSRemotingTransportException ex)
            {
                Append($"ERROR: WinRM connection failed: {ex.Message}");
                Append("Enable WinRM on the target (Enable-PSRemoting) and check the firewall.");
            }
            catch (Exception ex)
            {
                Append($"ERROR: {ex.Message}");
            }
            CopyPercent = null;

            bool success = !cancelled && RemoteTestService.IsSuccess(exitCode);
            var explained = cancelled ? "" : InstallErrorText.Describe(exitCode);
            Append("========================================");
            Append(cancelled ? $"■ {deploymentType.ToUpperInvariant()} CANCELLED"
                : success ? $"✓ {deploymentType.ToUpperInvariant()} SUCCEEDED" : $"✗ {deploymentType.ToUpperInvariant()} FAILED");
            if (!string.IsNullOrEmpty(explained)) Append($"Exit code {exitCode}: {explained}");
            Append("========================================");
            Flush();
            StatusText = cancelled ? $"{deploymentType.ToLowerInvariant()} cancelled"
                : success ? $"{deploymentType.ToLowerInvariant()} succeeded (exit {exitCode})" : $"{deploymentType.ToLowerInvariant()} failed (exit {exitCode})";
            LastRunSucceeded = success;
            LastRunSummary = cancelled ? $"{deploymentType} cancelled"
                : success ? $"{deploymentType} succeeded" : $"{deploymentType} failed (exit {exitCode})";

            // Keep the operation busy through follow-up discovery so another action
            // cannot uninstall the app or change the target during this delay.
            // A sandbox is not reachable by name, so there is nothing to search afterwards.
            if (success && deploymentType == "Install" && !sandbox)
            {
                Append("Waiting for the registry to settle…");
                Flush();
                await Task.Delay(3000);
                if (context.Revision == _contextVersion && !cts.IsCancellationRequested)
                    await DiscoverCoreAsync(context);
            }
        }
        finally
        {
            _runCts = null;
            CopyPercent = null;
            _flushTimer.Stop();
            Flush();
            IsRunning = false;
        }
    }

    private async Task DiscoverAsync()
    {
        if (IsRunning || !NeedsComputer || !IsValidTarget) return;

        IsRunning = true;
        _flushTimer.Start();
        try
        {
            await DiscoverCoreAsync(CaptureDetectionContext());
        }
        finally
        {
            _flushTimer.Stop();
            Flush();
            IsRunning = false;
        }
    }

    private async Task DiscoverCoreAsync(DetectionContext context)
    {
        StatusText = "discovering detection rule…";

        try
        {
            var result = await new DetectionDiscoveryService()
                .DiscoverAsync(context.Computer, context.AppName, context.Version, context.SourcePath);

            // The wizard can replace its package while this tool is running in the
            // background. Only the context that requested discovery owns its result.
            if (context.Revision != _contextVersion) return;

            foreach (var message in result.Messages) Append(message);

            if (result.Success && result.SuggestedRules.Count > 0)
            {
                // Prefer the version rule: it still holds across an upgrade.
                _discoveredRule = result.SuggestedRules.LastOrDefault(r => r.CheckVersion) ?? result.SuggestedRules[0];
                DiscoveredSummary = _discoveredRule.Title;
                Append($"[OK] Detection rule found: {_discoveredRule.Title}");
                StatusText = "detection rule found";
            }
            else
            {
                _discoveredRule = null;
                DiscoveredSummary = "";
                Append($"WARNING: {result.ErrorMessage}");
                StatusText = "no detection rule found";
            }
        }
        catch (Exception ex)
        {
            if (context.Revision != _contextVersion) return;
            _discoveredRule = null;
            DiscoveredSummary = "";
            Append($"ERROR: {ex.Message}");
            StatusText = "detection failed";
        }
        finally
        {
            OnPropertyChanged(nameof(HasDiscoveredRule));
            ApplyDetectionCommand.RaiseCanExecuteChanged();
        }
    }

    private void CancelRun()
    {
        _runCts?.Cancel();
        StatusText = "cancelling…";
        CancelCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Pushes the discovered rule into the publish step's detection fields.</summary>
    private void ApplyDetection()
    {
        if (IsRunning || _discoveredRule == null || !_hasPublishStep || !_isGeneratedPackage) return;

        ApplyDetectionRequested?.Invoke(_discoveredRule);

        Append($"[OK] Applied to the publish step: {_discoveredRule.Title}");
        Flush();
        StatusText = "detection rule applied to publish";
    }

    /// <summary>Remove the staged package after a run. Persisted between tests.</summary>
    public bool CleanupAfterRun
    {
        get => _settingsService.Settings.RemoteTest.CleanupAfterRun;
        set
        {
            if (_settingsService.Settings.RemoteTest.CleanupAfterRun == value) return;
            _settingsService.Settings.RemoteTest.CleanupAfterRun = value;
            SaveSettingsQuietly();
            OnPropertyChanged();
        }
    }

    // ── Recent computers ───────────────────────────────────────────────
    /// <summary>Re-reads the saved machines and command lines. The wizard writes the same list.</summary>
    public void RefreshRecentComputers()
    {
        RecentComputers.Clear();
        foreach (var name in _settingsService.Settings.RemoteTest.RecentComputers)
            RecentComputers.Add(name);
        OnPropertyChanged(nameof(CommandPreview));
        // The wizard's test step and the standalone page share these saved choices.
        RaiseAll(nameof(Method), nameof(IsWinRm), nameof(IsPsExec), nameof(IsSandbox), nameof(NeedsComputer), nameof(TargetLabel),
            nameof(MethodHelpText), nameof(ContextHelpText), nameof(CleanupLabel), nameof(CleanupToolTip), nameof(SandboxNetworking),
            nameof(CleanupAfterRun));
    }

    private void RememberComputer(string name)
    {
        if (!RemoteTestService.IsValidComputerName(name)) return;

        var existing = RecentComputers.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) RecentComputers.Remove(existing);
        RecentComputers.Insert(0, name);
        while (RecentComputers.Count > MaxRecentComputers) RecentComputers.RemoveAt(RecentComputers.Count - 1);

        _settingsService.Settings.RemoteTest.RecentComputers = RecentComputers.ToList();
        SaveSettingsQuietly();
    }

    /// <summary>
    /// Persists a convenience setting. A failed write goes to the console pane rather
    /// than interrupting a test that is about to run.
    /// </summary>
    private void SaveSettingsQuietly()
    {
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            Append($"WARNING: could not save settings: {ex.Message}");
        }
    }

    // ── Console ────────────────────────────────────────────────────────
    private void Append(string text)
    {
        var line = new RemoteTestLine { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text, Kind = Classify(text) };
        lock (_pending) _pending.Add(line);

        // The same line goes to the live console; the rule-off lines are only decoration there.
        ActivityLevel? level = line.Kind switch
        {
            LineKind.Good => ActivityLevel.Success,
            LineKind.Warn => ActivityLevel.Warning,
            LineKind.Bad => ActivityLevel.Error,
            LineKind.Info => ActivityLevel.Info,
            _ => null,
        };
        if (level is { } shown) ActivityLog.Shared.Add(shown, text);
    }

    private void Flush()
    {
        RemoteTestLine[] batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => { foreach (var line in batch) Lines.Add(line); });
        else
            foreach (var line in batch) Lines.Add(line);
    }

    private static LineKind Classify(string text)
    {
        if (text.StartsWith("ERROR") || text.Contains('✗')) return LineKind.Bad;
        if (text.StartsWith("WARNING") || text.Contains('■')) return LineKind.Warn;
        if (text.StartsWith("[OK]") || text.Contains('✓')) return LineKind.Good;
        if (text.StartsWith("====")) return LineKind.Dim;
        return LineKind.Info;
    }
}
