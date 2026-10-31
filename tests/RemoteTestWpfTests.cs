#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:property AssemblyName=RemoteTestWpfTests
#:project ../Packman/Packman.csproj

using System.IO;
using Packman.Services;
using Packman.ViewModels;

// Remote Test's local path ("Run on this PC"): it must stage, register the SYSTEM task and clean
// up from one elevated script, never open WinRM, forward the deploy mode, print the exit code
// with its meaning, and search this PC's own C:\ for detection. The elevated PowerShell is
// replaced by a recorder, so nothing elevates, installs or registers a task. The view model
// checks cover the first-install confirmation, the mode switch, the log and the local detection
// search (against a folder this test creates under %LOCALAPPDATA%). Windows only (WPF).
//   dotnet run --file tests/RemoteTestWpfTests.cs -c Release
// Exits non-zero when a check fails. Settings are read, never saved.
internal static class RemoteTestWpfTests
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static readonly (string Name, Action Check)[] Checks =
    [
        ("A local run never opens WinRM or pings", NoWinRm),
        ("Staging, the task and cleanup share the one elevated script, staging first", StagesInsideElevation),
        ("The command line carries the selected deploy mode and runs as SYSTEM", SystemWithDeployMode),
        ("User context registers an interactive task for the logged-on user", UserContext),
        ("Exit code 0 is reported without a meaning", ExitCodeZero),
        ("A failing exit code is reported with its InstallErrorText meaning", ExitCodeWithMeaning),
        ("A reboot-required exit code reports its meaning and counts as success", ExitCodeReboot),
        ("A run that returns no exit code is reported as -1 with a warning", NoExitCode),
        ("Cleanup is part of the elevated script only when asked for", CleanupInScript),
        ("A declined elevation reaches the caller", ElevationDeclined),
        ("A package without its launcher fails before anything elevates", MissingLauncher),
        ("Detection searches this PC's C:\\ directly and another PC over C$", SearchRoots),
        ("The output file is read in whole lines until the run ends", ReadsWholeLines),
        ("The real elevated runner streams output, reports errors and takes a script of full size", RunnerStreamsOutput),
        ("Run on this PC targets this PC, and typing another computer leaves it", ModeSwitch),
        ("The first local install asks for confirmation; declining runs nothing", ConfirmDeclined),
        ("Local install and uninstall forward the deploy mode and fill the log and summary", ViewModelRuns),
        ("Detect searches this PC's own folders", DetectsLocally),
    ];

    [STAThread]
    public static int Main()
    {
        var dialogs = new FakeDialogs();
        AppServices.Dialogs = dialogs;
        _dialogs = dialogs;
        foreach (var (name, check) in Checks)
        {
            try
            {
                check();
                _passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── Local routing ──

    private static void NoWinRm()
    {
        var (service, log, _) = Run();
        var all = string.Join("\n", log);
        True(all.Contains("Packman Remote Test (this PC)"), "header names this PC");
        True(!all.Contains("WinRM") && !all.Contains("ping", StringComparison.OrdinalIgnoreCase), $"no WinRM or ping line:\n{all}");
        True(service.Scripts.Count == 1, $"one elevated script, got {service.Scripts.Count}");
    }

    private static void StagesInsideElevation()
    {
        var (service, _, _) = Run(cleanup: true);
        var script = service.Scripts[0];
        int stage = script.IndexOf("robocopy.exe", StringComparison.Ordinal);
        int task = script.IndexOf("Register-ScheduledTask", StringComparison.Ordinal);
        int cleanup = script.IndexOf("Remove-Item -LiteralPath 'C:\\Temp\\Packman\\Vendor_App_1.0.0'", StringComparison.Ordinal);
        True(stage >= 0 && task > stage && cleanup > task, $"order stage {stage} < task {task} < cleanup {cleanup}");
        True(script.Contains("Vendor_App\\1.0.0'") || script.Contains("1.0.0' 'C:\\Temp\\Packman\\Vendor_App_1.0.0'"),
            "robocopy mirrors the source into the staging folder");
        True(script.Contains("/MIR"), "mirrors with /MIR");
    }

    private static void SystemWithDeployMode()
    {
        var (service, log, _) = Run(commandLine: "Invoke-AppDeployToolkit.exe Install -DeployMode Silent");
        var script = service.Scripts[0];
        True(script.Contains("'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest"), "SYSTEM principal");
        True(script.Contains("/c Invoke-AppDeployToolkit.exe Install -DeployMode Silent > "), "deploy mode forwarded to the task command");
        True(script.Contains("$workingDirectory = 'C:\\Temp\\Packman\\Vendor_App_1.0.0\\Application'"), "runs from the staged Application folder");
        True(log.Contains("Command: Invoke-AppDeployToolkit.exe Install -DeployMode Silent"), "command echoed in the log");
        True(log.Contains("Run as: NT AUTHORITY\\SYSTEM"), "context echoed in the log");
    }

    private static void UserContext()
    {
        var (service, log, _) = Run(runAsUser: true);
        True(service.Scripts[0].Contains("-LogonType Interactive"), "interactive logon type");
        True(log.Contains("Run as: Logged-on user"), "context echoed in the log");
    }

    // ── Exit codes ──

    private static void ExitCodeZero()
    {
        var (_, log, exit) = Run(exitLine: "PACKMAN_EXIT_CODE:0");
        True(exit == 0, $"exit {exit}");
        True(log.Contains("Exit code: 0"), "exit code line");
        True(!log.Any(l => l.StartsWith("Meaning:")), "no meaning for 0");
        True(!log.Any(l => l.Contains("PACKMAN_EXIT_CODE")), "sentinel is not echoed");
        True(log.Contains("Task output line"), "script output is shown");
    }

    private static void ExitCodeWithMeaning()
    {
        var (_, log, exit) = Run(exitLine: "PACKMAN_EXIT_CODE:1603");
        True(exit == 1603, $"exit {exit}");
        True(log.Contains("Exit code: 1603"), "exit code line");
        True(log.Contains("Meaning: A fatal error occurred during installation."), $"meaning line:\n{string.Join("\n", log)}");
        True(!RemoteTestService.IsSuccess(exit), "1603 is a failure");
    }

    private static void ExitCodeReboot()
    {
        var (_, log, exit) = Run(exitLine: "PACKMAN_EXIT_CODE:3010");
        True(exit == 3010 && RemoteTestService.IsSuccess(exit), "3010 is success");
        True(log.Any(l => l.StartsWith("Meaning: A restart is required")), "restart meaning");
    }

    private static void NoExitCode()
    {
        var (_, log, exit) = Run(exitLine: null);
        True(exit == -1, $"exit {exit}");
        True(log.Any(l => l.StartsWith("WARNING: The remote monitor returned no exit code")), "warning shown");
    }

    private static void CleanupInScript()
    {
        True(Run(cleanup: true).Service.Scripts[0].Contains("Cleaning up..."), "cleanup requested");
        True(!Run(cleanup: false).Service.Scripts[0].Contains("Remove-Item -LiteralPath 'C:\\Temp\\Packman"), "no cleanup when off");
    }

    private static void ElevationDeclined()
    {
        var service = new RecordingService { Decline = true };
        var source = Package();
        try
        {
            Throws<InvalidOperationException>(() => service.Deploy("THISPC", source, "Install",
                "Invoke-AppDeployToolkit.exe Install", false, false, _ => { }, null, local: true), "Elevation was declined");
        }
        finally { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(source))!, true); }
    }

    private static void MissingLauncher()
    {
        var service = new RecordingService();
        var source = Package(withLauncher: false);
        try
        {
            Throws<FileNotFoundException>(() => service.Deploy("THISPC", source, "Install",
                "Invoke-AppDeployToolkit.exe Install", false, false, _ => { }, null, local: true), "Invoke-AppDeployToolkit.exe is missing");
            True(service.Scripts.Count == 0, "nothing elevated");
        }
        finally { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(source))!, true); }
    }

    // ── Detection and output file ──

    private static void SearchRoots()
    {
        var local = DetectionDiscoveryService.GetSearchRoots(Environment.MachineName);
        True(local.Take(3).All(r => r.StartsWith("C:\\")), $"local roots on C:\\ : {string.Join(", ", local.Take(3))}");
        var remote = DetectionDiscoveryService.GetSearchRoots("OTHERPC");
        True(remote.Take(3).All(r => r.StartsWith("\\\\OTHERPC\\C$")), $"remote roots over C$: {string.Join(", ", remote.Take(3))}");
    }

    // The real RunElevated, told Packman is already elevated so it starts PowerShell without a UAC
    // prompt. Covers the wrapper, the encoded command at the size of the real script, and the tail.
    private static void RunnerStreamsOutput()
    {
        var chunks = new List<string>();
        var script = "'tick 1'; Start-Sleep -Milliseconds 1500; 'tick 2'; Write-Error 'non-terminating'; throw 'boom'\n# " + new string('x', 10_000);
        new AlreadyElevatedService().RunElevated(script, _ => { }, chunks.Add);
        var text = string.Join("", chunks);
        True(text.Contains("tick 1\n") && text.Contains("tick 2\n"), $"script output arrives:\n{text}");
        True(text.Contains("ERROR: ") && text.Contains("non-terminating"), "a non-terminating error is reported");
        True(text.Contains("ERROR: boom"), "a terminating error is reported");
        True(chunks.Count(c => c.Length > 0) >= 2, $"output is streamed while the script runs, got {chunks.Count(c => c.Length > 0)} chunk(s)");
    }

    private sealed class AlreadyElevatedService : RemoteTestService
    {
        internal override bool IsElevated => true;
    }

    private static void ReadsWholeLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "first\nsecond-par");
            long offset = 0;
            True(RemoteTestService.ReadNewLines(path, ref offset, final: false) == "first\n", "only the whole line");
            True(RemoteTestService.ReadNewLines(path, ref offset, final: false) == "", "nothing new yet");
            File.AppendAllText(path, "tial\n");
            True(RemoteTestService.ReadNewLines(path, ref offset, final: false) == "second-partial\n", "the finished line");
            File.AppendAllText(path, "tail without newline");
            True(RemoteTestService.ReadNewLines(path, ref offset, final: true) == "tail without newline", "final read takes the rest");
        }
        finally { File.Delete(path); }
    }

    // ── View model ──

    private static FakeDialogs _dialogs = new();

    private static void ModeSwitch()
    {
        var vm = new RemoteTestViewModel(AppServices.Settings);
        vm.RunOnThisPcCommand.Execute(null);
        True(vm.RunOnThisPc && vm.TargetComputer == Environment.MachineName && vm.IsValidTarget, "this PC is the target");
        True(!vm.CheckOnlineCommand.CanExecute(null), "ping is off for this PC");
        vm.TargetComputer = "OTHERPC";
        True(!vm.RunOnThisPc, "typing a computer leaves this-PC mode");
        True(vm.CheckOnlineCommand.CanExecute(null), "ping is back for a remote computer");
    }

    private static void ConfirmDeclined()
    {
        var (vm, service, root) = LocalViewModel("Probe1");
        try
        {
            _dialogs.Answer = false;
            _dialogs.Messages.Clear();
            Run(vm.InstallCommand);
            True(_dialogs.Messages.Count == 1, $"asked once, got {_dialogs.Messages.Count}");
            True(_dialogs.Messages[0].Contains("on this PC") && _dialogs.Messages[0].Contains(Environment.MachineName), $"message names this PC: {_dialogs.Messages[0]}");
            True(service.Scripts.Count == 0 && !vm.Lines.Any(l => l.Text.StartsWith("Packman Remote Test")), "nothing ran");
            True(!vm.IsRunning, "not left running");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ViewModelRuns()
    {
        var (vm, service, root) = LocalViewModel("Probe2");
        try
        {
            vm.SelectedDeployMode = "Silent";
            _dialogs.Answer = true;
            _dialogs.Messages.Clear();
            service.ExitLine = "PACKMAN_EXIT_CODE:1603";
            Run(vm.InstallCommand);
            True(service.Scripts.Count == 1 && service.Scripts[0].Contains("Install -DeployMode Silent"), "install forwards the deploy mode");
            var text = string.Join("\n", vm.Lines.Select(l => l.Text));
            True(text.Contains("Exit code: 1603") && text.Contains("Meaning: A fatal error occurred during installation."), $"exit code and meaning in the log:\n{text}");
            True(text.Contains("✗ INSTALL FAILED"), "failure banner");
            True(vm.LastRunSummary == "Install failed (exit 1603)" && vm.LastRunSucceeded == false, $"summary '{vm.LastRunSummary}'");

            Run(vm.InstallCommand);
            True(_dialogs.Messages.Count == 1, "confirmed once per session, not per install");

            service.ExitLine = "PACKMAN_EXIT_CODE:0";
            Run(vm.UninstallCommand);
            True(_dialogs.Messages.Count == 1, "uninstall does not ask");
            True(service.Scripts[^1].Contains("Uninstall -DeployMode Silent"), "uninstall forwards the deploy mode");
            True(vm.LastRunSummary == "Uninstall succeeded" && vm.LastRunSucceeded == true, $"summary '{vm.LastRunSummary}'");
            True(vm.Lines.Any(l => l.Text == "✓ UNINSTALL SUCCEEDED"), "success banner");
            True(vm.RecentComputers.All(c => c != Environment.MachineName), "this PC is not added to the recent computers");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void DetectsLocally()
    {
        var probe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackmanT0218Probe");
        Directory.CreateDirectory(probe);
        var (vm, _, root) = LocalViewModel("PackmanT0218Probe");
        try
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(probe, "PackmanT0218Probe.exe"));
            Run(vm.DetectCommand);
            var text = string.Join("\n", vm.Lines.Select(l => l.Text));
            True(text.Contains($"on {Environment.MachineName}") && text.Contains("Selected PackmanT0218Probe.exe"), $"found on this PC:\n{text}");
            True(vm.HasDiscoveredRule && vm.DiscoveredSummary.Contains("PackmanT0218Probe.exe"), $"rule '{vm.DiscoveredSummary}'");
        }
        finally
        {
            Directory.Delete(probe, true);
            Directory.Delete(root, true);
        }
    }

    private static (RemoteTestViewModel Vm, RecordingService Service, string Root) LocalViewModel(string appName)
    {
        var service = new RecordingService();
        var vm = new RemoteTestViewModel(AppServices.Settings) { CreateService = () => service };
        var version = Package(appName: appName);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(version))!;
        vm.SelectPackage(version);
        vm.RunOnThisPcCommand.Execute(null);
        return (vm, service, root);
    }

    // The commands are async void; wait for the run to finish.
    private static void Run(AsyncRelayCommand command)
    {
        command.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (command.IsRunning)
        {
            if (DateTime.UtcNow > deadline) throw new Exception("the command did not finish");
            Thread.Sleep(50);
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool Answer { get; set; }
        public List<string> Messages { get; } = new();
        public bool Confirm(string message, string title, string action, string safe = "Cancel") { Messages.Add(message); return Answer; }
        public bool? ConfirmOrCancel(string message, string title, string yes, string no) => Answer;
        public void Warn(string message, string title) { }
        public void Info(string message, string title) { }
    }

    // ── Harness ──

    // Stands in for the elevated PowerShell: records the script, plays back what it would print.
    private sealed class RecordingService : RemoteTestService
    {
        public List<string> Scripts { get; } = new();
        public string? ExitLine { get; set; } = "PACKMAN_EXIT_CODE:0";
        public bool Decline { get; set; }

        internal override void RunElevated(string taskScript, Action<string> output, Action<string> emit)
        {
            if (Decline) throw new InvalidOperationException("Elevation was declined; running on this PC needs administrator rights.");
            Scripts.Add(taskScript);
            emit("Task output line\n");
            if (ExitLine != null) emit(ExitLine + "\n");
        }
    }

    private static (RecordingService Service, List<string> Log, int Exit) Run(
        string commandLine = "Invoke-AppDeployToolkit.exe Install", bool runAsUser = false,
        bool cleanup = false, string? exitLine = "PACKMAN_EXIT_CODE:0")
    {
        var service = new RecordingService { ExitLine = exitLine };
        var source = Package();
        var log = new List<string>();
        try
        {
            int exit = service.Deploy("THISPC", source, "Install", commandLine, cleanup, runAsUser, log.Add, null, local: true);
            return (service, log, exit);
        }
        finally { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(source))!, true); }
    }

    // <root>\Vendor_App\1.0.0\Application, named like a package on the share.
    private static string Package(bool withLauncher = true, string appName = "App")
    {
        var version = Path.Combine(Directory.CreateTempSubdirectory("packman-local-").FullName, "Vendor_App", "1.0.0");
        var application = Directory.CreateDirectory(Path.Combine(version, "Application")).FullName;
        File.WriteAllText(Path.Combine(application, "Invoke-AppDeployToolkit.ps1"),
            $"$adtSession = @{{\n    AppVendor = 'Vendor'\n    AppName = '{appName}'\n    AppVersion = '1.0.0'\n}}\n");
        if (withLauncher) File.WriteAllText(Path.Combine(application, "Invoke-AppDeployToolkit.exe"), "");
        return version;
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action, string messagePart) where T : Exception
    {
        try { action(); }
        catch (T ex) when (ex.Message.Contains(messagePart)) { return; }
        throw new Exception($"expected {typeof(T).Name} containing '{messagePart}'");
    }
}
