using System.Diagnostics;
using System.IO;
using System.Management.Automation.Language;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>Remote test plumbing that runs without a target: the copy's cancellation and the task script per context.</summary>
public sealed class RemoteTestServiceTests : IDisposable
{
    private const string Package = @"C:\ProgramData\Packman\Vendor_App_1.0";

    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-remote-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static HashSet<int> RobocopyIds() => Process.GetProcessesByName("robocopy").Select(p => p.Id).ToHashSet();

    [Fact]
    public async Task CancellingTheCopyKillsRobocopy()
    {
        var source = Directory.CreateDirectory(Path.Combine(_folder, "source")).FullName;
        var destination = Path.Combine(_folder, "destination");
        var lockedFile = Path.Combine(source, "locked.bin");
        File.WriteAllText(lockedFile, "x");

        // Robocopy cannot read a file held open without sharing, so it sits in its /R:2 /W:5 retries.
        using var hold = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var before = RobocopyIds();
        using var cts = new CancellationTokenSource();
        var copy = RemoteTestService.CopyWithRobocopyAsync(source, destination, null, cts.Token);

        List<Process> ours = [];
        await WizardGateTests.WaitForAsync(() =>
        {
            ours = Process.GetProcessesByName("robocopy").Where(p => !before.Contains(p.Id)).ToList();
            return ours.Count > 0;
        });
        await Task.Delay(500);
        Assert.False(copy.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);

        foreach (var process in ours)
            Assert.True(process.WaitForExit(3000), $"robocopy {process.Id} still running after cancel");
    }

    [Fact]
    public async Task CopyMirrorsTheSource()
    {
        var source = Directory.CreateDirectory(Path.Combine(_folder, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        var destination = Path.Combine(_folder, "destination");
        var reported = new List<int?>();

        await RemoteTestService.CopyWithRobocopyAsync(source, destination, p => reported.Add(p), CancellationToken.None);

        Assert.Equal("a", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.Equal(100, reported[^1]);
    }

    private static string TaskScript(bool runAsUser) => RemoteTestService.BuildTaskScript(
        runAsUser, "Invoke-AppDeployToolkit.exe Install -DeployMode Silent", Package,
        Package + @"\Application", Package + @"\Packman_Console.log", Package + @"\Application\Config\config.psd1");

    [Fact]
    public void UserRunCanReadItsPackageAndWriteOnlyItsLog()
    {
        var script = TaskScript(runAsUser: true);

        Assert.Contains($"Get-Item -LiteralPath '{Package}'", script);
        Assert.Contains("'ReadAndExecute', 'ContainerInherit, ObjectInherit'", script);
        Assert.Contains("FileSystemAccessRule($userSid, 'Modify', 'Allow')", script);
        Assert.DoesNotContain("FullControl", script);
        // The log is created for the user after the stale one is removed.
        Assert.True(script.IndexOf("Remove-Item -LiteralPath $consoleLog", StringComparison.Ordinal)
                    < script.IndexOf("[IO.File]::WriteAllText($consoleLog", StringComparison.Ordinal));
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void SystemRunGrantsNothing()
    {
        var script = TaskScript(runAsUser: false);

        Assert.Contains("New-ScheduledTaskPrincipal -UserId 'S-1-5-18'", script);
        Assert.DoesNotContain("AddAccessRule", script);
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("3010", 3010)]
    [InlineData(" 1603 ", 1603)]
    [InlineData("-3", -3)]
    // The task scheduler prints the code unsigned: an HRESULT, a crash and exit -1.
    [InlineData("2147944003", unchecked((int)0x80070643))]
    [InlineData("3221225477", unchecked((int)0xC0000005))]
    [InlineData("4294967295", -1)]
    // No readable code is a failure, never 0.
    [InlineData("", -1)]
    [InlineData("abc", -1)]
    [InlineData("99999999999", -1)]
    public void ExitCodeIsReadWithoutTurningAFailureIntoSuccess(string printed, int expected)
    {
        var code = RemoteTestService.ParseExitCode(printed);

        Assert.Equal(expected, code);
        Assert.Equal(expected is 0 or 3010 or 1641, RemoteTestService.IsSuccess(code));
    }
}
