using System.IO;
using Packman.Helpers;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>The Create page's welcome card: what it lists, when it refreshes, and when it may stay hidden.</summary>
public sealed class SetupChecklistTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-checklist-").FullName;
    private readonly string _settingsPath;
    private readonly SettingsService _settings;
    private readonly IntuneAuthService _auth = new();

    public SetupChecklistTests()
    {
        _settingsPath = Path.Combine(_folder, "appsettings.json");
        _settings = new SettingsService(_settingsPath);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Kind(SetupChecklistViewModel checklist, string label) => Assert.Single(checklist.Items, i => i.Label == label).Kind;

    private void ConfigureEverything()
    {
        var paths = _settings.Settings.NetworkPaths;
        paths.IntuneApplications = Directory.CreateDirectory(Path.Combine(_folder, "share")).FullName;
        var template = Directory.CreateDirectory(Path.Combine(_folder, "template", PsadtLayout.ToolkitFolderName)).Parent!.FullName;
        File.WriteAllText(Path.Combine(template, PsadtLayout.ScriptName), "");
        paths.PSADTTemplate = template;
        paths.IntuneWinAppUtil = Path.Combine(_folder, "IntuneWinAppUtil.exe");
        File.WriteAllText(paths.IntuneWinAppUtil, "");
    }

    [Fact]
    public async Task FreshInstallListsEveryPrerequisiteAsMissing()
    {
        var checklist = new SetupChecklistViewModel(_settings, _auth);
        await checklist.RefreshAsync();

        Assert.Equal(new[] { "Output share", "PSADT template", "IntuneWinAppUtil", "Intune sign-in" }, checklist.Items.Select(i => i.Label));
        Assert.Equal("bad", Kind(checklist, "Output share"));
        Assert.Equal("bad", Kind(checklist, "PSADT template"));
        Assert.Equal("bad", Kind(checklist, "IntuneWinAppUtil"));
        Assert.Equal("warn", Kind(checklist, "Intune sign-in"));
        Assert.True(checklist.HasMissing);
        Assert.True(checklist.IsVisible);
        Assert.False(checklist.IsChecking);
    }

    [Fact]
    public async Task DismissingWhileSomethingIsMissingKeepsTheCard()
    {
        var checklist = new SetupChecklistViewModel(_settings, _auth);
        await checklist.RefreshAsync();

        checklist.DismissCommand.Execute(null);

        Assert.True(checklist.IsVisible);
        Assert.True(new SettingsService(_settingsPath).Settings.WelcomeCardDismissed);
    }

    [Fact]
    public async Task SavingSettingsRefreshesTheCardAndDismissalHidesIt()
    {
        var checklist = new SetupChecklistViewModel(_settings, _auth);
        await checklist.RefreshAsync();

        ConfigureEverything();
        _settings.Save();
        await WizardGateTests.WaitForAsync(() => !checklist.HasMissing);

        Assert.Equal("ok", Kind(checklist, "Output share"));
        Assert.Equal("ok", Kind(checklist, "PSADT template"));
        Assert.Equal("ok", Kind(checklist, "IntuneWinAppUtil"));
        Assert.True(checklist.IsVisible);

        checklist.DismissCommand.Execute(null);
        Assert.False(checklist.IsVisible);

        // Remembered: a new session starts with the card hidden.
        var reopened = new SetupChecklistViewModel(new SettingsService(_settingsPath), _auth);
        await reopened.RefreshAsync();
        Assert.False(reopened.IsVisible);
    }

    [Fact]
    public async Task SignInRefreshesTheCard()
    {
        var checklist = new SetupChecklistViewModel(_settings, _auth);
        await checklist.RefreshAsync();

        _auth.UseTestSession("11111111-1111-1111-1111-111111111111", Fakes.Token(""));
        await WizardGateTests.WaitForAsync(() => Kind(checklist, "Intune sign-in") == "ok");

        Assert.Contains("test", Assert.Single(checklist.Items, i => i.Label == "Intune sign-in").Message);
    }

    [Fact]
    public void CreatePageHidesTheCardOnceAPackageExists()
    {
        var vm = new MainViewModel(_settings, _auth);
        Assert.True(vm.ShowSetupChecklist);

        vm.CreatePackage.CurrentPackagePath = Fakes.Package(Path.Combine(_folder, "package"));
        Assert.False(vm.ShowSetupChecklist);
    }
}
