using System.IO;
using System.Management.Automation.Language;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Packman.Tests;

/// <summary>
/// Create and upgrade end to end: the real PSADT templates (the bundled 4.1.8 one and a 4.2.0
/// one when it is on this machine), real MSI and EXE installer files, hostile application names,
/// cancellation, overwrite and read-only templates. Everything the generator writes is read back
/// through <see cref="PsadtScript"/> and the PowerShell parser, never by string matching alone.
///
/// Tests that need installer files or the 4.2 template from the packager's own Desktop return
/// without asserting when those files are absent, so the suite still passes on another machine.
/// <see cref="TemplateLayoutTests"/> covers the three phase layouts on synthetic scripts and
/// <see cref="PackageFolderNameTests"/> the folder name format; this file does not repeat either.
/// </summary>
public sealed class PackagingEndToEndTests(ITestOutputHelper output) : IDisposable
{
    // Read-only inputs on this machine. Never executed, only read and copied.
    private const string DesktopInstallers = @"C:\Users\RS\Desktop\IntuneApps\_TestInstallers";
    private const string DesktopTemplate42 = @"C:\Users\RS\Desktop\IntuneApps\PSAppDeployToolkit_Template_v4";

    private static string MsiPutty => Path.Combine(DesktopInstallers, "putty-64bit-0.83-installer.msi");
    private static string Msi7z2408 => Path.Combine(DesktopInstallers, "7z2408-x64.msi");
    private static string Msi7z2409 => Path.Combine(DesktopInstallers, "7z2409-x64.msi");
    private static string ExeNotepadPlusPlus => Path.Combine(DesktopInstallers, "npp.8.7.1.Installer.x64.exe");
    private static string Exe7z => Path.Combine(DesktopInstallers, "7z2409.exe");

    /// <summary>The 4.1.8 template that ships in the repository; always available.</summary>
    private static string BundledTemplate => Path.Combine(RepoRoot(), "Packman", "PSADT", "Application");

    // From this source file, so it works whatever folder the tests are built into.
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    /// <summary>True when one of the machine-local inputs is not here, so the test should skip.</summary>
    private static bool Absent(params string[] paths) => paths.Any(p => !File.Exists(p) && !Directory.Exists(p));

    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-e2e-").FullName;

    public void Dispose()
    {
        // The templates ship read-only files; clear the flag or the delete fails.
        try { DirectoryCopy.ClearReadOnlyRecursive(_root); Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string Bundled41 = "bundled-4.1.8";
    private const string Real42 = "desktop-4.2.0";

    private static string FullTemplate(string which) => which == Real42 ? DesktopTemplate42 : BundledTemplate;

    private string Share(string name)
    {
        var share = Path.Combine(_root, name);
        Directory.CreateDirectory(share);
        return share;
    }

    /// <summary>
    /// A template holding one real template's script plus the two files preflight requires:
    /// the same script editing as the full template without copying 20 MB of toolkit.
    /// </summary>
    private string TrimmedTemplate(string name, string which = Bundled41, string? scriptTail = null)
    {
        var folder = Path.Combine(_root, "template-" + name);
        Directory.CreateDirectory(Path.Combine(folder, PsadtLayout.ToolkitFolderName));
        var script = Path.Combine(folder, PsadtLayout.ScriptName);
        File.Copy(Path.Combine(FullTemplate(which), PsadtLayout.ScriptName), script, overwrite: true);
        DirectoryCopy.ClearReadOnly(script);
        if (scriptTail != null) File.AppendAllText(script, scriptTail);
        File.WriteAllText(Path.Combine(folder, PsadtLayout.SetupFileName), "");
        File.WriteAllText(Path.Combine(folder, PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psd1"), "@{}\n");
        return folder;
    }

    private static AppSettings.PackageLayoutConfig Layout(params string[] extraFolders) =>
        new() { IconFolder = true, ExtraFolders = [.. extraFolders] };

    private static PsadtScript ScriptOf(string packagePath) => PsadtScript.Load(PsadtLayout.ScriptPath(packagePath));

    /// <summary>The script's AST, asserting it has no parse errors.</summary>
    private static Ast AssertParses(PsadtScript script)
    {
        var ast = Parser.ParseInput(script.Content, out _, out var errors);
        Assert.Empty(errors.Select(e => $"line {e.Extent.StartLineNumber}: {e.Message}"));
        return ast;
    }

    /// <summary>
    /// The deployment phase a command sits in ("Install", "Uninstall", "Repair"), whichever
    /// layout the template uses, or null when it is at the top level of the script.
    /// </summary>
    private static string? PhaseOf(Ast node)
    {
        for (var a = node; a != null; a = a.Parent)
        {
            // 4.0/4.1: function Install-ADTDeployment { … }
            if (a is FunctionDefinitionAst function)
                return function.Name.Replace("-ADTDeployment", "", StringComparison.OrdinalIgnoreCase);

            // 4.2+: New-Variable -Name Install -Force -Value { … }
            if (a is ScriptBlockExpressionAst block && block.Parent is CommandAst command
                && string.Equals(command.GetCommandName(), "New-Variable", StringComparison.OrdinalIgnoreCase))
            {
                var elements = command.CommandElements;
                for (var i = 1; i < elements.Count - 1; i++)
                    if (elements[i] is CommandParameterAst parameter && parameter.ParameterName.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        return elements[i + 1].Extent.Text.Trim('\'', '"');
            }
        }
        return null;
    }

    /// <summary>Every call of <paramref name="commandName"/> in the script, with the phase it sits in.</summary>
    private static List<(string Phase, string Text, CommandAst Command)> Commands(Ast ast, string commandName) =>
        ast.FindAll(n => n is CommandAst c && string.Equals(c.GetCommandName(), commandName, StringComparison.OrdinalIgnoreCase), true)
            .Cast<CommandAst>()
            .Select(c => (PhaseOf(c) ?? "", c.Extent.Text, c))
            .ToList();

    /// <summary>The literal string arguments of a command, which is where our switches and codes land.</summary>
    private static List<string> Literals(CommandAst command) =>
        command.CommandElements.OfType<StringConstantExpressionAst>().Select(s => s.Value).ToList();

    private Task<PackageCreationResult> Generate(string share, string template, ApplicationInfo info,
        AppSettings.PackageLayoutConfig? layout = null, bool overwrite = false, CancellationToken ct = default) =>
        new PSADTGenerator(share, template, layout ?? Layout()).CreatePackageAsync(info, overwrite, ct);

    // ── Generate: real templates, real installers ─────────────────────────────

    [Theory]
    [InlineData(Bundled41)]
    [InlineData(Real42)]
    public async Task GenerateWithARealMsiFillsTheScriptAndPassesPreflight(string which)
    {
        // Skips cleanly when this machine has neither the 4.2 template nor the test installers.
        if (Absent(FullTemplate(which), MsiPutty)) return;

        var msi = MsiInfoService.ExtractMsiInfo(MsiPutty);
        var share = Share("msi-" + which);
        var result = await Generate(share, FullTemplate(which), new ApplicationInfo
        {
            Manufacturer = msi.Manufacturer,
            Name = "PuTTY",
            Version = "0.83",
            Architecture = "x64",
            InstallContext = "System",
            SourcesPath = MsiPutty,
            MsiProductCode = msi.ProductCode,
            InstallArguments = "ALLUSERS=1",
        }, Layout("Docs"));

        Assert.Empty(result.Warnings);
        Assert.Equal(Path.Combine(share, "Simon_Tatham_PuTTY", "0.83"), result.PackagePath);

        // Folder layout: Application holds the toolkit and the installer, Intune starts empty.
        Assert.True(File.Exists(Path.Combine(PsadtLayout.FilesFolder(result.PackagePath), Path.GetFileName(MsiPutty))));
        Assert.True(File.Exists(Path.Combine(PsadtLayout.ApplicationFolder(result.PackagePath), PsadtLayout.SetupFileName)));
        Assert.Equal(new[] { "Application", "Docs", "Icon", "Intune" },
            Directory.EnumerateDirectories(result.PackagePath).Select(p => Path.GetFileName(p)!).Order().ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(PsadtLayout.IntuneFolder(result.PackagePath)));
        // No temp file from the script save is left in the package.
        Assert.Empty(Directory.EnumerateFiles(PsadtLayout.ApplicationFolder(result.PackagePath), "*" + TextFileIO.TempSuffix));

        var script = ScriptOf(result.PackagePath);
        Assert.Equal(which == Real42 ? PhaseLayout.PhaseBlocks : PhaseLayout.Markers, script.DetectLayout());
        Assert.Equal("Simon Tatham", script.Vendor);
        Assert.Equal("PuTTY", script.AppName);
        Assert.Equal("0.83", script.AppVersion);
        Assert.Equal("x64", script.AppArch);
        Assert.Equal(PsadtScript.TodayStamp, script.ScriptDate);
        Assert.Equal(Environment.UserName, script.ScriptAuthor);
        Assert.True(script.RequireAdmin);
        Assert.Equal("System", script.InstallContext);
        Assert.Equal("System", InstallContextParser.ExtractFromPackage(result.PackagePath));
        Assert.Equal(Path.GetFileName(MsiPutty), script.InstallerFileName);
        Assert.Equal([Path.GetFileName(MsiPutty)], script.SourceFileNames);
        Assert.Equal(msi.ProductCode, script.MsiProductCode);
        Assert.False(script.HasSignatureBlock);

        // The install command lands in the Install phase and the uninstall in Uninstall,
        // whichever shape the template uses.
        var ast = AssertParses(script);
        var calls = Commands(ast, "Start-ADTMsiProcess");
        var install = Assert.Single(calls, c => c.Text.Contains("-Action 'Install'"));
        Assert.Equal("Install", install.Phase);
        Assert.Contains(Path.GetFileName(MsiPutty), install.Text);
        Assert.Contains("ALLUSERS=1", Literals(install.Command));

        var uninstall = Assert.Single(calls, c => c.Text.Contains("-Action 'Uninstall'"));
        Assert.Equal("Uninstall", uninstall.Phase);
        Assert.Contains(msi.ProductCode, Literals(uninstall.Command));

        Assert.Empty(PackagePreflight.EmptyPhases(ast));
        Assert.Empty(PackagePreflight.Check(result.PackagePath));
    }

    [Theory]
    [InlineData(Bundled41)]
    [InlineData(Real42)]
    public async Task GenerateWithARealExeKeepsThePlaceholdersUntilSwitchesAreGiven(string which)
    {
        // Skips cleanly when the 4.2 template or the test installers are not on this machine.
        if (Absent(FullTemplate(which), ExeNotepadPlusPlus)) return;

        var share = Share("exe-" + which);
        var info = new ApplicationInfo
        {
            Manufacturer = "Don HO", Name = "Notepad++", Version = "8.7.1", Architecture = "x64",
            InstallContext = "System", SourcesPath = ExeNotepadPlusPlus,
        };

        // No switches: the script carries the placeholders and preflight blocks publishing.
        var blank = await Generate(share, FullTemplate(which), info);
        Assert.Empty(blank.Warnings);
        var placeholderIssue = Assert.Single(PackagePreflight.Check(blank.PackagePath));
        Assert.Contains("placeholder", placeholderIssue);
        var blankScript = ScriptOf(blank.PackagePath);
        Assert.Contains("<silent flags>", blankScript.Content);
        Assert.Contains("<uninstall flags>", blankScript.Content);
        Assert.Equal(Path.GetFileName(ExeNotepadPlusPlus), blankScript.InstallerFileName);
        Assert.Null(blankScript.MsiProductCode);
        AssertParses(blankScript);

        // With the vendor's switches the package is ready and the commands sit in their phases.
        info.Name = "Notepad++ Silent";
        info.InstallArguments = "/S";
        info.UninstallArguments = "/S";
        var ready = await Generate(share, FullTemplate(which), info);
        var script = ScriptOf(ready.PackagePath);
        Assert.DoesNotContain("<silent flags>", script.Content);
        Assert.DoesNotContain("<uninstall flags>", script.Content);

        var ast = AssertParses(script);
        var calls = Commands(ast, "Start-ADTProcess");
        Assert.Equal(2, calls.Count);
        Assert.Equal(["Install", "Uninstall"], calls.Select(c => c.Phase).Order().ToArray());
        Assert.All(calls, c => Assert.Contains("/S", Literals(c.Command)));
        Assert.All(calls, c => Assert.Contains(Path.GetFileName(ExeNotepadPlusPlus), c.Text));
        Assert.Empty(PackagePreflight.EmptyPhases(ast));
        Assert.Empty(PackagePreflight.Check(ready.PackagePath));
    }

    [Fact]
    public async Task UserContextIsWrittenEvenWhenTheTemplateHasNoRequireAdminEntry()
    {
        // The 4.2 template dropped RequireAdmin from $adtSession; skips when it is not here.
        if (Absent(DesktopTemplate42, MsiPutty)) return;

        Assert.Null(PsadtScript.Load(Path.Combine(DesktopTemplate42, PsadtLayout.ScriptName)).GetSessionValue("RequireAdmin"));

        var share = Share("user-context");
        var result = await Generate(share, DesktopTemplate42, new ApplicationInfo
        {
            Manufacturer = "Simon Tatham", Name = "PuTTY User", Version = "0.83", InstallContext = "User",
            SourcesPath = MsiPutty, MsiProductCode = MsiInfoService.ExtractMsiInfo(MsiPutty).ProductCode,
        });

        Assert.Empty(result.Warnings);
        var script = ScriptOf(result.PackagePath);
        Assert.Equal("$false", script.GetSessionValue("RequireAdmin"));
        Assert.Equal("User", script.InstallContext);
        Assert.Equal("User", InstallContextParser.ExtractFromPackage(result.PackagePath));
        AssertParses(script);
        Assert.Empty(PackagePreflight.Check(result.PackagePath));
    }

    [Fact]
    public async Task AnMsiWithoutAProductCodeGetsThePlaceholderAndPreflightFlagsIt()
    {
        var share = Share("no-product-code");
        var template = TrimmedTemplate("no-product-code");
        var msi = Path.Combine(_root, "unreadable.msi");
        File.WriteAllText(msi, "not really an MSI");

        var result = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Mystery", Version = "1.0", SourcesPath = msi, InstallArguments = "/qn",
        });

        // PackageType falls back to the extension, so it is still an MSI command.
        var script = ScriptOf(result.PackagePath);
        Assert.Contains(PsadtScript.ProductCodePlaceholder, script.Content);
        Assert.Equal("unreadable.msi", script.InstallerFileName);
        AssertParses(script);
        var issue = Assert.Single(PackagePreflight.Check(result.PackagePath));
        Assert.Contains("product code", issue);
    }

    [Fact]
    public async Task TheTemplatesSignatureBlockIsStrippedAndEncodingAndLineEndingsSurvive()
    {
        var tail = "\r\n# SIG # Begin signature block\r\n# MIIabc\r\n# SIG # End signature block\r\n";
        var template = TrimmedTemplate("signed", Bundled41, tail);
        var before = TextFileIO.Read(Path.Combine(template, PsadtLayout.ScriptName));
        Assert.Contains(PsadtScript.SignatureBegin, before.Content);

        var share = Share("signed");
        var installer = Path.Combine(_root, "setup.exe");
        File.WriteAllText(installer, "x");
        var result = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Signed", Version = "1.0", SourcesPath = installer, InstallArguments = "/S", UninstallArguments = "/S",
        });

        var after = TextFileIO.Read(PsadtLayout.ScriptPath(result.PackagePath));
        Assert.DoesNotContain(PsadtScript.SignatureBegin, after.Content);
        Assert.DoesNotContain(PsadtScript.SignatureEnd, after.Content);
        // The template's encoding (UTF-8 with BOM) and CRLF endings are kept as they were.
        Assert.Equal(before.Encoding.WebName, after.Encoding.WebName);
        Assert.Equal(before.Encoding.GetPreamble().Length, after.Encoding.GetPreamble().Length);
        Assert.Equal(before.Crlf, after.Crlf);
        Assert.DoesNotContain(after.Content.Split('\n').SkipLast(1), line => !line.EndsWith('\r'));
        AssertParses(ScriptOf(result.PackagePath));
    }

    // ── Overwrite, cancellation, read-only templates ──────────────────────────

    [Fact]
    public async Task GeneratingTwiceNeedsOverwriteAndThenReplacesThePackageWhole()
    {
        var share = Share("overwrite");
        var template = TrimmedTemplate("overwrite");
        var installer = Path.Combine(_root, "setup.exe");
        File.WriteAllText(installer, "x");
        var generator = new PSADTGenerator(share, template, Layout());
        var info = new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "App", Version = "1.0", SourcesPath = installer, InstallArguments = "/S", UninstallArguments = "/S",
        };

        var first = await generator.CreatePackageAsync(info);
        var check = generator.ValidatePackageCreation(info);
        Assert.True(check.PackageExists);
        Assert.Equal(first.PackagePath, check.ExistingPath);

        File.WriteAllText(Path.Combine(first.PackagePath, "stray.txt"), "left over");
        File.WriteAllText(Path.Combine(PsadtLayout.FilesFolder(first.PackagePath), "old-extra.msi"), "x");
        File.AppendAllText(PsadtLayout.ScriptPath(first.PackagePath), "\r\n# hand edit that a rebuild drops\r\n");

        // Without the flag the existing version is protected, and it is left untouched.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.CreatePackageAsync(info));
        Assert.Contains("already exists", refused.Message);
        Assert.True(File.Exists(Path.Combine(first.PackagePath, "stray.txt")));

        var second = await generator.CreatePackageAsync(info, overwriteExisting: true);
        Assert.Equal(first.PackagePath, second.PackagePath);
        Assert.Empty(second.Warnings);
        Assert.False(File.Exists(Path.Combine(second.PackagePath, "stray.txt")));
        Assert.False(File.Exists(Path.Combine(PsadtLayout.FilesFolder(second.PackagePath), "old-extra.msi")));
        var script = ScriptOf(second.PackagePath);
        Assert.DoesNotContain("hand edit that a rebuild drops", script.Content);
        // One install and one uninstall command, not two of each from a second pass.
        var ast = AssertParses(script);
        Assert.Equal(2, Commands(ast, "Start-ADTProcess").Count);
        // No build or backup folder is left beside the package.
        Assert.Equal(["1.0"], Directory.EnumerateDirectories(Path.GetDirectoryName(first.PackagePath)!).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.Empty(PackagePreflight.Check(second.PackagePath));
    }

    [Fact]
    public async Task CancellingMidCopyLeavesNoVersionFolderBehind()
    {
        // The bundled template is thousands of files, so cancelling once the first folder
        // appears reliably lands in the middle of the copy.
        var share = Share("cancel");
        var installer = Path.Combine(_root, "setup.exe");
        File.WriteAllText(installer, "x");
        var expected = Path.Combine(share, "Contoso_Cancelled", "1.0");

        using var cts = new CancellationTokenSource();
        var watcher = Task.Run(() =>
        {
            while (!Directory.Exists(PsadtLayout.ApplicationFolder(expected)) && !cts.IsCancellationRequested)
                Thread.SpinWait(50);
            cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generate(share, BundledTemplate, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Cancelled", Version = "1.0", SourcesPath = installer,
        }, ct: cts.Token));
        await watcher;

        Assert.False(Directory.Exists(expected));
        // The app folder may remain, but it must hold no half-built version.
        Assert.DoesNotContain(Directory.EnumerateDirectories(share, "*", SearchOption.AllDirectories),
            d => Path.GetFileName(d) != "Contoso_Cancelled");
    }

    [Fact]
    public async Task CancellingAnOverwriteKeepsTheExistingVersionExactlyAsItWas()
    {
        var share = Share("cancel-overwrite");
        var installer = Path.Combine(_root, "setup.exe");
        File.WriteAllText(installer, "x");
        var generator = new PSADTGenerator(share, BundledTemplate, Layout());
        var info = new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Kept", Version = "1.0", SourcesPath = installer, InstallArguments = "/S", UninstallArguments = "/S",
        };

        var first = await generator.CreatePackageAsync(info);
        File.WriteAllText(Path.Combine(first.PackagePath, "keep-me.txt"), "mine");
        var scriptBefore = File.ReadAllText(PsadtLayout.ScriptPath(first.PackagePath));

        using var cts = new CancellationTokenSource();
        var buildPath = first.PackagePath + ".packman-new";
        var watcher = Task.Run(() =>
        {
            while (!Directory.Exists(PsadtLayout.ApplicationFolder(buildPath)) && !cts.IsCancellationRequested)
                Thread.SpinWait(50);
            cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.CreatePackageAsync(info, true, cts.Token));
        await watcher;

        Assert.True(File.Exists(Path.Combine(first.PackagePath, "keep-me.txt")));
        Assert.Equal(scriptBefore, File.ReadAllText(PsadtLayout.ScriptPath(first.PackagePath)));
        Assert.Equal(["1.0"], Directory.EnumerateDirectories(Path.GetDirectoryName(first.PackagePath)!).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.Empty(PackagePreflight.Check(first.PackagePath));
    }

    [Fact]
    public async Task AReadOnlyTemplateIsCopiedWritableAndTheVersionCanStillBeRebuilt()
    {
        var template = TrimmedTemplate("read-only");
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
            new FileInfo(file).IsReadOnly = true;

        var share = Share("read-only");
        var installer = Path.Combine(_root, "setup.exe");
        File.WriteAllText(installer, "x");
        var generator = new PSADTGenerator(share, template, Layout());
        var info = new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "ReadOnly", Version = "1.0", SourcesPath = installer, InstallArguments = "/S", UninstallArguments = "/S",
        };

        var first = await generator.CreatePackageAsync(info);
        // The script was still edited, and nothing in the package stayed read-only.
        Assert.Equal("ReadOnly", ScriptOf(first.PackagePath).AppName);
        Assert.DoesNotContain(Directory.EnumerateFiles(first.PackagePath, "*", SearchOption.AllDirectories), f => new FileInfo(f).IsReadOnly);

        // A rebuild has to delete read-only files the packager (or a tool) marked afterwards.
        foreach (var file in Directory.EnumerateFiles(first.PackagePath, "*", SearchOption.AllDirectories))
            new FileInfo(file).IsReadOnly = true;
        var second = await generator.CreatePackageAsync(info, overwriteExisting: true);
        Assert.Equal(first.PackagePath, second.PackagePath);
        Assert.Empty(second.Warnings);
        Assert.Equal(["1.0"], Directory.EnumerateDirectories(Path.GetDirectoryName(first.PackagePath)!).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.Empty(PackagePreflight.Check(second.PackagePath));
    }

    // ── Hostile application names ─────────────────────────────────────────────

    [Theory]
    [InlineData("Say \"Hi\" Reader", "Contoso_Say__Hi__Reader")]   // " is not a folder character
    [InlineData("O'Reilly Tool", "Contoso_O'Reilly_Tool")]         // apostrophe: single-quoted PowerShell
    [InlineData("Jack & Jill", "Contoso_Jack_&_Jill")]
    [InlineData("Ünïcødé 日本語 аpp", "Contoso_Ünïcødé_日本語_аpp")]
    [InlineData("Trailing dots...", "Contoso_Trailing_dots")]      // Windows drops trailing dots
    [InlineData("Trailing space ", "Contoso_Trailing_space")]
    [InlineData("CON", "Contoso_CON")]                             // only a whole reserved name needs the prefix
    [InlineData("a/b:c|d?e*f", "Contoso_a_b_c_d_e_f")]
    [InlineData("Back`tick $env:PATH", "Contoso_Back`tick_$env_PATH")]
    [InlineData("100% Pure", "Contoso_100%_Pure")]
    public async Task HostileNamesAreSanitisedForTheFolderAndKeptExactlyInTheScript(string name, string expectedFolder)
    {
        var share = Share("names-" + Math.Abs(name.GetHashCode()));
        var template = TrimmedTemplate("names-" + Math.Abs(name.GetHashCode()), Bundled41);
        var installer = Path.Combine(_root, "app.msi");
        File.WriteAllText(installer, "x");

        var result = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = name, Version = "1.0 (beta)", SourcesPath = installer,
            MsiProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}", InstallArguments = "ALLUSERS=1",
        });

        Assert.Empty(result.Warnings);
        Assert.Equal(Path.Combine(share, expectedFolder, "1.0 (beta)"), result.PackagePath);

        // The name reaches the script unchanged, and the script is still valid PowerShell.
        var script = ScriptOf(result.PackagePath);
        Assert.Equal(name, script.AppName);
        Assert.Equal("Contoso", script.Vendor);
        Assert.Equal("1.0 (beta)", script.AppVersion);
        var ast = AssertParses(script);
        Assert.Equal(name, Assert.Single(ast.FindAll(n => n is StringConstantExpressionAst s && s.Value == name, true).Cast<StringConstantExpressionAst>()).Value);
        Assert.Empty(PackagePreflight.Check(result.PackagePath));
    }

    [Fact]
    public async Task AReservedDeviceNameAloneInTheFolderNameGetsAPrefix()
    {
        var share = Share("reserved");
        var template = TrimmedTemplate("reserved");
        var installer = Path.Combine(_root, "app.msi");
        File.WriteAllText(installer, "x");

        var result = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "CON", Version = "1.0", SourcesPath = installer,
            MsiProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}",
        }, new AppSettings.PackageLayoutConfig { FolderNameTemplate = "%appName%" });

        Assert.Equal(Path.Combine(share, "_CON", "1.0"), result.PackagePath);
        Assert.Equal("CON", ScriptOf(result.PackagePath).AppName);
    }

    [Fact]
    public async Task AnAppNameTooLongForAFolderFailsWithoutLeavingAVersionBehind()
    {
        var share = Share("long-name");
        var template = TrimmedTemplate("long-name");
        var installer = Path.Combine(_root, "app.msi");
        File.WriteAllText(installer, "x");

        // 200 characters still fit in one folder name and work.
        var ok = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "", Name = new string('a', 200), Version = "1.0", SourcesPath = installer,
            MsiProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}",
        });
        Assert.True(Directory.Exists(ok.PackagePath));
        // Long package paths rely on the machine allowing them; note it rather than assume it.
        output.WriteLine($"200-character app name: package path is {ok.PackagePath.Length} characters");

        // Past the 255-character limit of a single folder name the build fails. Windows'
        // own message is all the packager gets; see the report's note on name length.
        var tooLong = new ApplicationInfo
        {
            Manufacturer = "", Name = new string('b', 256), Version = "1.0", SourcesPath = installer,
            MsiProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}",
        };
        var failure = await Assert.ThrowsAnyAsync<IOException>(() => Generate(share, template, tooLong));
        output.WriteLine($"256-character app name: {failure.GetType().Name}: {failure.Message}");
        // Only the 200-character app's folder exists; the failed build left nothing at all.
        Assert.Equal([Path.GetFileName(Path.GetDirectoryName(ok.PackagePath)!)],
            Directory.EnumerateDirectories(share).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.DoesNotContain(Directory.EnumerateDirectories(share, "*", SearchOption.AllDirectories),
            d => Path.GetFileName(d)!.Contains('b'));
    }

    [Fact]
    public async Task SeveralSourceFilesAllLandInFilesAndOneIsTakenAsTheInstaller()
    {
        if (Absent(MsiPutty, Msi7z2409)) return;   // skips without the real installers

        var share = Share("multi-source");
        var template = TrimmedTemplate("multi-source");
        var result = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Multi", Version = "1.0",
            SourcesPath = MsiPutty + ";" + Msi7z2409,
            MsiProductCode = MsiInfoService.ExtractMsiInfo(MsiPutty).ProductCode,
        });

        Assert.Equal(["7z2409-x64.msi", "putty-64bit-0.83-installer.msi"],
            Directory.EnumerateFiles(PsadtLayout.FilesFolder(result.PackagePath)).Select(p => Path.GetFileName(p)!).Order().ToArray());

        // Documents today's pick: the first installer in Files order, which is not
        // necessarily the first file of the list the caller passed. See the report.
        var script = ScriptOf(result.PackagePath);
        Assert.Equal("7z2409-x64.msi", script.InstallerFileName);
        Assert.Equal(MsiInfoService.ExtractMsiInfo(MsiPutty).ProductCode, script.MsiProductCode);
        AssertParses(script);
    }

    // ── Upgrade ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpgradeMsiToMsiSwapsTheInstallerAndProductCodeAndKeepsEverythingElse()
    {
        if (Absent(Msi7z2408, Msi7z2409)) return;   // skips without the real installers

        var oldMsi = MsiInfoService.ExtractMsiInfo(Msi7z2408);
        var newMsi = MsiInfoService.ExtractMsiInfo(Msi7z2409);
        Assert.NotEqual(oldMsi.ProductCode, newMsi.ProductCode);

        var share = Share("upgrade-msi");
        var template = TrimmedTemplate("upgrade-msi", Bundled41);
        var layout = Layout("Docs");
        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = oldMsi.Manufacturer, Name = "7-Zip", Version = oldMsi.ProductVersion, Architecture = "x64",
            SourcesPath = Msi7z2408, MsiProductCode = oldMsi.ProductCode, InstallArguments = "ALLUSERS=1",
        }, layout);

        // What a packager leaves in a built package: a hand edit, an icon, notes, a transform,
        // and the Intune marker of the version that was published.
        var handEdit = "Write-ADTLogEntry -Message 'close Explorer first'";
        var scriptPath = PsadtLayout.ScriptPath(created.PackagePath);
        File.WriteAllText(scriptPath, File.ReadAllText(scriptPath)
            .Replace("## <Perform Pre-Installation tasks here>", "## <Perform Pre-Installation tasks here>\r\n        " + handEdit));
        File.WriteAllText(Path.Combine(created.PackagePath, PsadtLayout.IconFolderName, "7zip_icon.png"), "png");
        File.WriteAllText(Path.Combine(created.PackagePath, "Docs", "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(created.PackagePath, PsadtLayout.IntuneFolderName, "packman.id"), "{}");
        File.WriteAllText(Path.Combine(PsadtLayout.FilesFolder(created.PackagePath), "site.mst"), "transform");

        var upgraded = await new PackageUpgradeService(share, layout)
            .UpgradePackageAsync(created.PackagePath, newMsi.ProductVersion, Msi7z2409);

        Assert.Empty(upgraded.Warnings);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(created.PackagePath)!, newMsi.ProductVersion), upgraded.PackagePath);
        Assert.True(Directory.Exists(created.PackagePath));   // the old version stays

        // Files: new installer in, old installer out, the transform carried forward.
        Assert.Equal(["7z2409-x64.msi", "site.mst"],
            Directory.EnumerateFiles(PsadtLayout.FilesFolder(upgraded.PackagePath)).Select(p => Path.GetFileName(p)!).Order().ToArray());
        // Icon and the extra folder come across; Intune starts empty for the new version.
        Assert.Equal("7zip_icon.png", Path.GetFileName(PsadtLayout.FindIcon(upgraded.PackagePath)));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(upgraded.PackagePath, "Docs", "notes.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(PsadtLayout.IntuneFolder(upgraded.PackagePath)));

        var script = ScriptOf(upgraded.PackagePath);
        Assert.Equal(newMsi.ProductVersion, script.AppVersion);
        Assert.Equal("7-Zip", script.AppName);
        Assert.Equal(oldMsi.Manufacturer, script.Vendor);
        Assert.Equal("x64", script.AppArch);
        Assert.Equal(PsadtScript.TodayStamp, script.ScriptDate);
        Assert.Equal("7z2409-x64.msi", script.InstallerFileName);
        Assert.Equal(["7z2409-x64.msi"], script.SourceFileNames);
        Assert.Equal(newMsi.ProductCode, script.MsiProductCode);
        Assert.DoesNotContain(oldMsi.ProductCode, script.Content);
        // The packager's edit and the install arguments survive: no template is involved.
        Assert.Contains(handEdit, script.Content);
        var ast = AssertParses(script);
        var install = Assert.Single(Commands(ast, "Start-ADTMsiProcess"), c => c.Text.Contains("-Action 'Install'"));
        Assert.Equal("Install", install.Phase);
        Assert.Contains("ALLUSERS=1", Literals(install.Command));
        Assert.Empty(PackagePreflight.Check(upgraded.PackagePath));

        // The old version still installs the old product.
        Assert.Equal(oldMsi.ProductCode, ScriptOf(created.PackagePath).MsiProductCode);
    }

    [Fact]
    public async Task UpgradeExeToExeRepointsTheScriptAndKeepsTheSwitches()
    {
        if (Absent(ExeNotepadPlusPlus, Exe7z)) return;   // skips without the real installers

        var share = Share("upgrade-exe");
        var template = TrimmedTemplate("upgrade-exe", Bundled41);
        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Don HO", Name = "Notepad++", Version = "8.7.1",
            SourcesPath = ExeNotepadPlusPlus, InstallArguments = "/S", UninstallArguments = "/S",
        });

        var upgraded = await new PackageUpgradeService(share, Layout()).UpgradePackageAsync(created.PackagePath, "24.09", Exe7z);

        Assert.Empty(upgraded.Warnings);
        Assert.Equal(["7z2409.exe"], Directory.EnumerateFiles(PsadtLayout.FilesFolder(upgraded.PackagePath)).Select(p => Path.GetFileName(p)!).ToArray());
        var script = ScriptOf(upgraded.PackagePath);
        Assert.Equal("24.09", script.AppVersion);
        Assert.Equal(["7z2409.exe"], script.SourceFileNames);
        Assert.DoesNotContain(Path.GetFileName(ExeNotepadPlusPlus), script.Content);
        var ast = AssertParses(script);
        var calls = Commands(ast, "Start-ADTProcess");
        Assert.Equal(2, calls.Count);
        Assert.Equal(["Install", "Uninstall"], calls.Select(c => c.Phase).Order().ToArray());
        Assert.All(calls, c => Assert.Contains("/S", Literals(c.Command)));
        Assert.Empty(PackagePreflight.Check(upgraded.PackagePath));
    }

    [Fact]
    public async Task UpgradeFromMsiToExeWarnsAndReplacesTheProductCodeWithThePlaceholder()
    {
        if (Absent(Msi7z2408, Exe7z)) return;   // skips without the real installers

        var msi = MsiInfoService.ExtractMsiInfo(Msi7z2408);
        var share = Share("upgrade-kind");
        var template = TrimmedTemplate("upgrade-kind", Bundled41);
        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = msi.Manufacturer, Name = "7-Zip", Version = msi.ProductVersion,
            SourcesPath = Msi7z2408, MsiProductCode = msi.ProductCode,
        });

        var upgraded = await new PackageUpgradeService(share, Layout()).UpgradePackageAsync(created.PackagePath, "24.09", Exe7z);

        var warning = Assert.Single(upgraded.Warnings);
        Assert.Contains("from MSI to EXE", warning);
        var script = ScriptOf(upgraded.PackagePath);
        Assert.DoesNotContain(msi.ProductCode, script.Content);
        Assert.Contains(PsadtScript.ProductCodePlaceholder, script.Content);
        Assert.Equal(["7z2409.exe"], script.SourceFileNames);
        AssertParses(script);
        // The placeholder blocks publishing until the packager rewrites the commands.
        Assert.Contains(PackagePreflight.Check(upgraded.PackagePath), issue => issue.Contains("product code"));
    }

    [Fact]
    public async Task UpgradeRefusesAnExistingVersionAMissingSourceAndAFolderThatIsNotAPackage()
    {
        var share = Share("upgrade-refusals");
        var template = TrimmedTemplate("upgrade-refusals");
        var installer = Path.Combine(_root, "setup.exe");
        File.WriteAllText(installer, "x");
        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "App", Version = "1.0", SourcesPath = installer, InstallArguments = "/S", UninstallArguments = "/S",
        });
        var service = new PackageUpgradeService(share, Layout());

        var existing = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpgradePackageAsync(created.PackagePath, "1.0", installer));
        Assert.Contains("already exists", existing.Message);

        var missing = await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.UpgradePackageAsync(created.PackagePath, "2.0", Path.Combine(_root, "nope.exe")));
        Assert.Contains("Source file not found", missing.Message);

        var notAPackage = await Assert.ThrowsAsync<FileNotFoundException>(() => service.UpgradePackageAsync(share, "2.0", installer));
        Assert.Contains("PSADT v4 packages only", notAPackage.Message);

        // Nothing was created by any of the three failures.
        Assert.Equal(["1.0"], Directory.EnumerateDirectories(Path.GetDirectoryName(created.PackagePath)!).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public async Task UpgradingAScriptThatNamesNoInstallerWarnsAndLeavesThePreviousFile()
    {
        var share = Share("upgrade-handwritten");
        var package = Path.Combine(share, "Hand_Written", "1.0");
        Directory.CreateDirectory(PsadtLayout.FilesFolder(package));
        File.WriteAllText(PsadtLayout.ScriptPath(package),
            "$adtSession = @{\r\n    AppVendor = 'Hand'\r\n    AppName = 'Written'\r\n    AppVersion = '1.0'\r\n}\r\n"
            + "## MARK: Install\r\nNew-Variable -Name Install -Force -Value {\r\n    Write-ADTLogEntry 'installs nothing'\r\n}\r\n");
        File.WriteAllText(Path.Combine(PsadtLayout.FilesFolder(package), "old-setup.exe"), "old");
        var newInstaller = Path.Combine(_root, "new-setup.exe");
        File.WriteAllText(newInstaller, "new");

        var upgraded = await new PackageUpgradeService(share, Layout()).UpgradePackageAsync(package, "2.0", newInstaller);

        var warning = Assert.Single(upgraded.Warnings);
        Assert.Contains("names no installer", warning);
        Assert.Equal(["new-setup.exe", "old-setup.exe"],
            Directory.EnumerateFiles(PsadtLayout.FilesFolder(upgraded.PackagePath)).Select(p => Path.GetFileName(p)!).Order().ToArray());
        Assert.Equal("2.0", ScriptOf(upgraded.PackagePath).AppVersion);
    }

    // ── MsiInfoService over the real MSIs ─────────────────────────────────────

    [Fact]
    public async Task MsiInfoReadsTheRealInstallerTables()
    {
        if (Absent(MsiPutty)) return;   // skips without the real installers

        var info = MsiInfoService.ExtractMsiInfo(MsiPutty);
        Assert.True(info.IsValid);
        Assert.True(PsadtScript.IsProductCode(info.ProductCode), $"product code {info.ProductCode}");
        Assert.True(PsadtScript.IsProductCode(info.UpgradeCode), $"upgrade code {info.UpgradeCode}");
        Assert.NotEqual(info.ProductCode, info.UpgradeCode);
        Assert.Equal("PuTTY release 0.83 (64-bit)", info.ProductName);
        Assert.Equal("Simon Tatham", info.Manufacturer);
        Assert.StartsWith("0.83", info.ProductVersion);
        Assert.NotEmpty(info.Properties);
        Assert.Equal(info.ProductCode, info.Properties.Single(p => p.Name == "ProductCode").Value);
        Assert.Equal("Per machine", info.InstallScope);

        // Platform and language come from the summary stream.
        Assert.Equal("x64;1033", info.Summary[7]);
        Assert.Equal("x64", MsiInfoService.PlatformOf(info.Summary[7]));
        Assert.Contains("English", MsiInfoService.LanguagesOf(info.Summary[7]));

        // Warnings: silent when the package matches, explicit when it does not.
        Assert.Empty(info.Warnings(userInstall: false, "x64"));
        Assert.Equal(
            [
                "The MSI is built for x64, but the package is set to x86.",
                "The MSI installs per machine (ALLUSERS=1), which needs elevation, but the package installs as User.",
            ],
            info.Warnings(userInstall: true, "x86"));

        // Features and dialog options are read without throwing and are usable as arguments.
        Assert.NotEmpty(info.Features);
        Assert.All(info.Features, f => Assert.NotEmpty(f.Name));
        Assert.Contains(info.Features, f => f.Detail.Contains("Installed by default"));
        Assert.All(info.Options, o => Assert.True(MsiInfoService.IsPublicName(o.Name)));
        Assert.All(info.Options, o => Assert.StartsWith(o.Name + "=", o.Argument));
        Assert.Equal(info.Properties.Count, info.Search(null).Count);
        Assert.Contains(info.Search("product"), p => p.Name == "ProductCode");
        Assert.Empty(info.Search("no-such-property-anywhere"));
        Assert.Contains(info.KeyProperties, p => p.Name == "ProductName");
        Assert.Empty(info.Transforms);   // no .mst beside the installers

        // The STA path the UI uses returns the same values.
        var async = await MsiInfoService.ExtractMsiInfoAsync(MsiPutty);
        Assert.Equal(info.ProductCode, async.ProductCode);
        Assert.Equal(info.Properties.Count, async.Properties.Count);

        output.WriteLine($"{Path.GetFileName(MsiPutty)}: {info.ProductName} {info.ProductVersion} by {info.Manufacturer}, "
            + $"product {info.ProductCode}, upgrade {info.UpgradeCode}, {info.Properties.Count} properties, "
            + $"{info.Features.Count} features, {info.Options.Count} options, scope '{info.InstallScope}'");
    }

    [Fact]
    public void TwoVersionsOfOneMsiShareAnUpgradeCodeAndDifferInProductCode()
    {
        if (Absent(Msi7z2408, Msi7z2409)) return;   // skips without the real installers

        var older = MsiInfoService.ExtractMsiInfo(Msi7z2408);
        var newer = MsiInfoService.ExtractMsiInfo(Msi7z2409);
        Assert.NotEqual(older.ProductCode, newer.ProductCode);
        Assert.Equal(older.UpgradeCode, newer.UpgradeCode);
        Assert.Equal("Igor Pavlov", newer.Manufacturer);
        Assert.StartsWith("24.09", newer.ProductVersion);
        Assert.True(string.Compare(older.ProductVersion, newer.ProductVersion, StringComparison.Ordinal) < 0);
    }

    [Fact]
    public void ReadingSomethingThatIsNotAnMsiReturnsNothingInsteadOfThrowing()
    {
        if (Absent(Exe7z)) return;   // skips without the real installers

        var fromExe = MsiInfoService.ExtractMsiInfo(Exe7z);
        Assert.False(fromExe.IsValid);
        Assert.Empty(fromExe.Properties);
        Assert.Empty(fromExe.Features);
        Assert.Empty(fromExe.Options);
        Assert.Equal("", fromExe.InstallScope);
        Assert.Empty(fromExe.Warnings(userInstall: false, "x64"));

        var missing = MsiInfoService.ExtractMsiInfo(Path.Combine(_root, "does-not-exist.msi"));
        Assert.False(missing.IsValid);
    }

    // ── InstallerDetector over the real EXEs ──────────────────────────────────

    [Theory]
    [InlineData("npp.8.7.1.Installer.x64.exe", "NSIS (Nullsoft)", Confidence.High, "x86", true)]
    [InlineData("7z2409.exe", "7-Zip SFX", Confidence.High, "x86", false)]
    public void TheDetectorNamesEveryRealExeInstaller(string fileName, string kind, Confidence confidence, string architecture, bool signed)
    {
        var path = Path.Combine(DesktopInstallers, fileName);
        if (Absent(path)) return;   // skips without the real installers

        var detection = InstallerDetector.Detect(path);
        output.WriteLine($"{fileName}: {detection.Kind.Name}, {detection.Confidence} confidence, evidence '{detection.Evidence}', "
            + $"setup program {detection.Architecture}, signed {detection.HasSignature}, "
            + $"install switches [{string.Join(" ", detection.Kind.Switches.Where(s => !s.Uninstall).Select(s => s.Text))}], "
            + $"uninstall switches [{string.Join(" ", detection.Kind.Switches.Where(s => s.Uninstall).Select(s => s.Text))}]");

        Assert.Equal(kind, detection.Kind.Name);
        Assert.Equal(confidence, detection.Confidence);
        Assert.Equal(architecture, detection.Architecture);
        Assert.Equal(signed, detection.HasSignature);
        Assert.NotEmpty(detection.Evidence);
        Assert.NotSame(InstallerCatalog.Unknown, detection.Kind);
    }

    [Fact]
    public void EveryRealExeIsDetectedAndEveryDetectionIsUsable()
    {
        if (Absent(DesktopInstallers)) return;   // skips without the test installer folder

        foreach (var exe in Directory.EnumerateFiles(DesktopInstallers, "*.exe"))
        {
            var detection = InstallerDetector.Detect(exe);
            output.WriteLine($"{Path.GetFileName(exe)} -> {detection.Kind.Name} ({detection.Confidence}) via {detection.Evidence}");
            Assert.NotSame(InstallerCatalog.Unknown, detection.Kind);
            Assert.NotNull(detection.Confidence);
            // A switch the rail offers must be a switch, never free text with a space in it.
            Assert.All(detection.Kind.Switches.Where(s => !s.Text.StartsWith("/log", StringComparison.OrdinalIgnoreCase)),
                s => Assert.DoesNotContain(' ', s.Text));
        }
    }

    [Fact]
    public void TheDetectorRefusesFilesThatAreNotWindowsExecutables()
    {
        if (Absent(MsiPutty)) return;   // skips without the real installers

        // An MSI is not a PE file; the viewmodel only runs the detector on .exe sources.
        var fromMsi = Assert.Throws<InvalidDataException>(() => InstallerDetector.Detect(MsiPutty));
        Assert.Contains("not a Windows executable", fromMsi.Message);

        var text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "just text, long enough to have a few bytes in it");
        Assert.Throws<InvalidDataException>(() => InstallerDetector.Detect(text));

        var empty = Path.Combine(_root, "empty.exe");
        File.WriteAllText(empty, "");
        Assert.Throws<InvalidDataException>(() => InstallerDetector.Detect(empty));
    }

    // ── The viewmodels: auto-fill, validation, warnings ───────────────────────

    [Fact]
    public async Task TheCreateFormFillsItselfFromARealInstallerAndKeepsWhatYouTyped()
    {
        if (Absent(MsiPutty, ExeNotepadPlusPlus)) return;   // skips without the real installers

        var vm = new CreatePackageViewModel();
        Assert.False(vm.Validate());
        Assert.Contains("required", vm.StatusText);

        vm.LoadFromFile(MsiPutty);
        await vm.InstallerRead;
        Assert.Equal("PuTTY release 0.83 (64-bit)", vm.AppName);
        Assert.Equal("Simon Tatham", vm.Manufacturer);
        Assert.StartsWith("0.83", vm.Version);
        Assert.Equal("MSI", vm.InstallerType);
        Assert.True(vm.HasProductCode);
        Assert.True(vm.HasMsiInsight);
        Assert.Equal("", vm.InstallerError);
        Assert.True(vm.Validate());

        // MSI warnings follow the architecture and context the packager picks.
        Assert.Empty(vm.MsiWarnings);
        vm.Architecture = "x86";
        vm.UserInstall = true;
        Assert.Equal(2, vm.MsiWarnings.Count);
        Assert.True(vm.HasMsiWarnings);
        vm.Architecture = "x64";
        vm.UserInstall = false;

        // Picking another installer replaces what the first one filled in…
        vm.LoadFromFile(ExeNotepadPlusPlus);
        await vm.InstallerRead;
        Assert.Equal("Notepad++", vm.AppName);
        Assert.Equal("EXE", vm.InstallerType);
        Assert.Equal("NSIS (Nullsoft)", vm.InstallerKindName);
        Assert.Equal("High", vm.InstallerConfidence);
        Assert.True(vm.HasDetectedInstaller);
        Assert.False(vm.HasProductCode);
        Assert.Contains(vm.InstallSwitches, s => s.Text == "/S");
        Assert.Contains(vm.ExeFacts, f => f.Label == "Signature");

        // …but never what the packager typed.
        vm.AppName = "Notepad++ (standard build)";
        vm.LoadFromFile(MsiPutty);
        await vm.InstallerRead;
        Assert.Equal("Notepad++ (standard build)", vm.AppName);
        Assert.Equal("Simon Tatham", vm.Manufacturer);

        var info = vm.BuildApplicationInfo();
        Assert.Equal("Notepad++ (standard build)", info.Name);
        Assert.Equal("MSI", info.PackageType);
        Assert.True(PsadtScript.IsProductCode(info.MsiProductCode));

        // Blank fields fall back rather than reaching the generator empty, and a path
        // pasted with Explorer's "Copy as path" quotes is unwrapped.
        var blank = new CreatePackageViewModel { AppName = "App", SourcesPath = $"\"{MsiPutty}\"" };
        var blankInfo = blank.BuildApplicationInfo();
        Assert.Equal("Unknown", blankInfo.Manufacturer);
        Assert.Equal("1.0.0", blankInfo.Version);
        Assert.Equal(MsiPutty, blankInfo.SourcesPath);
    }

    [Fact]
    public async Task TheCreateFormGeneratesThroughTheSettingsAndCopiesTheIcon()
    {
        if (Absent(ExeNotepadPlusPlus)) return;   // skips without the real installers

        var share = Share("vm-generate");
        var settings = new AppSettings
        {
            NetworkPaths = { IntuneApplications = share, PSADTTemplate = TrimmedTemplate("vm-generate") },
            PackageLayout = Layout(),
        };

        var vm = new CreatePackageViewModel();
        vm.LoadFromFile(ExeNotepadPlusPlus);
        await vm.InstallerRead;
        vm.AddInstallSwitchCommand.Execute("/S");
        vm.AddUninstallSwitchCommand.Execute("/S");
        Assert.Equal("/S", vm.InstallArguments);

        var extractedIcon = vm.ExtractedIconPath;
        try
        {
            var packagePath = await vm.GenerateAsync(settings);

            Assert.NotNull(packagePath);
            Assert.Equal(packagePath, vm.CurrentPackagePath);
            Assert.Empty(vm.ScriptWarnings);
            Assert.False(vm.HasScriptWarnings);
            Assert.False(vm.WasCancelled);
            Assert.Contains("Package created", vm.StatusText);
            Assert.Empty(PackagePreflight.Check(packagePath));
            // The icon pulled out of the EXE is the one the publish step uploads.
            if (extractedIcon.Length > 0)
                Assert.NotNull(PsadtLayout.FindIcon(packagePath));

            // A second build of the same version is refused, and the first one survives.
            Assert.Null(await vm.GenerateAsync(settings));
            Assert.Contains("already exists", vm.StatusText);
            Assert.True(Directory.Exists(packagePath));

            // Without an output share nothing is attempted.
            var unset = new CreatePackageViewModel { AppName = "App" };
            Assert.Null(await unset.GenerateAsync(new AppSettings()));
            Assert.Contains("Intune Applications Path", unset.StatusText);
        }
        finally
        {
            // ExtractIconToTemp writes into %TEMP%\Packman; leave nothing of ours there.
            if (extractedIcon.Length > 0 && File.Exists(extractedIcon)) File.Delete(extractedIcon);
        }
    }

    [Fact]
    public async Task TheUpgradeFormReadsThePackageAndTheNewInstaller()
    {
        if (Absent(Msi7z2408, Msi7z2409, Exe7z)) return;   // skips without the real installers

        var share = Share("vm-upgrade");
        var settings = new AppSettings
        {
            NetworkPaths = { IntuneApplications = share, PSADTTemplate = TrimmedTemplate("vm-upgrade") },
            PackageLayout = Layout(),
        };
        var older = MsiInfoService.ExtractMsiInfo(Msi7z2408);
        var created = await Generate(share, settings.NetworkPaths.PSADTTemplateInUse, new ApplicationInfo
        {
            Manufacturer = older.Manufacturer, Name = "7-Zip", Version = older.ProductVersion, InstallContext = "User",
            SourcesPath = Msi7z2408, MsiProductCode = older.ProductCode,
        });

        var vm = new UpgradePackageViewModel();
        vm.LoadPackage(created.PackagePath);
        Assert.True(vm.HasLoadedPackage);
        Assert.Equal("Igor Pavlov 7-Zip", vm.PackageDisplayName);
        Assert.Equal($"Version {older.ProductVersion}", vm.PackageDisplayVersion);
        Assert.Equal("User Install", vm.PackageDisplayContext);
        Assert.False(vm.CanUpgrade);

        // The new version is read off the MSI itself.
        vm.SetNewSource(Msi7z2409);
        Assert.Equal("24.09.00.0", vm.NewVersion);
        Assert.True(vm.CanUpgrade);

        var upgraded = await vm.UpgradeAsync(settings);
        Assert.NotNull(upgraded);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(created.PackagePath)!, "24.09.00.0"), upgraded);
        Assert.Contains("created", vm.StatusText);
        Assert.Equal("User", InstallContextParser.ExtractFromPackage(upgraded));

        // An EXE's version comes from its metadata or its file name.
        vm.SetNewSource(Exe7z);
        Assert.StartsWith("24.09", vm.NewVersion);

        // A folder that is not a package says so instead of throwing.
        var empty = new UpgradePackageViewModel();
        empty.LoadPackage(share);
        Assert.False(empty.HasLoadedPackage);
        Assert.Contains("not a valid package folder", empty.StatusText);

        vm.Reset();
        Assert.False(vm.HasLoadedPackage);
        Assert.Equal("", vm.NewVersion);
    }

    // ── Installer names PowerShell treats specially ───────────────────────────

    [Fact]
    public async Task UpgradeRepointsTheScriptWhenTheInstallerNameHasAnApostrophe()
    {
        if (Absent(Exe7z, ExeNotepadPlusPlus)) return;

        var share = Share("name-apostrophe");
        var template = TrimmedTemplate("name-apostrophe");
        var installer = Path.Combine(_root, "O'Reilly setup.exe");
        File.Copy(Exe7z, installer, overwrite: true);

        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Apostrophe", Version = "1.0", SourcesPath = installer,
            InstallArguments = "/S", UninstallArguments = "/S",
        });
        // An apostrophe is legal inside the double-quoted path the script uses, and the name
        // is read back exactly as it is on disk.
        Assert.Contains("O'Reilly setup.exe", ScriptOf(created.PackagePath).Content);
        Assert.Equal("O'Reilly setup.exe", ScriptOf(created.PackagePath).InstallerFileName);
        Assert.Equal(["O'Reilly setup.exe"], ScriptOf(created.PackagePath).SourceFileNames);
        Assert.Empty(PackagePreflight.Check(created.PackagePath));

        var upgraded = await new PackageUpgradeService(share, Layout())
            .UpgradePackageAsync(created.PackagePath, "2.0", ExeNotepadPlusPlus);

        Assert.Empty(upgraded.Warnings);
        Assert.Equal([Path.GetFileName(ExeNotepadPlusPlus)], ScriptOf(upgraded.PackagePath).SourceFileNames);
        Assert.DoesNotContain("O'Reilly setup.exe", ScriptOf(upgraded.PackagePath).Content);
        // The previous installer goes, so only the one the script names ships.
        Assert.Equal([Path.GetFileName(ExeNotepadPlusPlus)],
            Directory.EnumerateFiles(PsadtLayout.FilesFolder(upgraded.PackagePath)).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.Empty(PackagePreflight.Check(upgraded.PackagePath));
    }

    [Fact]
    public async Task UpgradeRemovesThePreviousInstallerWhenItsNameHasADollarSign()
    {
        if (Absent(Exe7z, ExeNotepadPlusPlus)) return;

        var share = Share("name-dollar");
        var template = TrimmedTemplate("name-dollar");
        var installer = Path.Combine(_root, "cost $5 setup.exe");
        File.Copy(Exe7z, installer, overwrite: true);

        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Dollar", Version = "1.0", SourcesPath = installer,
            InstallArguments = "/S", UninstallArguments = "/S",
        });

        // The script escapes the $ for PowerShell; what the readers report is the file on disk.
        var createdScript = ScriptOf(created.PackagePath);
        Assert.Contains("cost `$5 setup.exe", createdScript.Content);
        Assert.Equal("cost $5 setup.exe", createdScript.InstallerFileName);
        Assert.Equal(["cost $5 setup.exe"], createdScript.SourceFileNames);
        Assert.Empty(PackagePreflight.Check(created.PackagePath));

        var upgraded = await new PackageUpgradeService(share, Layout())
            .UpgradePackageAsync(created.PackagePath, "2.0", ExeNotepadPlusPlus);

        Assert.Empty(upgraded.Warnings);
        Assert.Equal([Path.GetFileName(ExeNotepadPlusPlus)],
            Directory.EnumerateFiles(PsadtLayout.FilesFolder(upgraded.PackagePath)).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.Equal([Path.GetFileName(ExeNotepadPlusPlus)], ScriptOf(upgraded.PackagePath).SourceFileNames);
        Assert.Empty(PackagePreflight.Check(upgraded.PackagePath));
    }

    // ── A script that cannot install what it names ────────────────────────────

    [Fact]
    public async Task GenerateWarnsWhenNoInstallerLandedInFiles()
    {
        var share = Share("no-installer");
        var template = TrimmedTemplate("no-installer");
        var sources = Path.Combine(_root, "sources-without-an-installer");
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "readme.txt"), "no installer in here");

        var result = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "NoInstaller", Version = "1.0", SourcesPath = sources,
            InstallArguments = "/S", UninstallArguments = "/S",
        });

        // The install command has to name something; the packager is told it is not there.
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("No .msi or .exe installer was found", warning);
        Assert.Contains("setup.exe", warning);

        var script = ScriptOf(result.PackagePath);
        var referenced = Assert.Single(script.SourceFileNames);
        Assert.Equal("setup.exe", referenced);
        Assert.False(File.Exists(Path.Combine(PsadtLayout.FilesFolder(result.PackagePath), referenced)));

        // And preflight blocks the publish until the installer is added or the script fixed.
        var issue = Assert.Single(PackagePreflight.Check(result.PackagePath));
        Assert.Contains($@"{PsadtLayout.FilesFolderName}\setup.exe", issue);
        Assert.Contains("not in the package", issue);
        AssertParses(script);
    }

    [Fact]
    public async Task PreflightFlagsAnMsiInstalledThroughStartAdtProcess()
    {
        // An EXE package upgraded to an MSI: the install command is still Start-ADTProcess,
        // which now points at an .msi and would fail on every device.
        if (Absent(ExeNotepadPlusPlus, MsiPutty)) return;

        var share = Share("kind-change-to-msi");
        var template = TrimmedTemplate("kind-change-to-msi");
        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Don HO", Name = "Notepad++", Version = "8.7.1", SourcesPath = ExeNotepadPlusPlus,
            InstallArguments = "/S", UninstallArguments = "/S",
        });

        var upgraded = await new PackageUpgradeService(share, Layout()).UpgradePackageAsync(created.PackagePath, "0.83", MsiPutty);
        var kindWarning = Assert.Single(upgraded.Warnings);
        Assert.Contains("from EXE to MSI", kindWarning);

        var script = ScriptOf(upgraded.PackagePath);
        Assert.Contains("Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\putty-64bit-0.83-installer.msi\"", script.Content);
        Assert.Contains(PackagePreflight.Check(upgraded.PackagePath),
            issue => issue.Contains("runs an .msi with Start-ADTProcess"));
    }

    [Fact]
    public async Task PreflightFlagsAnExeInstalledThroughStartAdtMsiProcess()
    {
        // The other direction: an MSI package upgraded to an EXE keeps Start-ADTMsiProcess.
        if (Absent(MsiPutty, ExeNotepadPlusPlus)) return;

        var share = Share("kind-change-to-exe");
        var template = TrimmedTemplate("kind-change-to-exe");
        var msi = MsiInfoService.ExtractMsiInfo(MsiPutty);
        var created = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Simon Tatham", Name = "PuTTY", Version = "0.83", SourcesPath = MsiPutty,
            MsiProductCode = msi.ProductCode, InstallArguments = "ALLUSERS=1",
        });

        var upgraded = await new PackageUpgradeService(share, Layout()).UpgradePackageAsync(created.PackagePath, "8.7.1", ExeNotepadPlusPlus);
        var kindWarning = Assert.Single(upgraded.Warnings);
        Assert.Contains("from MSI to EXE", kindWarning);

        var script = ScriptOf(upgraded.PackagePath);
        Assert.Contains("Start-ADTMsiProcess -Action 'Install' -FilePath \"$($adtSession.DirFiles)\\npp.8.7.1.Installer.x64.exe\"", script.Content);
        Assert.Contains(PackagePreflight.Check(upgraded.PackagePath),
            issue => issue.Contains("runs an .exe with Start-ADTMsiProcess"));
    }

    // ── Session values the script must keep on one line ───────────────────────

    [Fact]
    public async Task ASessionValueCarryingLineBreaksIsFlattenedToOneLine()
    {
        // An MSI or version-resource field can carry a line break. Written as it came, the
        // next entry would match inside this one's string and turn data into script.
        var share = Share("multiline-name");
        var template = TrimmedTemplate("multiline-name");
        var installer = Path.Combine(_root, "multiline.msi");
        File.WriteAllText(installer, "x");

        var result = await Generate(share, template, new ApplicationInfo
        {
            Manufacturer = "Contoso\r\nEvil = 'injected'", Name = "Line\nBreak\tApp", Version = "1.0",
            SourcesPath = installer, MsiProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}",
        });

        var script = ScriptOf(result.PackagePath);
        Assert.Equal("Contoso  Evil = 'injected'", script.Vendor);
        Assert.Equal("Line Break App", script.AppName);
        // Nothing was added to the hashtable, and the script is still valid PowerShell.
        Assert.Null(script.GetSessionValue("Evil"));
        AssertParses(script);
    }
}
