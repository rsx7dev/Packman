using System.IO;
using Packman.Models;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>SettingsService against a temp settings file: the group migration and corrupt-file quarantine.</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-settings-").FullName;
    private string SettingsPath => Path.Combine(_folder, "appsettings.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AppSettings LoadJson(string json)
    {
        File.WriteAllText(SettingsPath, json);
        return new SettingsService(SettingsPath).Settings;
    }

    [Fact]
    public void PerPackageGroupWithUninstallIntentMovesToTheUninstallGroup()
    {
        var groups = LoadJson("""
            { "GroupAssignment": { "CreateGroupPerPackage": true, "NewGroupIntent": "Uninstall", "GroupNameTemplate": "Legacy_%appName%" } }
            """).GroupAssignment;

        Assert.True(groups.CreateUninstallGroupPerPackage);
        Assert.Equal("Legacy_%appName%", groups.UninstallGroupNameTemplate);
        Assert.False(groups.CreateGroupPerPackage);
        Assert.Equal(AssignmentIntent.Required, groups.NewGroupIntent);
    }

    [Fact]
    public void PerPackageGroupWithRequiredIntentIsUnchanged()
    {
        var defaults = new AppSettings.GroupAssignmentConfig();
        var groups = LoadJson("""
            { "GroupAssignment": { "CreateGroupPerPackage": true, "NewGroupIntent": "Required", "GroupNameTemplate": "Keep_%appName%" } }
            """).GroupAssignment;

        Assert.True(groups.CreateGroupPerPackage);
        Assert.Equal(AssignmentIntent.Required, groups.NewGroupIntent);
        Assert.Equal("Keep_%appName%", groups.GroupNameTemplate);
        Assert.False(groups.CreateUninstallGroupPerPackage);
        Assert.Equal(defaults.UninstallGroupNameTemplate, groups.UninstallGroupNameTemplate);
    }

    [Fact]
    public void CorruptFileIsQuarantinedAndDefaultsLoad()
    {
        const string corrupt = "{not json";
        File.WriteAllText(SettingsPath, corrupt);
        var service = new SettingsService(SettingsPath);

        var settings = service.Settings;

        Assert.NotNull(service.LoadError);
        Assert.Equal(new AppSettings().AuthMode, settings.AuthMode);
        Assert.False(File.Exists(SettingsPath));
        var backup = Assert.Single(Directory.GetFiles(_folder, "appsettings.json.corrupt-*"));
        Assert.Equal(corrupt, File.ReadAllText(backup));
        Assert.Contains(backup, service.LoadError);
    }

    [Fact]
    public void SavingAfterQuarantineWritesAFileThatReloadsCleanly()
    {
        File.WriteAllText(SettingsPath, "{not json");
        var service = new SettingsService(SettingsPath);
        service.Settings.NetworkPaths.IntuneApplications = @"\\server\share\Apps";

        service.Save();
        var reopened = new SettingsService(SettingsPath);

        Assert.Equal(@"\\server\share\Apps", reopened.Settings.NetworkPaths.IntuneApplications);
        Assert.Null(reopened.LoadError);
    }
}
