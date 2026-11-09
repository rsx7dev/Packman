using System.IO;
using Packman.Helpers;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>Settings ▸ Paths: the Remote test staging folder, the template's log folder and the upload logs folder.</summary>
public sealed class PathSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-paths-").FullName;

    public void Dispose()
    {
        UploadLogger.ConfiguredFolder = "";
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("", @"C:\ProgramData\Packman")]
    [InlineData("  ", @"C:\ProgramData\Packman")]
    [InlineData(@"D:\Staging", @"D:\Staging\Packman")]
    [InlineData(@"D:\Staging\", @"D:\Staging\Packman")]
    // A drive root is fine: only the Packman subfolder is ever secured or renamed.
    [InlineData(@"E:\", @"E:\Packman")]
    [InlineData(@"E:", @"E:\Packman")]
    public void StagingAlwaysUsesItsOwnSubfolder(string parent, string expected)
    {
        Assert.Null(RemoteStaging.Problem(parent));
        Assert.Equal(expected, RemoteStaging.BasePath(parent));
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"Staging")]
    [InlineData(@"%ProgramData%")]
    [InlineData(@"$env:ProgramData")]
    [InlineData(@"C:\Temp\..\Windows")]
    [InlineData(@"C:\Bad|Name")]
    [InlineData(@"C:\It's")]
    public void StagingRefusesPathsThatAreNotPlainLocalFolders(string parent)
    {
        Assert.NotNull(RemoteStaging.Problem(parent));
        Assert.Throws<ArgumentException>(() => RemoteStaging.BasePath(parent));
    }

    [Fact]
    public void StagingAdminShareFollowsTheDrive()
    {
        Assert.Equal(@"\\PC1\C$\ProgramData\Packman\App_1.0", RemoteStaging.AdminShare("PC1", @"C:\ProgramData\Packman\App_1.0"));
        Assert.Equal(@"\\PC1\D$\Staging\Packman", RemoteStaging.AdminShare("PC1", @"d:\Staging\Packman"));
    }

    [Fact]
    public void DefaultStagingStaysWhereItWas() =>
        Assert.Equal(RemoteTestService.RemoteBasePath, RemoteStaging.BasePath(""));

    [Fact]
    public void LogFolderComesFromTheToolkitSection()
    {
        const string psd1 = """
            @{
                MSI = @{
                    LogPath = $null
                }
                Toolkit = @{
                    LogPath = '$env:SystemRoot\Logs\Custom'
                    LogPathNoAdminRights = '$env:ProgramData\Logs\Software'
                }
            }
            """;
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        Assert.Equal($@"{systemRoot}\Logs\Custom", PsadtConfig.LogFolderFromText(psd1));
    }

    [Fact]
    public void LogFolderFallsBackToPsadtsDefault()
    {
        Assert.Null(PsadtConfig.LogFolderFromText("@{ Toolkit = @{ LogPath = $null } }"));
        Assert.Null(PsadtConfig.LogFolderFromText("@{ Toolkit = @{ LogPath = '$env:NoSuchPackmanVariable\\Logs' } }"));
        Assert.Equal(PsadtConfig.DefaultLogFolder, PsadtConfig.LogFolder(_folder));
        Assert.Equal(PsadtConfig.DefaultLogFolder, PsadtConfig.LogFolder(null));
    }

    [Fact]
    public void LogFolderReadsTheTemplateFileOrItsScript()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Config"));
        File.WriteAllText(Path.Combine(_folder, "Config", "config.psd1"), "@{\n Toolkit = @{\n  LogPath = 'D:\\PsadtLogs'\n }\n}");
        Assert.Equal(@"D:\PsadtLogs", PsadtConfig.LogFolder(_folder));
        Assert.Equal(@"D:\PsadtLogs", PsadtConfig.LogFolder(Path.Combine(_folder, "Invoke-AppDeployToolkit.ps1")));
    }

    [Fact]
    public void LogSwitchFollowsTheTemplateLogFolder()
    {
        var burn = InstallerCatalog.Kinds.Single(k => k.Name == "WiX Burn");
        var log = burn.Switches.Single(s => s.Text.StartsWith("/log", StringComparison.Ordinal));

        Assert.Equal(@"/log D:\PsadtLogs\Install.log", InstallerCatalog.WithLogFolder(log, @"D:\PsadtLogs").Text);
        Assert.Same(log, InstallerCatalog.WithLogFolder(log, PsadtConfig.DefaultLogFolder));
        var quiet = burn.Switches.First(s => s.Text == "/quiet");
        Assert.Same(quiet, InstallerCatalog.WithLogFolder(quiet, @"D:\PsadtLogs"));
    }

    [Fact]
    public void UploadLogsGoToTheConfiguredFolder()
    {
        UploadLogger.ConfiguredFolder = Path.Combine(_folder, "share");
        string path;
        using (var log = new UploadLogger("Packman-QA-paths", "Upload"))
        {
            log.Info("hello");
            path = log.LogFilePath;
        }
        Assert.StartsWith(Path.Combine(_folder, "share", "Upload"), path);
        var text = File.ReadAllText(path);
        Assert.Contains("hello", text);
        Assert.Contains($"on {Environment.MachineName}", text);
    }

    [Fact]
    public void UploadLogsFallBackLocallyWhenTheFolderCantBeUsed()
    {
        // A file where the folder should be: CreateDirectory fails.
        var blocked = Path.Combine(_folder, "blocked");
        File.WriteAllText(blocked, "");
        UploadLogger.ConfiguredFolder = blocked;

        using var log = new UploadLogger("Packman-QA-paths-fallback", "Upload");
        Assert.StartsWith(UploadLogger.DefaultFolder, log.LogFilePath);
        log.Dispose();
        Assert.Contains("logging locally instead", File.ReadAllText(log.LogFilePath));
        File.Delete(log.LogFilePath);
    }

    [Fact]
    public void EmptyUploadLogsFolderMeansLocalAppData() =>
        Assert.Equal(UploadLogger.DefaultFolder, UploadLogger.FolderFor("  "));
}
