using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Packman.Services;
using Packman.ViewModels;

// Explicit integration check: installs a disposable real PSADT package through production code.
internal static class Program
{
    private const string Stage = @"C:\Temp\Packman\Orchestra_PackmanLocalProbe_1.0.0";
    private const string Installed = @"C:\Program Files\PackmanLocalProbe";
    private const string Logs = @"C:\ProgramData\PackmanLocalProbeLogs";
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PackmanLocalVerificationT0218");
    private static readonly string Evidence = Path.Combine(AppContext.BaseDirectory, "verification-evidence");

    [STAThread]
    public static int Main()
    {
        // An already elevated node process registers the SYSTEM task without requesting UAC.
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Console.WriteLine($"Fixture identity: {identity.User}; elevated: {elevated}; SYSTEM: {identity.IsSystem}");
        if (!elevated)
        {
            Console.Error.WriteLine("FAIL: The node launcher is unelevated. An already elevated node process is required to register the SYSTEM task without UAC.");
            return 1;
        }
        Directory.CreateDirectory(Evidence);
        using var transcript = new StreamWriter(Path.Combine(Evidence, "verification.txt"), false) { AutoFlush = true };
        var stdout = Console.Out;
        Console.SetOut(transcript);
        bool ownsFixture = false;
        bool passed = false;
        RemoteTestViewModel? vm = null;
        try
        {
            Console.WriteLine($"UTC: {DateTime.UtcNow:O}; identity: {WindowsIdentity.GetCurrent().User}");
            foreach (var path in new[] { Root, Stage, Installed, Logs })
                Check(!Directory.Exists(path), "Baseline absent: " + path);
            PowerShell("if (Get-ScheduledTask -TaskName Packman_RemoteTest -ErrorAction SilentlyContinue) { throw 'Existing deployment task; refusing to run' }");
            Directory.CreateDirectory(Root);
            ownsFixture = true;
            Prepare();
            var settings = new SettingsService(Path.Combine(Root, "settings.json"));
            settings.Settings.RemoteTest.CleanupAfterRun = false;
            var create = new CreatePackageViewModel {
                Manufacturer = "Orchestra", AppName = "PackmanLocalProbe", Version = "1.0.0",
                CurrentPackagePath = Path.Combine(Root, "Orchestra_PackmanLocalProbe", "1.0.0")
            };
            var dialogs = new FixtureDialogs();
            AppServices.Dialogs = dialogs;
            vm = new RemoteTestViewModel(settings, create, hasPublishStep: true) { SelectedDeployMode = "Silent" };
            var upload = new UploadStepViewModel(create, settings, AppServices.Auth);
            vm.ApplyDetectionRequested += upload.ApplyDiscoveredRule;
            vm.RunOnThisPcCommand.Execute(null);
            Check(vm.RunOnThisPc && vm.IsSystemContext, "Local SYSTEM mode selected");

            Run(vm.InstallCommand);
            SaveLog(vm, "install.log");
            VerifyRun(vm, "Install", 3010);
            Check(dialogs.Confirmations == 1, "First local install confirmation requested once");
            Check(File.Exists(Path.Combine(Installed, "PackmanLocalProbe.exe")), "Real probe installed");

            Run(vm.DetectCommand);
            Check(vm.HasDiscoveredRule, "Detect found the installed local executable");
            Check(vm.ApplyDetectionCommand.CanExecute(null), "Apply detection enabled for generated package");
            vm.ApplyDetectionCommand.Execute(null);
            Check(upload.DetectionPath == Installed && upload.DetectionName == "PackmanLocalProbe.exe" && upload.DetectionReady,
                $"Apply detection populated publish fields: {upload.DetectionPath} / {upload.DetectionName} / {upload.DetectionValue}");
            Check(vm.Lines.Any(l => l.Text.StartsWith("[OK] Applied to the publish step:")), "Apply result in existing log view");
            SaveLog(vm, "detect-apply.log");

            Run(vm.UninstallCommand);
            SaveLog(vm, "uninstall.log");
            VerifyRun(vm, "Uninstall", 0);
            Check(!Directory.Exists(Installed), "Real probe uninstalled");
            Run(vm.DetectCommand);
            Check(!vm.HasDiscoveredRule, "Detect no longer finds probe after uninstall");
            SaveLog(vm, "detect-after-uninstall.log");
            Check(dialogs.Confirmations == 1, "Uninstall did not repeat install confirmation");
            PowerShell("if (Get-ScheduledTask -TaskName Packman_RemoteTest -ErrorAction SilentlyContinue) { throw 'Deployment task was not removed' }; 'PASS: one-shot task removed'");
            passed = true;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex); }
        finally
        {
            if (ownsFixture)
            {
                try
                {
                    // If an assertion failed after installation, still exercise production uninstall.
                    if (Directory.Exists(Installed) && vm != null)
                    {
                        Run(vm.UninstallCommand);
                        SaveLog(vm, "recovery-uninstall.log");
                    }
                    Restore();
                }
                catch (Exception ex) { passed = false; Console.WriteLine("RESTORATION FAILED: " + ex); }
            }
            Console.WriteLine(passed ? "PASS: SYSTEM/Silent install, Detect/Apply, uninstall and restoration" : "FAIL: integration verification");
            Console.SetOut(stdout);
        }
        Console.Write(File.ReadAllText(Path.Combine(Evidence, "verification.txt")));
        return passed ? 0 : 1;
    }

    private static void Prepare()
    {
        PowerShell($$"""
            $service = Get-CimInstance Win32_Service -Filter "Name='WinRM'"
            $service | Select-Object State, StartMode | Export-Clixml '{{Root}}\winrm.xml'
            'Baseline WinRM: ' + $service.State + ' / ' + $service.StartMode
            @{ 'C:\Temp' = (Test-Path 'C:\Temp'); 'C:\Temp\Packman' = (Test-Path 'C:\Temp\Packman') } | Export-Clixml '{{Root}}\parents.xml'
            Stop-Service WinRM
            Set-Service WinRM -StartupType Disabled
            $service = Get-CimInstance Win32_Service -Filter "Name='WinRM'"
            if ($service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled') { throw 'WinRM is not disabled and stopped' }
            'PASS: WinRM stopped and disabled'
            New-Item -ItemType Directory -Path '{{Stage}}' | Out-Null
            icacls.exe '{{Stage}}' /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX'
            if ($LASTEXITCODE -ne 0) { throw 'icacls failed' }
            'PASS: staging protected; SYSTEM/Administrators write, Users read only'
            """);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Packman-Integration-Verification");
        const string url = "https://github.com/PSAppDeployToolkit/PSAppDeployToolkit/releases/download/4.1.7/PSAppDeployToolkit_Template_v4.zip";
        var archive = Path.Combine(Root, "runtime.zip");
        File.WriteAllBytes(archive, client.GetByteArrayAsync(url).GetAwaiter().GetResult());
        var application = Path.Combine(Root, "Orchestra_PackmanLocalProbe", "1.0.0", "Application");
        ZipFile.ExtractToDirectory(archive, application);
        using var script = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("RemoteTestFixture.Invoke-AppDeployToolkit.ps1")!);
        File.WriteAllText(Path.Combine(application, "Invoke-AppDeployToolkit.ps1"), script.ReadToEnd(), new UTF8Encoding(true));
        var config = Path.Combine(application, "Config", "config.psd1");
        var content = File.ReadAllText(config);
        content = Regex.Replace(content, @"(?m)^(\s*LogPath\s*=\s*).*$", "$1'" + Logs + "'");
        content = Regex.Replace(content, @"(?m)^(\s*CompressLogs\s*=\s*).*$", "$1$$false");
        File.WriteAllText(config, content, new UTF8Encoding(true));
        Console.WriteLine("Prepared real PSADT 4.1.7 package: " + application);
    }

    private static void VerifyRun(RemoteTestViewModel vm, string operation, int exitCode)
    {
        var log = string.Join("\n", vm.Lines.Select(l => l.Text));
        Check(vm.LastRunSucceeded == true && log.Contains($"✓ {operation.ToUpperInvariant()} SUCCEEDED"), operation + " result succeeded");
        Check(log.Contains($"Exit code: {exitCode}"), operation + " exit code: " + exitCode);
        if (Packman.Helpers.InstallErrorText.Describe(exitCode) is { } meaning)
            Check(log.Contains("Meaning: " + meaning), "InstallErrorText: " + meaning);
        Check(log.Contains($"Command: Invoke-AppDeployToolkit.exe {operation} -DeployMode Silent"), "Selected Silent deploy mode forwarded");
        Check(log.Contains($"SID=S-1-5-18; mode=Silent; operation={operation}"), "Real PSADT log confirms SYSTEM/Silent " + operation);
        Check(log.Contains(Logs) && Directory.GetFiles(Logs, "*.log", SearchOption.AllDirectories).Length > 0, "PSADT log path: " + Logs);
        Check(!log.Contains("asking for elevation") && !log.Contains("WinRM"), "Local run used neither UAC nor WinRM");
    }

    private static void Restore()
    {
        PowerShell($$"""
            if (Get-ScheduledTask -TaskName Packman_RemoteTest -ErrorAction SilentlyContinue) { throw 'Deployment task remains; inspect before cleanup' }
            if (Test-Path '{{Installed}}') { throw 'Probe remains installed; recovery uninstall failed' }
            if (Test-Path '{{Logs}}') {
                New-Item -ItemType Directory -Force '{{Evidence}}\psadt' | Out-Null
                Copy-Item '{{Logs}}\*' '{{Evidence}}\psadt' -Recurse -Force
                Remove-Item '{{Logs}}' -Recurse -Force
            }
            if (Test-Path '{{Stage}}') { Remove-Item '{{Stage}}' -Recurse -Force }
            if (Test-Path '{{Root}}\parents.xml') {
                $parents = Import-Clixml '{{Root}}\parents.xml'
                foreach ($parent in 'C:\Temp\Packman','C:\Temp') {
                    if (-not $parents[$parent] -and (Test-Path $parent) -and -not (Get-ChildItem $parent -Force)) { Remove-Item $parent }
                }
            }
            if (Test-Path '{{Root}}\winrm.xml') {
                $before = Import-Clixml '{{Root}}\winrm.xml'
                $mode = switch ($before.StartMode) { 'Auto' { 'Automatic' } 'Manual' { 'Manual' } 'Disabled' { 'Disabled' } }
                Set-Service WinRM -StartupType $mode
                if ($before.State -eq 'Running') { Start-Service WinRM }
                $after = Get-CimInstance Win32_Service -Filter "Name='WinRM'"
                if ($after.State -ne $before.State -or $after.StartMode -ne $before.StartMode) { throw 'WinRM restoration mismatch' }
                'PASS: WinRM restored: ' + $after.State + ' / ' + $after.StartMode
            }
            """);
        Directory.Delete(Root, true);
        foreach (var path in new[] { Root, Stage, Installed, Logs }) Check(!Directory.Exists(path), "Restored absent: " + path);
        Console.WriteLine("PASS: owner settings untouched; fixture settings isolated; PSADT evidence saved to " + Evidence);
    }

    private static void Run(AsyncRelayCommand command)
    {
        Check(command.CanExecute(null), "Command enabled");
        command.Execute(null);
        while (command.IsRunning) Thread.Sleep(50);
    }

    private static void SaveLog(RemoteTestViewModel vm, string name) =>
        File.WriteAllLines(Path.Combine(Evidence, name), vm.Lines.Select(l => l.Text));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void PowerShell(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) {
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Console.WriteLine(output.GetAwaiter().GetResult());
        if (process.ExitCode != 0) throw new Exception(error.GetAwaiter().GetResult());
    }

    private sealed class FixtureDialogs : IDialogService
    {
        public int Confirmations { get; private set; }
        public bool Confirm(string message, string title, string action, string safe = "Cancel")
        {
            Check(title == "Install on this PC" && message.Contains("on this PC") && message.Contains(Environment.MachineName), "Confirmation: " + message);
            Confirmations++;
            return true;
        }
        public bool? ConfirmOrCancel(string message, string title, string yes, string no) => throw new Exception(message);
        public void Warn(string message, string title) => throw new Exception(message);
        public void Info(string message, string title) => Console.WriteLine(message);
    }
}
