using System.IO;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// Settings ▸ Package layout ▸ Package folder name: the format, where Generate and Upgrade put
/// packages for it, and finding linked packages under both one- and two-level app folders.
/// </summary>
public sealed class PackageFolderNameTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string AppId = "22222222-2222-2222-2222-222222222222";

    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-foldername-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(null, "Contoso_Acme_Reader")]
    [InlineData("", "Contoso_Acme_Reader")]
    [InlineData("%vendor%_%appName%", "Contoso_Acme_Reader")]
    [InlineData("%appName%", "Acme_Reader")]
    [InlineData(@"%vendor%\%appName%", @"Contoso\Acme_Reader")]
    [InlineData("%vendor%/%appName%", @"Contoso\Acme_Reader")]
    [InlineData("%APPNAME% (%Vendor%)", "Acme_Reader (Contoso)")]
    [InlineData(@"Apps\%vendor% - %appName%", @"Apps\Contoso - Acme_Reader")]
    public void FormatsExpand(string? template, string expected)
    {
        Assert.Null(PackagePaths.FolderTemplateProblem(template));
        Assert.Equal(expected, PackagePaths.AppFolder(template, "Contoso", "Acme Reader"));
    }

    [Fact]
    public void DefaultFormatIsTheNameEveryExistingPackageHas()
    {
        Assert.Equal(PackagePaths.AppFolderName("Simon Tatham", "PuTTY release 0.83 (64-bit)"),
            PackagePaths.AppFolder(null, "Simon Tatham", "PuTTY release 0.83 (64-bit)"));
        Assert.Equal("Simon_Tatham_PuTTY_release_0.83_(64-bit)", PackagePaths.AppFolderName("Simon Tatham", "PuTTY release 0.83 (64-bit)"));
        Assert.Equal("_App", PackagePaths.AppFolderName("", "App"));
        Assert.Equal(PackagePaths.VersionFolder(_root, PackagePaths.AppFolderName("Contoso", "App"), "1.0"),
            PackagePaths.PackageFolder(_root, null, "Contoso", "App", "1.0"));
    }

    [Theory]
    [InlineData("%vendor%")]                       // every app of a vendor in one folder
    [InlineData("%appName%_%appVersion%")]         // the version is always its own level
    [InlineData(@"%vendor%\Apps\%appName%")]       // three levels
    [InlineData(@"%vendor%\\%appName%")]           // empty level
    [InlineData("%appName%_%arch%")]               // unknown token
    [InlineData("%appName%:x")]
    [InlineData(@"..\%appName%")]
    public void UnusableFormatsAreRefused(string template)
    {
        Assert.NotNull(PackagePaths.FolderTemplateProblem(template));
        Assert.Throws<ArgumentException>(() => PackagePaths.AppFolder(template, "Contoso", "App"));
    }

    [Fact]
    public void NamesNeverAddLevelsOrEscapeTheRoot()
    {
        // A '\' or '..' in a vendor or app name is sanitised, not followed.
        Assert.Equal(@"Evil_Corp\.._.._Windows", PackagePaths.AppFolder(@"%vendor%\%appName%", @"Evil\Corp", @"..\..\Windows"));
        // A level that is only dots is refused outright.
        Assert.Throws<ArgumentException>(() => PackagePaths.PackageFolder(_root, @"%vendor%\%appName%", "..", "App", "1.0"));
        Assert.StartsWith(_root, PackagePaths.PackageFolder(_root, @"%vendor%\%appName%", "a..", "App", "1.0"));

        // No vendor: the vendor level is dropped rather than failing.
        Assert.Equal("App", PackagePaths.AppFolder(@"%vendor%\%appName%", "", "App"));
        Assert.Throws<ArgumentException>(() => PackagePaths.AppFolder("%appName%", "Contoso", "  "));
    }

    [Fact]
    public async Task GenerateFollowsTheFormatAndUpgradeStaysBesideTheExistingVersion()
    {
        var template = Path.Combine(_root, ".template");
        Directory.CreateDirectory(template);
        File.WriteAllText(Path.Combine(template, PsadtLayout.ScriptName),
            "$adtSession = @{\n    AppVendor = ''\n    AppName = ''\n    AppVersion = ''\n}\n## MARK: Install\n## MARK: Uninstall\n");
        var installer = Path.Combine(_root, "setup.exe");
        File.WriteAllText(installer, "v1");
        var share = Path.Combine(_root, "share");
        Directory.CreateDirectory(share);

        var nested = new AppSettings.PackageLayoutConfig { FolderNameTemplate = @"%vendor%\%appName%" };
        var created = await new PSADTGenerator(share, template, nested).CreatePackageAsync(new ApplicationInfo
        {
            Manufacturer = "Contoso", Name = "Acme Reader", Version = "1.0", SourcesPath = installer,
        });
        Assert.Equal(Path.Combine(share, "Contoso", "Acme_Reader", "1.0"), created.PackagePath);

        // The format changed since: the next version still lands beside the first.
        var flat = new AppSettings.PackageLayoutConfig { FolderNameTemplate = "%appName%" };
        var upgraded = await new PackageUpgradeService(share, flat).UpgradePackageAsync(created.PackagePath, "2.0", installer);
        Assert.Equal(Path.Combine(share, "Contoso", "Acme_Reader", "2.0"), upgraded.PackagePath);
        Assert.False(Directory.Exists(Path.Combine(share, "Acme_Reader")));
    }

    [Fact]
    public void LinkedPackagesAreFoundUnderBothLayouts()
    {
        var flat = Path.Combine(_root, "Contoso_App", "1.0");
        var nested = Path.Combine(_root, "Fabrikam", "Tool", "2.0");
        var unlinked = Path.Combine(_root, "Fabrikam", "Other", "1.0");
        foreach (var folder in new[] { flat, nested, unlinked })
            Directory.CreateDirectory(PsadtLayout.ApplicationFolder(folder));

        PackageMarker.Save(nested, new PackageMarker.Link { AppId = AppId, TenantId = Tenant });
        Assert.Equal(new[] { nested }, PackageSourceLocator.FindAllLinked(_root, AppId, Tenant));

        PackageMarker.Save(flat, new PackageMarker.Link { AppId = AppId, TenantId = Tenant });
        Assert.Equal(2, PackageSourceLocator.FindAllLinked(_root, AppId, Tenant).Count);
        Assert.Null(PackageSourceLocator.FindLinked(_root, AppId, Tenant));
    }

    [Fact]
    public void SettingsEditorPreviewsAndRoundTripsTheFormat()
    {
        var layout = new PackageLayoutViewModel();
        Assert.Equal(PackagePaths.DefaultFolderTemplate, new AppSettings.PackageLayoutConfig().FolderNameTemplate);
        Assert.Equal(PackagePaths.DefaultFolderTemplate, layout.FolderNameTemplate);
        Assert.Equal("Contoso_Acme_Reader", layout.TreeLines[0].Name);
        Assert.Equal("1.0", layout.TreeLines[1].Name);

        layout.UseFolderNamePresetCommand.Execute(@"%vendor%\%appName%");
        Assert.Equal(new[] { "Contoso", "Acme_Reader", "1.0" }, layout.TreeLines.Take(3).Select(l => l.Name).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, layout.TreeLines.Take(3).Select(l => l.Depth).ToArray());
        Assert.Contains(@"Contoso\Acme_Reader\1.0", layout.FolderNamePreview);
        Assert.Equal(@"%vendor%\%appName%", layout.ToConfig().FolderNameTemplate);

        // A format that can't be used shows why, and the tree falls back to the default.
        layout.FolderNameTemplate = "%vendor%";
        Assert.True(layout.HasFolderNameProblem);
        Assert.Equal("Contoso_Acme_Reader", layout.TreeLines[0].Name);

        layout.FolderNameTemplate = "  ";
        Assert.False(layout.HasFolderNameProblem);
        Assert.Equal(PackagePaths.DefaultFolderTemplate, layout.ToConfig().FolderNameTemplate);

        // A settings file from before the option existed loads the default.
        layout.Load(new AppSettings.PackageLayoutConfig { FolderNameTemplate = "" });
        Assert.Equal(PackagePaths.DefaultFolderTemplate, layout.FolderNameTemplate);
    }
}
