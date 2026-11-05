using System.ComponentModel;
using System.IO;
using Packman.Helpers;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>The wizard's primary button: what Generate needs first, and Publish waiting on a clean preflight.</summary>
public sealed class WizardGateTests : IDisposable
{
    private const int ConfigureStep = 1;
    private const int ReviewStep = 2;
    private const string Placeholder = "Start-ADTProcess -FilePath 'setup.exe' -ArgumentList '<silent flags>'\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-wizard-").FullName;
    private readonly SettingsService _settings;
    private readonly IntuneAuthService _auth = new();

    public WizardGateTests() => _settings = new SettingsService(Path.Combine(_folder, "appsettings.json"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void GenerateNamesTheFirstMissingInputUntilAllAreSet()
    {
        var vm = new MainViewModel(_settings, _auth);
        var paths = _settings.Settings.NetworkPaths;
        paths.IntuneApplications = "";

        var steps = new (string Expected, Action Fix)[]
        {
            ("Choose the installer or source folder", () => vm.CreatePackage.SourcesPath = _folder),
            ("Enter the application name", () => vm.CreatePackage.AppName = "App"),
            ("Enter the manufacturer", () => vm.CreatePackage.Manufacturer = "Vendor"),
            ("Enter the version", () => vm.CreatePackage.Version = "1.0"),
            ("Set the output share in Settings", () => paths.IntuneApplications = _folder),
        };
        foreach (var (expected, fix) in steps)
        {
            Assert.Equal(expected, vm.GenerateLockReason);
            Assert.Equal(expected, vm.PrimaryLockReason);
            Assert.False(vm.CanGenerate);
            Assert.False(vm.PrimaryCommand.CanExecute(null));
            fix();
        }

        Assert.Null(vm.GenerateLockReason);
        Assert.Null(vm.PrimaryLockReason);
        Assert.True(vm.CanGenerate);
        Assert.True(vm.PrimaryCommand.CanExecute(null));
    }

    [Fact]
    public void GenerateIsNotGatedOnceAPackageExists()
    {
        var vm = new MainViewModel(_settings, _auth);
        vm.CreatePackage.CurrentPackagePath = Fakes.Package(_folder);

        Assert.Null(vm.GenerateLockReason);
        Assert.True(vm.PrimaryCommand.CanExecute(null));
    }

    [Fact]
    public async Task PublishStaysDisabledWhilePreflightRunsAndWhileItFindsIssues()
    {
        _auth.UseTestSession("11111111-1111-1111-1111-111111111111", Fakes.Token(""));
        var package = Fakes.Package(Path.Combine(_folder, "package"), Placeholder);
        var script = Path.Combine(PsadtLayout.ApplicationFolder(package), PsadtLayout.ScriptName);
        var vm = new MainViewModel(_settings, _auth);
        vm.CreatePackage.CurrentPackagePath = package;

        // Every change of PackageIssues, with the gate as it stood at that moment.
        var seen = new List<(bool Checking, bool CanPublish, bool CanExecute, string State)>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PackageIssues))
                lock (seen) seen.Add((vm.PackageIssues == null, vm.CanPublish, vm.PrimaryCommand.CanExecute(null), vm.PackageState));
        };

        vm.CurrentStepIndex = ReviewStep;
        Assert.False(vm.CanPublish);
        await WaitForAsync(() => vm.PackageIssues != null);

        var issue = Assert.Single(vm.PackageIssues!);
        Assert.Contains("placeholder", issue);
        Assert.False(vm.PackageReady);
        Assert.Equal("Needs attention", vm.PackageState);
        Assert.Equal(issue, vm.PrimaryLockReason);
        Assert.False(vm.CanPublish);
        Assert.False(vm.PrimaryCommand.CanExecute(null));

        // Edit script fixes it; closing the tool re-runs the check.
        File.WriteAllText(script, "$adtSession = @{ AppName = 'Test' }\n");
        lock (seen) seen.Clear();
        vm.OpenEditToolCommand.Execute(null);
        vm.CloseToolCommand.Execute(null);
        await WaitForAsync(() => vm.PackageIssues != null);

        lock (seen)
        {
            Assert.Equal((true, false, false, "Checking…"), seen[0]);
            Assert.Equal((false, true, true, "Ready"), seen[^1]);
        }
        Assert.True(vm.PackageReady);
        Assert.Null(vm.PrimaryLockReason);
    }

    [Fact]
    public async Task ConfigureStepAlsoChecksThePackage()
    {
        var vm = new MainViewModel(_settings, _auth);
        vm.CreatePackage.CurrentPackagePath = Fakes.Package(Path.Combine(_folder, "package"), Placeholder);

        vm.CurrentStepIndex = ConfigureStep;
        await WaitForAsync(() => vm.PackageIssues != null);

        Assert.Equal("bad", vm.PackageStateKind);
        Assert.Contains("placeholder", vm.PackageIssue);
    }

    [Fact]
    public async Task PublishNeedsSignInEvenWhenThePackageIsClean()
    {
        var vm = new MainViewModel(_settings, _auth);
        vm.CreatePackage.CurrentPackagePath = Fakes.Package(Path.Combine(_folder, "package"));

        vm.CurrentStepIndex = ReviewStep;
        await WaitForAsync(() => vm.PackageIssues != null);

        Assert.True(vm.PackageReady);
        Assert.False(vm.CanPublish);
        Assert.Equal("Sign in to Intune on the Settings page first", vm.PrimaryLockReason);
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 10_000, "Timed out waiting for the background check");
            await Task.Delay(20);
        }
    }
}
