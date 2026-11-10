using System.Diagnostics;
using System.IO;
using System.Management.Automation.Language;
using System.Text;
using Packman.Helpers;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>Fixes from the pre-release security review, each pinned by the input that broke it.</summary>
public sealed class SecurityHardeningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-hardening-").FullName;

    public void Dispose()
    {
        UploadLogger.ConfiguredFolder = "";
        try
        {
            // Remove junctions first so nothing is deleted through them.
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) Directory.Delete(dir);
            DirectoryCopy.ClearReadOnlyRecursive(_root);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private const string Session = "$adtSession = @{\n    AppVendor = ''\n    AppName = ''\n    AppVersion = ''\n}\n";

    [Theory]
    [InlineData("A\nAppName = x\n", "+ $(Stop-Computer) +")]
    [InlineData("A\r\n}\r\n", "x")]
    [InlineData("A\u2028AppName = x", "'; Stop-Computer; '")]
    public void ALineBreakInOneValueCannotTurnTheNextIntoScript(string vendor, string appName)
    {
        var script = PsadtScript.Parse(Session);
        Assert.True(script.SetOrAddSessionValue("AppVendor", vendor));
        Assert.True(script.SetOrAddSessionValue("AppName", appName));
        Assert.True(script.SetOrAddSessionBool("RequireAdmin", true));

        var ast = Parser.ParseInput(script.Content, out _, out var errors);
        Assert.Empty(errors);
        // Everything written is still data: no command or sub-expression appeared.
        Assert.Empty(ast.FindAll(n => n is CommandAst or SubExpressionAst, searchNestedScriptBlocks: true));
        Assert.Equal(appName.Trim(), script.AppName);
        Assert.DoesNotContain('\n', script.Vendor);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script.Content, @"(?m)^\s*AppName\s*="));
    }

    [Fact]
    public void TheMonitorNeverEvaluatesThePackagesConfigAsPowerShell()
    {
        var task = RemoteTestService.BuildTaskScript(false, "Invoke-AppDeployToolkit.exe Install", @"C:\P\App", @"C:\P\App\Application",
            @"C:\P\App\Packman_Console.log", @"C:\P\App\Application\Config\config.psd1");
        Assert.DoesNotContain("ExpandString", task);
        Assert.DoesNotContain("Invoke-Expression", task);
        Parser.ParseInput(task, out _, out var errors);
        Assert.Empty(errors);
        // The whole command line is one quoted cmd argument, so a quoted program path survives.
        Assert.Contains("'/s /c \"Invoke-AppDeployToolkit.exe Install > \"C:\\P\\App\\Packman_Console.log\" 2>&1\"'", task);
    }

    [Theory]
    [InlineData(@"$env:ProgramData\Logs\Custom", true)]
    [InlineData(@"$envWinDir\Logs\Software2", true)]
    [InlineData(@"$(New-Item -ItemType File -Path '__MARKER__')\Logs", false)]
    [InlineData(@"$env:ProgramData\$(Get-Date)", false)]
    public void LogPathsFromThePackageAreExpandedAsTextOnly(string logPath, bool watched)
    {
        // Runs the monitor's own expansion block against a config holding this LogPath.
        var marker = Path.Combine(_root, "evaluated.txt");
        var config = Path.Combine(_root, "config.psd1");
        File.WriteAllText(config, "@{ Toolkit = @{ LogPath = '" + logPath.Replace("__MARKER__", marker).Replace("'", "''") + "' } }");

        var task = RemoteTestService.BuildTaskScript(false, "x.exe", @"C:\P", @"C:\P", @"C:\P\c.log", config);
        var start = task.IndexOf("$logDirs = @(", StringComparison.Ordinal);
        var end = task.IndexOf("\"Watching PSADT logs in:", StringComparison.Ordinal);
        var block = "$configPath = '" + config + "'\n" + task[start..end] + "\n$logDirs -join '|'";

        var output = RunPowerShell(block);
        Assert.False(File.Exists(marker), "the package's LogPath was evaluated as PowerShell");
        var programData = Environment.GetEnvironmentVariable("ProgramData")!;
        if (watched) Assert.Contains(logPath.StartsWith("$envWinDir") ? @"\Logs\Software2" : programData + @"\Logs\Custom", output);
        else Assert.Contains("does not evaluate", output);
    }

    [Fact]
    public void StagingChecksTheFoldersAboveTheStagingFolder()
    {
        var script = RemoteTestService.BuildSecureStagingScript(@"C:\ProgramData\Packman");
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
        Assert.Contains("Get-UntrustedAncestor", script);

        // The check itself, run for real (reading ACLs needs no elevation): the default parent
        // passes, a folder in the user's profile does not.
        var start = script.IndexOf("function Get-UntrustedAncestor", StringComparison.Ordinal);
        var end = script.IndexOf("function New-SecureBase", StringComparison.Ordinal);
        var function = "$trusted = @('S-1-5-18', 'S-1-5-32-544'); $sidType = [System.Security.Principal.SecurityIdentifier]\n" + script[start..end];

        Assert.Equal("", RunPowerShell(function + "\n$base = 'C:\\ProgramData\\Packman'; \"$(Get-UntrustedAncestor)\"").Trim());
        var inProfile = RunPowerShell(function + $"\n$base = '{Path.Combine(_root, "Packman")}'; \"$(Get-UntrustedAncestor)\"").Trim();
        Assert.NotEqual("", inProfile);
    }

    [Fact]
    public void CopyAndClearReadOnlyNeverFollowALink()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "secret");
        File.SetAttributes(secret, FileAttributes.ReadOnly);

        var package = Directory.CreateDirectory(Path.Combine(_root, "package")).FullName;
        File.WriteAllText(Path.Combine(package, "real.txt"), "real");
        var link = Path.Combine(package, "Docs");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        if (!Directory.Exists(link)) return;   // junctions unavailable on this volume

        var copy = Path.Combine(_root, "copy");
        DirectoryCopy.Copy(package, copy);
        Assert.True(File.Exists(Path.Combine(copy, "real.txt")));
        Assert.False(File.Exists(Path.Combine(copy, "Docs", "secret.txt")));

        DirectoryCopy.ClearReadOnlyRecursive(package);
        Assert.True(new FileInfo(secret).IsReadOnly, "read-only was cleared on a file outside the package");
    }

    [Fact]
    public void TheUploadLogFileIsRedactedLikeTheConsole()
    {
        UploadLogger.ConfiguredFolder = Path.Combine(_root, "logs");
        string path;
        using (var log = new UploadLogger("Packman-QA-redaction"))
        {
            log.Info("PUT https://store.blob.core.windows.net/c/blob?sv=2024&sig=SECRETSIGNATURE done");
            log.LogMetadata("Authorization", "Bearer abcdefghijklmnopqrstuvwxyz0123456789");
            path = log.LogFilePath;
        }
        var text = File.ReadAllText(path);
        Assert.Contains("https://store.blob.core.windows.net/c/blob", text);
        Assert.DoesNotContain("SECRETSIGNATURE", text);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789", text);
    }

    [Fact]
    public void AFolderNamedLikeAPlaceholderIsNotExpanded()
    {
        var helper = LocalTestRunner.BuildHelperScript("'secure-step'", "'task-step'", @"C:\Share\__TASK_SCRIPT__\1.0", @"C:\ProgramData\Packman\__SECURE___1.0", "Task", false);
        Parser.ParseInput(helper, out _, out var errors);
        Assert.Empty(errors);
        Assert.Contains(@"$source = 'C:\Share\__TASK_SCRIPT__\1.0'", helper);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(helper, "'task-step'"));
    }

    private static string RunPowerShell(string script)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
