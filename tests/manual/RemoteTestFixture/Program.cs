using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

// Attended fixture preparation only. All deployments must be run from Packman's UI.
internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PackmanLocalVerificationT0218");
    private static readonly string Settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packman", "appsettings.json");
    private const string Stage = @"C:\Temp\Packman\Orchestra_PackmanLocalProbe_1.0.0";
    private const string Installed = @"C:\Program Files\PackmanLocalProbe";
    private const string Logs = @"C:\ProgramData\PackmanLocalProbeLogs";
    private static readonly TextBox Output = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    [STAThread]
    public static void Main()
    {
        var app = new Application();
        var panel = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Top);
        panel.Children.Add(buttons);
        panel.Children.Add(Output);
        Add("Prepare fixture", Prepare);
        Add("Protect staging (UAC)", Protect);
        Add("Inspect", Inspect);
        Add("Restore PC (UAC)", Restore);
        Log("Manual Remote Test fixture. Close Packman before Prepare or Restore. UAC approvals belong to the attendant.");
        Log("Fixture/evidence: " + Root);
        app.Run(new Window { Title = "Packman local verification fixture", Width = 1050, Height = 700, Content = panel });

        void Add(string title, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Thickness(6), Padding = new Thickness(8) };
            button.Click += async (_, _) =>
            {
                buttons.IsEnabled = false;
                try { await action(); }
                catch (Exception ex) { Log("FAIL: " + ex.Message); }
                finally { buttons.IsEnabled = true; }
            };
            buttons.Children.Add(button);
        }
    }

    private static void Log(string text)
    {
        Output.Text = text + Environment.NewLine + Output.Text;
        Output.ScrollToHome();
        if (Directory.Exists(Root)) File.AppendAllText(Path.Combine(Root, "evidence.txt"), text + Environment.NewLine);
    }

    private static void RequireClosed()
    {
        if (Process.GetProcessesByName("Packman").Length != 0) throw new Exception("Close Packman first; settings must not be overwritten by a running instance.");
    }

    private static async Task Prepare()
    {
        RequireClosed();
        if (File.Exists(Path.Combine(Root, "restored.txt"))) Directory.Move(Root, Root + "-restored-" + DateTime.UtcNow.Ticks);
        if (Directory.Exists(Root) || Directory.Exists(Stage) || Directory.Exists(Installed) || Directory.Exists(Logs))
            throw new Exception("A fixture path already exists. Inspect it; do not overwrite baseline data.");
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "settings-existed.txt"), File.Exists(Settings).ToString());
        if (File.Exists(Settings)) File.Copy(Settings, Path.Combine(Root, "settings.backup"));
        File.WriteAllText(Path.Combine(Root, "parents.json"), new JsonObject {
            [@"C:\Temp"] = Directory.Exists(@"C:\Temp"), [@"C:\Temp\Packman"] = Directory.Exists(@"C:\Temp\Packman")
        }.ToJsonString());
        Log("Baseline: fixture stage, installed app and dedicated PSADT logs absent. Settings backed up.");
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Packman-Manual-Verification");
        const string url = "https://github.com/PSAppDeployToolkit/PSAppDeployToolkit/releases/download/4.1.7/PSAppDeployToolkit_Template_v4.zip";
        Log("Downloading real PSADT runtime: " + url);
        var archive = Path.Combine(Root, "runtime.zip");
        await File.WriteAllBytesAsync(archive, await client.GetByteArrayAsync(url));
        var template = Path.Combine(Root, "template");
        ZipFile.ExtractToDirectory(archive, template);
        using var script = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("RemoteTestFixture.Invoke-AppDeployToolkit.ps1")!);
        await File.WriteAllTextAsync(Path.Combine(template, "Invoke-AppDeployToolkit.ps1"), await script.ReadToEndAsync(), new UTF8Encoding(true));
        var config = Path.Combine(template, "Config", "config.psd1");
        var configText = await File.ReadAllTextAsync(config);
        configText = Regex.Replace(configText, @"(?m)^(\s*LogPath\s*=\s*).*$", "$1'" + Logs + "'");
        configText = Regex.Replace(configText, @"(?m)^(\s*CompressLogs\s*=\s*).*$", "$1$$false");
        await File.WriteAllTextAsync(config, configText, new UTF8Encoding(true));
        Directory.CreateDirectory(Path.Combine(Root, "packages"));
        Directory.CreateDirectory(Path.Combine(Root, "sources"));
        await File.WriteAllTextAsync(Path.Combine(Root, "sources", "probe.txt"), "The template installs a copy of the Windows whoami executable, then removes it.");
        var settings = File.Exists(Settings) ? JsonNode.Parse(File.ReadAllText(Settings))!.AsObject() : new JsonObject();
        // Keep authentication and unrelated preferences exactly as read; restoration uses the original bytes.
        var paths = settings["NetworkPaths"]?.AsObject() ?? new JsonObject();
        settings["NetworkPaths"] = null;
        paths["IntuneApplications"] = Path.Combine(Root, "packages");
        paths["PSADTTemplate"] = template;
        if (string.IsNullOrEmpty(paths["IntuneWinAppUtil"]?.GetValue<string>()))
            throw new Exception("An existing IntuneWinAppUtil path is required for setup; none was configured.");
        settings["NetworkPaths"] = paths;
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        await File.WriteAllTextAsync(Settings, settings.ToJsonString());
        Log("READY: launch Packman, create manufacturer Orchestra / app PackmanLocalProbe / version 1.0.0, source folder " + Path.Combine(Root, "sources"));
        Log("Template deliberately has no installer insertion markers: its tested install/uninstall actions are already defined.");
        await Inspect();
    }

    private static async Task Protect()
    {
        if (!File.Exists(Path.Combine(Root, "settings-existed.txt"))) throw new Exception("Prepare the fixture first.");
        if (Directory.Exists(Stage)) throw new Exception("Staging already exists; refusing to change its ACL again.");
        Log("ATTENDANT: approve the fixture preparation UAC prompt now. This is separate from Packman's install UAC.");
        await PowerShell($$"""
            $ErrorActionPreference = 'Stop'
            $service = Get-CimInstance Win32_Service -Filter "Name='WinRM'"
            $service | Select-Object State, StartMode | Export-Clixml '{{Root}}\winrm.xml'
            Stop-Service WinRM
            Set-Service WinRM -StartupType Disabled
            New-Item -ItemType Directory -Path '{{Stage}}' | Out-Null
            icacls.exe '{{Stage}}' /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX'
            if ($LASTEXITCODE -ne 0) { throw 'icacls failed' }
            'PROTECTED STAGING READY'
            """, true);
        await Inspect();
    }

    private static async Task Inspect()
    {
        Log("Fixture process elevated: " + new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
        await PowerShell($$"""
            Get-CimInstance Win32_Service -Filter "Name='WinRM'" | Select-Object Name, State, StartMode | Format-List | Out-String
            'Installed file exists: ' + (Test-Path '{{Installed}}\PackmanLocalProbe.exe')
            'Scheduled task exists: ' + [bool](Get-ScheduledTask -TaskName Packman_RemoteTest -ErrorAction SilentlyContinue)
            if (Test-Path '{{Stage}}') {
                icacls.exe '{{Stage}}'
                try { [IO.File]::WriteAllText('{{Stage}}\unelevated-write-probe', 'probe'); Remove-Item '{{Stage}}\unelevated-write-probe'; 'FAIL: unelevated staging write succeeded' }
                catch [UnauthorizedAccessException] { 'PASS: unelevated staging write denied' }
            }
            if (Test-Path '{{Logs}}') {
                Get-ChildItem '{{Logs}}' -Recurse -File | ForEach-Object { $_.FullName; Get-Content $_.FullName | Select-String 'PROBE |exit code|DeployMode|Deployment mode' }
            }
            """, false);
    }

    private static async Task Restore()
    {
        RequireClosed();
        if (!File.Exists(Path.Combine(Root, "settings-existed.txt"))) throw new Exception("No fixture baseline to restore.");
        if (Directory.Exists(Installed)) throw new Exception("Complete Uninstall in Packman first; the installed probe still exists.");
        if (Directory.Exists(Stage) || Directory.Exists(Logs) || File.Exists(Path.Combine(Root, "winrm.xml")))
        {
        Log("ATTENDANT: approve fixture cleanup UAC. It removes only this fixture's stage/logs and restores WinRM.");
        await PowerShell($$"""
            $ErrorActionPreference = 'Stop'
            if (Get-ScheduledTask -TaskName Packman_RemoteTest -ErrorAction SilentlyContinue) { throw 'The deployment task still exists; inspect it before cleanup.' }
            if (Test-Path '{{Logs}}') { Copy-Item '{{Logs}}' '{{Root}}\saved-psadt-logs' -Recurse; Remove-Item '{{Logs}}' -Recurse -Force }
            if (Test-Path '{{Stage}}') { Remove-Item '{{Stage}}' -Recurse -Force }
            $parents = Get-Content '{{Root}}\parents.json' -Raw | ConvertFrom-Json
            foreach ($parent in 'C:\Temp\Packman','C:\Temp') {
                if (-not $parents.$parent -and (Test-Path $parent) -and -not (Get-ChildItem $parent -Force)) { Remove-Item $parent }
            }
            if (Test-Path '{{Root}}\winrm.xml') {
                $before = Import-Clixml '{{Root}}\winrm.xml'
                $mode = switch ($before.StartMode) { 'Auto' { 'Automatic' } 'Manual' { 'Manual' } 'Disabled' { 'Disabled' } }
                Set-Service WinRM -StartupType $mode
                if ($before.State -eq 'Running') { Start-Service WinRM }
            }
            'PRIVILEGED CLEANUP COMPLETE'
            """, true);
        }
        if (File.ReadAllText(Path.Combine(Root, "settings-existed.txt")) == "True")
            File.Copy(Path.Combine(Root, "settings.backup"), Settings, true);
        else File.Delete(Settings);
        foreach (var directory in new[] { "template", "packages", "sources" })
            if (Directory.Exists(Path.Combine(Root, directory))) Directory.Delete(Path.Combine(Root, directory), true);
        if (File.Exists(Path.Combine(Root, "runtime.zip"))) File.Delete(Path.Combine(Root, "runtime.zip"));
        Log("Settings restored byte-for-byte; test source/template/packages removed. Evidence retained at " + Root);
        await Inspect();
        File.WriteAllText(Path.Combine(Root, "restored.txt"), "Restored");
        Log($"RESTORED: stage absent={!Directory.Exists(Stage)}, app absent={!Directory.Exists(Installed)}, logs absent={!Directory.Exists(Logs)}");
    }

    private static async Task PowerShell(string script, bool elevate)
    {
        var path = Path.Combine(Root, "fixture-action.ps1");
        var result = Path.Combine(Root, "fixture-action.log");
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(result, "");
        await File.WriteAllTextAsync(path, "try { & {\n" + script + "\n} *>&1 | Out-File -LiteralPath '" + result + "' -Encoding utf8; exit 0 } catch { $_ | Out-File -LiteralPath '" + result + "' -Append; exit 1 }", new UTF8Encoding(true));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"", UseShellExecute = elevate,
            WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true
        };
        if (elevate) start.Verb = "runas";
        using var process = await Task.Run(() => Process.Start(start)!);
        await process.WaitForExitAsync();
        Log(await File.ReadAllTextAsync(result));
        if (process.ExitCode != 0) throw new Exception("Fixture action failed, exit " + process.ExitCode);
    }
}
