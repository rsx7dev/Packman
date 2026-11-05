using System.IO;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>A remote test run reports to the live console as well as its own Output area.</summary>
public sealed class RemoteTestConsoleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-remote-console-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AFailedRunShowsItsStepsAndFailureInTheConsole()
    {
        var vm = new RemoteTestViewModel(new SettingsService(Path.Combine(_root, "appsettings.json")))
        {
            TargetComputer = "qa-unavailable.invalid",
        };
        vm.SelectPackage(Fakes.Package(Path.Combine(_root, "Contoso_Remote", "1.0")));
        ActivityLog.Shared.Clear();

        vm.InstallCommand.Execute(null);
        for (var i = 0; i < 600 && vm.LastRunSucceeded == null; i++) await Task.Delay(100);

        Assert.False(vm.LastRunSucceeded);
        var lines = ActivityLog.Shared.Lines;
        Assert.Equal((ActivityLevel.Section, "Remote test: install on qa-unavailable.invalid"), (lines[0].Level, lines[0].Text));
        Assert.Contains(lines, l => l.Level == ActivityLevel.Error && l.Text.Contains("INSTALL FAILED"));
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("===="));
    }

    [Fact]
    public async Task TheOnlineCheckShowsItsStartAndItsOutcomeInTheConsole()
    {
        var vm = new RemoteTestViewModel(new SettingsService(Path.Combine(_root, "appsettings.json")))
        {
            TargetComputer = "qa-unavailable.invalid",
        };
        ActivityLog.Shared.Clear();

        vm.CheckOnlineCommand.Execute(null);
        for (var i = 0; i < 100 && vm.StatusText is "checking…" or "not checked"; i++) await Task.Delay(100);

        var lines = ActivityLog.Shared.Lines;
        Assert.Equal("unreachable", vm.StatusText);
        Assert.Equal((ActivityLevel.Info, "Checking whether qa-unavailable.invalid is online…"), (lines[0].Level, lines[0].Text));
        Assert.Equal(ActivityLevel.Warning, lines[^1].Level);
        Assert.Contains("unreachable", lines[^1].Text);
    }
}
