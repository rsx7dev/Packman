using System.Diagnostics;
using System.IO;
using System.Management.Automation.Language;
using System.Text;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// Remote test on this computer: the helper script and its pipe protocol, run here without
/// elevation and with stand-in secure and task scripts, so nothing is staged under ProgramData
/// and no scheduled task is registered.
/// </summary>
public sealed class LocalTestRunnerTests : IDisposable
{
    private const string NoSuchTask = "Packman_Tests_NoSuchTask";
    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-local-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (string Source, string Staged) Folders()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "Application"));
        File.WriteAllText(Path.Combine(source, "Application", "a.txt"), "a");
        File.WriteAllText(Path.Combine(source, "b.txt"), "bb");
        return (source, Path.Combine(_root, "staged", "App_1.0"));
    }

    [Fact]
    public void ThisComputerIsRecognisedByItsNames()
    {
        Assert.True(LocalTestRunner.IsLocalTarget("localhost"));
        Assert.True(LocalTestRunner.IsLocalTarget("LOCALHOST"));
        Assert.True(LocalTestRunner.IsLocalTarget("127.0.0.1"));
        Assert.True(LocalTestRunner.IsLocalTarget(Environment.MachineName.ToLowerInvariant()));
        Assert.False(LocalTestRunner.IsLocalTarget("TEST-PC-01"));
        Assert.False(LocalTestRunner.IsLocalTarget(Environment.MachineName + "-2"));
        Assert.False(LocalTestRunner.IsLocalTarget(""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRealHelperScriptParses(bool runAsUser)
    {
        var task = RemoteTestService.BuildTaskScript(runAsUser, "Invoke-AppDeployToolkit.exe Install",
            @"C:\ProgramData\Packman\O'Brien_1.0", @"C:\ProgramData\Packman\O'Brien_1.0\Application",
            @"C:\ProgramData\Packman\O'Brien_1.0\Packman_Console.log", @"C:\ProgramData\Packman\O'Brien_1.0\Application\Config\config.psd1",
            eachSecond: $"'{LocalTestRunner.Heartbeat}'");
        Assert.Contains($"'{LocalTestRunner.Heartbeat}'", task);

        var helper = LocalTestRunner.BuildHelperScript(RemoteTestService.BuildSecureStagingScript(@"C:\ProgramData\Packman"),
            task, @"\\server\share\O'Brien\1.0", @"C:\ProgramData\Packman\O'Brien_1.0", "Packman_RemoteTest", cleanupAfterRun: true);
        Parser.ParseInput(helper, out _, out var errors);
        Assert.Empty(errors);
        Assert.DoesNotContain("__", helper.Replace(LocalTestRunner.Heartbeat, "").Replace(LocalTestRunner.Failed, ""));

        // A remote run's task script is unchanged: no heartbeat in its loop.
        Assert.DoesNotContain(LocalTestRunner.Heartbeat, RemoteTestService.BuildTaskScript(runAsUser, "x.exe", "p", "w", "c", "g"));
    }

    [Fact]
    public async Task ARunSecuresCopiesRunsAndCleansUpInOrder()
    {
        var (source, staged) = Folders();
        var helper = LocalTestRunner.BuildHelperScript(
            secureScript: "'[OK] secured'",
            taskScript: $"'Deployment task started'; '{LocalTestRunner.Heartbeat}'; \"two`nlines\"; Write-Error 'soft'; 'PACKMAN_EXIT_CODE:3010'",
            source, staged, NoSuchTask, cleanupAfterRun: true);

        var lines = new List<string>();
        await LocalTestRunner.RunAsync(helper, lines.Add, CancellationToken.None, elevate: false);

        Assert.Equal("[OK] secured", lines[0]);
        Assert.StartsWith("Copying files to ", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("[OK] 2 files in sync", StringComparison.Ordinal));
        Assert.DoesNotContain(LocalTestRunner.Heartbeat, lines);
        Assert.Contains("two", lines);
        Assert.Contains("lines", lines);
        Assert.Contains(lines, l => l.StartsWith("ERROR: soft", StringComparison.Ordinal));
        Assert.True(lines.IndexOf("PACKMAN_EXIT_CODE:3010") < lines.IndexOf("[OK] Cleanup completed"));
        Assert.False(Directory.Exists(staged));
    }

    [Fact]
    public async Task TheStagedCopyIsKeptWithoutCleanup()
    {
        var (source, staged) = Folders();
        var helper = LocalTestRunner.BuildHelperScript("'ok'", "'PACKMAN_EXIT_CODE:0'", source, staged, NoSuchTask, cleanupAfterRun: false);
        await LocalTestRunner.RunAsync(helper, _ => { }, CancellationToken.None, elevate: false);
        Assert.Equal("a", File.ReadAllText(Path.Combine(staged, "Application", "a.txt")));
    }

    [Fact]
    public async Task AFailureStopsTheRunAndIsReported()
    {
        var (source, staged) = Folders();
        var helper = LocalTestRunner.BuildHelperScript("throw 'the folder is not limited to SYSTEM'", "'never'", source, staged, NoSuchTask, false);

        var lines = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LocalTestRunner.RunAsync(helper, lines.Add, CancellationToken.None, elevate: false));
        Assert.Contains("not limited to SYSTEM", ex.Message);
        Assert.DoesNotContain("never", lines);
        Assert.False(Directory.Exists(staged));   // nothing was copied after the failed secure step
    }

    [Fact]
    public async Task CancellingClosesThePipeAndTheHelperStopsByItself()
    {
        var (source, staged) = Folders();
        var done = Path.Combine(_root, "helper-finished.txt");
        // Stands in for the wait loop: a heartbeat every 200 ms, for far longer than the test.
        var task = $"'started'; try {{ for ($i = 0; $i -lt 600; $i++) {{ Start-Sleep -Milliseconds 200; '{LocalTestRunner.Heartbeat}' }} }} finally {{ Set-Content -LiteralPath '{done}' -Value 'x' }}";
        var helper = LocalTestRunner.BuildHelperScript("'ok'", task, source, staged, NoSuchTask, false);

        using var cts = new CancellationTokenSource();
        var lines = new List<string>();
        var run = LocalTestRunner.RunAsync(helper, line => { lines.Add(line); if (line == "started") cts.CancelAfter(500); }, cts.Token, elevate: false);

        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(40), $"cancel took {timer.Elapsed}");
        Assert.True(File.Exists(done), "the helper carried on after Packman closed the pipe");
    }

    [Fact]
    public void ASwappedScriptIsNotRun()
    {
        var script = Path.Combine(_root, "helper.ps1");
        var marker = Path.Combine(_root, "ran.txt");
        File.WriteAllText(script, $"Set-Content -LiteralPath '{marker}' -Value 'x'", new UTF8Encoding(false));
        var realHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(script)));

        Assert.Equal(87, Run(LocalTestRunner.BuildBootstrap(script, new string('0', 64), "pipe")));
        Assert.False(File.Exists(marker));

        Assert.Equal(0, Run(LocalTestRunner.BuildBootstrap(script, realHash, "pipe")));
        Assert.True(File.Exists(marker));
    }

    private static int Run(string bootstrap)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)),
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
