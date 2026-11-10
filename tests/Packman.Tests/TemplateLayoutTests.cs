using System.IO;
using System.Management.Automation.Language;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// The generator fills in every PSADT v4 template shape: 4.0/4.1 deployment functions with
/// "&lt;Perform … tasks here&gt;" markers, 4.2+ phase blocks, and a template left with only
/// "## MARK:" headings. Install context lands in $adtSession whether or not the template has it.
/// </summary>
public sealed class TemplateLayoutTests : IDisposable
{
    // The shape of the PSADT 4.2 template: no RequireAdmin, phases as New-Variable blocks.
    private const string V42 = """
        $adtSession = @{
            AppVendor = ''
            AppName = ''
            AppVersion = ''
            AppArch = ''
            AppScriptDate = '2026-08-20'
            AppScriptAuthor = '<author name>'

            DeployAppScriptFriendlyName = $MyInvocation.MyCommand.Name
            DeployAppScriptParameters = $PSBoundParameters
            DeployAppScriptVersion = '4.2.0'
        }

        ## MARK: Pre-Install
        New-Variable -Name Pre-Install -Force -Value {
            Show-ADTInstallationProgress
        }

        ## MARK: Install
        New-Variable -Name Install -Force -Value {
        }

        ## MARK: Post-Install
        New-Variable -Name Post-Install -Force -Value {
        }

        ## MARK: Uninstall
        New-Variable -Name Uninstall -Force -Value {
        }

        ## MARK: Repair
        New-Variable -Name Repair -Force -Value {
        }

        """;

    // Markers removed, only the 4.1 MARK headings left.
    private const string HeadingsOnly = """
        $adtSession = @{
            AppName = ''
            RequireAdmin = $true
        }
        function Install-ADTDeployment
        {
            ##================================================
            ## MARK: Install
            ##================================================
            $adtSession.InstallPhase = $adtSession.DeploymentType

            Write-ADTLogEntry 'after'
        }

        """;

    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-layout-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string BundledScript =>
        File.ReadAllText(Path.Combine(RepoRoot(), "Packman", "PSADT", "Application", PsadtLayout.ScriptName));

    // From this source file, so it works whatever folder the tests are built into.
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [Fact]
    public void LayoutsAreRecognised()
    {
        Assert.Equal(PhaseLayout.Markers, PsadtScript.Parse(BundledScript).DetectLayout());
        Assert.Equal(PhaseLayout.PhaseBlocks, PsadtScript.Parse(V42).DetectLayout());
        Assert.Equal(PhaseLayout.MarkHeadings, PsadtScript.Parse(HeadingsOnly).DetectLayout());
        Assert.Equal(PhaseLayout.None, PsadtScript.Parse("Write-Host 'no phases'\n").DetectLayout());
        Assert.Equal("4.2.0", PsadtScript.Parse(V42).ToolkitVersion);
    }

    [Fact]
    public void PhaseBlocksGetTheCommandInsideTheRightBlock()
    {
        var script = PsadtScript.Parse(V42);
        Assert.Equal(PhaseLayout.PhaseBlocks, script.InsertIntoPhase(PsadtScript.InstallSection, "", "## MSI Installation", "Start-ADTMsiProcess -Action 'Install'"));
        Assert.Equal(PhaseLayout.PhaseBlocks, script.InsertIntoPhase(PsadtScript.UninstallSection, "Start-ADTMsiProcess -Action 'Uninstall'"));

        var ast = Parser.ParseInput(script.Content, out _, out var errors);
        Assert.Empty(errors);
        Assert.Contains("New-Variable -Name Install -Force -Value {\n\n    ## MSI Installation\n    Start-ADTMsiProcess -Action 'Install'\n}", script.Content.Replace("\r\n", "\n"));
        Assert.Contains("New-Variable -Name Uninstall -Force -Value {\n    Start-ADTMsiProcess -Action 'Uninstall'\n}", script.Content.Replace("\r\n", "\n"));
        // Pre-Install and Post-Install are not the Install phase.
        Assert.DoesNotContain("Show-ADTInstallationProgress\n    Start-ADTMsiProcess", script.Content.Replace("\r\n", "\n"));
        Assert.Empty(PackagePreflight.EmptyPhases(ast));
    }

    [Fact]
    public void MarkHeadingsInsertAfterTheInstallPhaseLine()
    {
        var script = PsadtScript.Parse(HeadingsOnly);
        Assert.Equal(PhaseLayout.MarkHeadings, script.InsertIntoPhase(PsadtScript.InstallSection, "Start-ADTProcess -FilePath 'x'"));
        var text = script.Content.Replace("\r\n", "\n");
        Assert.True(text.IndexOf("InstallPhase", StringComparison.Ordinal) < text.IndexOf("Start-ADTProcess", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Start-ADTProcess", StringComparison.Ordinal) < text.IndexOf("Write-ADTLogEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSessionKeysAreAddedBeforeTheScriptEntries()
    {
        var script = PsadtScript.Parse(V42);
        Assert.Null(script.GetSessionValue("RequireAdmin"));
        Assert.True(script.SetOrAddSessionBool("RequireAdmin", false));
        Assert.Equal("$false", script.GetSessionValue("RequireAdmin"));
        Assert.Equal("User", script.InstallContext);

        var text = script.Content.Replace("\r\n", "\n");
        Assert.Contains("    RequireAdmin = $false\n    DeployAppScriptFriendlyName", text);
        Assert.Empty(Parser.ParseInput(script.Content, out _, out var errors).FindAll(_ => false, true));
        Assert.Empty(errors);

        // Present keys are replaced in place, not duplicated.
        Assert.True(script.SetOrAddSessionBool("RequireAdmin", true));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script.Content, "RequireAdmin ="));
        Assert.False(PsadtScript.Parse("Write-Host 'no session'\n").SetOrAddSessionValue("AppName", "x"));
    }

    [Fact]
    public void EmptyPhaseBlocksAreAPublishBlocker()
    {
        var ast = Parser.ParseInput(V42, out _, out _);
        Assert.Equal(new[] { "Install", "Uninstall" }, PackagePreflight.EmptyPhases(ast).ToArray());
    }

    [Theory]
    [InlineData("bundled", "User")]
    [InlineData("v42", "System")]
    [InlineData("v42", "User")]
    public async Task GeneratorFillsInEveryLayout(string template, string context)
    {
        var templateFolder = Path.Combine(_root, "template-" + template);
        Directory.CreateDirectory(templateFolder);
        File.WriteAllText(Path.Combine(templateFolder, PsadtLayout.ScriptName), template == "v42" ? V42 : BundledScript);
        var msi = Path.Combine(_root, "app.msi");
        File.WriteAllText(msi, "");

        var output = Path.Combine(_root, "out-" + template + context);
        Directory.CreateDirectory(output);
        var generator = new PSADTGenerator(output, templateFolder, new AppSettings.PackageLayoutConfig());
        var result = await generator.CreatePackageAsync(new ApplicationInfo
        {
            Manufacturer = "Contoso",
            Name = "App",
            Version = "1.0",
            InstallContext = context,
            SourcesPath = msi,
            MsiProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}",
        });

        Assert.Empty(result.Warnings);
        var script = PsadtScript.Load(PsadtLayout.ScriptPath(result.PackagePath));
        Assert.Equal("App", script.AppName);
        Assert.Equal(context, script.InstallContext);
        Assert.Contains("Start-ADTMsiProcess -Action 'Install'", script.Content);
        Assert.Contains("-Action 'Uninstall' -FilePath '{3FA85F64-5717-4562-B3FC-2C963F66AFA6}'", script.Content);

        var ast = Parser.ParseInput(script.Content, out _, out var errors);
        Assert.Empty(errors);
        Assert.Empty(PackagePreflight.EmptyPhases(ast));
    }
}
