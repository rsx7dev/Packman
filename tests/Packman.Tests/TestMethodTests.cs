using System.IO;
using System.Management.Automation.Language;
using System.Text;
using System.Xml.Linq;
using Packman.Models;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// The PsExec and Windows Sandbox test methods, as far as they go without a target: what is
/// generated and handed to PsExec, PowerShell and the sandbox. Nothing is started.
/// </summary>
public sealed class TestMethodTests : IDisposable
{
    private const string Staged = @"C:\ProgramData\Packman\Vendor_App_1.0";

    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-method-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string TaskScript(string eachSecond = "") => RemoteTestService.BuildTaskScript(false,
        "Invoke-AppDeployToolkit.exe Install -DeployMode Silent", Staged, Staged + @"\Application",
        Staged + @"\Packman_Console.log", Staged + @"\Application\Config\config.psd1", eachSecond);

    private static void AssertParses(string script)
    {
        Parser.ParseInput(script, out _, out var errors);
        Assert.True(errors.Length == 0, string.Join("; ", errors.Select(e => $"line {e.Extent.StartLineNumber}: {e.Message}")));
    }

    // ── PsExec ─────────────────────────────────────────────────────────
    [Fact]
    public void PsExecRunsWindowsPowerShellAsSystemWithAnEncodedBootstrap()
    {
        var arguments = PsExecTestRunner.BuildArguments("TEST-PC-01", "exit 0");

        Assert.Equal(@"\\TEST-PC-01", arguments[0]);
        Assert.Contains("-accepteula", arguments);
        Assert.Contains("-s", arguments);
        Assert.Equal("powershell.exe", arguments[4]);
        Assert.Contains("-NonInteractive", arguments);
        Assert.Equal("-EncodedCommand", arguments[^2]);
        Assert.Equal("exit 0", Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1])));
    }

    [Fact]
    public void TheBootstrapRefusesAScriptWithAnotherHashAndReadsItFromWindowsTemp()
    {
        var bootstrap = PsExecTestRunner.BuildBootstrap("Packman_abc.ps1", "AB12");

        AssertParses(bootstrap);
        Assert.Contains(@"Join-Path $env:SystemRoot 'Temp\Packman_abc.ps1'", bootstrap);
        Assert.Contains("if($h -ne 'AB12'){exit 87}", bootstrap);
        // The check comes before the script is run.
        Assert.True(bootstrap.IndexOf("exit 87", StringComparison.Ordinal) < bootstrap.IndexOf("[scriptblock]::Create", StringComparison.Ordinal));
    }

    [Fact]
    public void AWrappedScriptStillParsesAndEndsWithTheDoneLine()
    {
        var wrapped = PsExecTestRunner.WrapScript(TaskScript());

        AssertParses(wrapped);
        Assert.EndsWith($"'{PsExecTestRunner.Done}'\n", wrapped);
        Assert.Contains("\"ERROR: $_\"", wrapped);
    }

    [Fact]
    public void PsExecIsTakenFromSettingsFirstThenFromRootedPathFolders()
    {
        var tools = Directory.CreateDirectory(Path.Combine(_folder, "tools")).FullName;
        var onPath = Path.Combine(tools, "PsExec64.exe");
        File.WriteAllText(onPath, "");
        var own = Path.Combine(_folder, "PsExec.exe");
        File.WriteAllText(own, "");
        var pathVariable = string.Join(Path.PathSeparator, "relative", Path.Combine(_folder, "missing"), tools);

        Assert.Equal(onPath, PsExecTestRunner.Find("", pathVariable));
        Assert.Equal(own, PsExecTestRunner.Find($"\"{own}\"", pathVariable));
        // A path the packager set and that is wrong is an error, not a reason to use another PsExec.
        Assert.Null(PsExecTestRunner.Find(Path.Combine(_folder, "gone.exe"), pathVariable));
        Assert.Null(PsExecTestRunner.Find(null, "relative"));
    }

    [Fact]
    public void ThePsExecPathSettingSaysWhatIsWrongWithIt()
    {
        var own = Path.Combine(_folder, "PsExec64.exe");
        File.WriteAllText(own, "");
        var other = Path.Combine(_folder, "notepad.exe");
        File.WriteAllText(other, "");

        Assert.Equal("", PsExecTestRunner.PathProblem(""));
        Assert.Equal("", PsExecTestRunner.PathProblem(own));
        Assert.Contains("folder", PsExecTestRunner.PathProblem(_folder));
        Assert.Contains("does not exist", PsExecTestRunner.PathProblem(Path.Combine(_folder, "gone.exe")));
        Assert.Contains("not PsExec", PsExecTestRunner.PathProblem(other));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Connecting to TEST-PC-01...", true)]
    [InlineData("Starting PSEXESVC service on TEST-PC-01...", true)]
    [InlineData("powershell.exe exited on TEST-PC-01 with error code 0.", true)]
    [InlineData("Couldn't access TEST-PC-01:", false)]
    [InlineData("Access is denied.", false)]
    public void OnlyWhatPsExecSaysAboutItsOwnProgressIsLeftOut(string line, bool chatter) =>
        Assert.Equal(chatter, PsExecTestRunner.IsChatter(line));

    [Fact]
    public async Task APsExecRunWithoutPsExecSaysWhereToGetIt()
    {
        var package = Fakes.Package(Path.Combine(_folder, "Contoso_App", "1.0"));
        File.WriteAllText(Path.Combine(package, "Application", "Invoke-AppDeployToolkit.exe"), "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new RemoteTestService().DeployAsync(
            "TEST-PC-DOES-NOT-EXIST", package, "Install", "Invoke-AppDeployToolkit.exe Install", false, false, _ => { },
            method: RemoteTestMethod.PsExec, psExecPath: Path.Combine(_folder, "gone", "PsExec64.exe")));

        Assert.Equal(RemoteTestService.PsExecMissing, ex.Message);
    }

    // ── Windows Sandbox ────────────────────────────────────────────────
    [Theory]
    [InlineData(true, "Enable")]
    [InlineData(false, "Disable")]
    public void TheSandboxMapsThePackageReadOnlyAndTheSessionWritable(bool networking, string expected)
    {
        var xml = XElement.Parse(SandboxTestRunner.BuildConfiguration(@"C:\Users\A & B\Package", @"C:\Users\A & B\Session", networking));

        Assert.Equal(expected, xml.Element("Networking")!.Value);
        var folders = xml.Element("MappedFolders")!.Elements("MappedFolder").ToList();
        Assert.Equal(2, folders.Count);
        Assert.Equal(@"C:\Users\A & B\Package", folders[0].Element("HostFolder")!.Value);
        Assert.Equal(SandboxTestRunner.PackageFolderInSandbox, folders[0].Element("SandboxFolder")!.Value);
        Assert.Equal("true", folders[0].Element("ReadOnly")!.Value);
        Assert.Equal(SandboxTestRunner.SessionFolderInSandbox, folders[1].Element("SandboxFolder")!.Value);
        Assert.Equal("false", folders[1].Element("ReadOnly")!.Value);
        Assert.Contains($@"-File {SandboxTestRunner.SessionFolderInSandbox}\run.ps1", xml.Element("LogonCommand")!.Element("Command")!.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSandboxScriptParsesStagesThePackageAndClosesOnlyWhenAsked(bool close)
    {
        var script = SandboxTestRunner.BuildLogonScript(RemoteTestService.BuildSecureStagingScript(@"C:\ProgramData\Packman"),
            TaskScript($"'{SandboxTestRunner.Heartbeat}'"), @"C:\ProgramData\Packman\O'Reilly_App_1.0", close);

        AssertParses(script);
        Assert.Contains(@"$staged = 'C:\ProgramData\Packman\O''Reilly_App_1.0'", script);
        Assert.Contains($"$source = '{SandboxTestRunner.PackageFolderInSandbox}'", script);
        Assert.Contains($"Send '{SandboxTestRunner.Done}'", script);
        Assert.Contains(close ? "if ($true) { Stop-Computer -Force }" : "if ($false) { Stop-Computer -Force }", script);
        Assert.DoesNotMatch("__[A-Z_]+__(?<!__PACKMAN_[A-Z]+__)", script);
    }

    [Fact]
    public void ALineIsReportedOnlyOnceItsLineBreakHasArrived()
    {
        var path = Path.Combine(_folder, "output.log");
        File.WriteAllBytes(path, []);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new UTF8Encoding(false).GetDecoder();
        var partial = new StringBuilder();

        File.AppendAllText(path, "first\r\nsec");
        Assert.Equal(["first"], SandboxTestRunner.ReadNewLines(reader, decoder, partial));
        Assert.Empty(SandboxTestRunner.ReadNewLines(reader, decoder, partial));

        File.AppendAllText(path, "ond é\r\n\r\nthird\r\n");
        Assert.Equal(["second é", "", "third"], SandboxTestRunner.ReadNewLines(reader, decoder, partial));
    }

    [Fact]
    public void TheMethodIsSavedWithItsNameAndDefaultsToWinRm()
    {
        var settings = new AppSettings();
        Assert.Equal(RemoteTestMethod.WinRM, settings.RemoteTest.Method);
        Assert.True(settings.RemoteTest.SandboxNetworking);

        settings.RemoteTest.Method = RemoteTestMethod.Sandbox;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Contains("\"Method\":\"Sandbox\"", json);
        Assert.Equal(RemoteTestMethod.Sandbox, System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.RemoteTest.Method);
    }
}
