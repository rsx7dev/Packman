using System.IO;
using System.Management.Automation.Language;
using System.Net;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>Scopes, staging, auto-fill and the welcome card, as their callers see them.</summary>
public sealed class ContractTests : GraphStaticsFixture
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string GroupsProbe = "Entra groups · read access";
    private const string DevicesProbe = "Entra devices · read access";
    private const string UsersProbe = "Entra users · read access";

    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-contract-").FullName;

    public override void Dispose()
    {
        base.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    // ── Sign-in scopes (SEC-02) ──

    [Fact]
    public void SignInIsReadOnly()
    {
        Assert.Equal(new[] { "User.Read", "DeviceManagementApps.Read.All" }, IntuneAuthService.InteractiveScopes);
        Assert.DoesNotContain(IntuneAuthService.InteractiveScopes, scope => scope.Contains("ReadWrite"));
    }

    [Fact]
    public void SignInLeavesGroupAndDeviceScopesToAdvanced()
    {
        Assert.DoesNotContain(IntuneAuthService.InteractiveScopes, scope =>
            scope.StartsWith("Group", StringComparison.OrdinalIgnoreCase) || scope.StartsWith("Device.", StringComparison.OrdinalIgnoreCase));
        Assert.Subset(IntuneAuthService.DirectoryScopes.ToHashSet(), IntuneAuthService.GroupCreateScopes.ToHashSet());
    }

    [Fact]
    public void WriteScopesAreRequestedSeparately()
    {
        Assert.Equal(new[] { "DeviceManagementApps.ReadWrite.All" }, IntuneAuthService.PublishScopes);
        Assert.Equal(new[] { "Group.ReadWrite.All", "GroupMember.ReadWrite.All", "Device.Read.All", "User.ReadBasic.All" }, IntuneAuthService.DirectoryScopes);
        Assert.Equal(new[] { "Group.ReadWrite.All" }, IntuneAuthService.GroupCreateScopes);
    }

    [Fact]
    public async Task EnsureScopesOnATestSessionKeepsTheSignIn()
    {
        var auth = new IntuneAuthService();
        auth.UseTestSession(Tenant, "test-token");

        Assert.True(await auth.EnsureScopesAsync(IntuneAuthService.DirectoryScopes, 0));

        Assert.Equal("test", auth.SignedInUser);
        Assert.Equal(Tenant, auth.TenantId);
        Assert.True(auth.IsSignedIn);
    }

    [Fact]
    public void HasScopesIsTrueWithoutAnInteractiveSessionToWiden()
    {
        var auth = new IntuneAuthService();
        Assert.True(auth.HasScopes(IntuneAuthService.DirectoryScopes));

        auth.UseTestSession(Tenant, "test-token");
        Assert.True(auth.HasScopes(IntuneAuthService.DirectoryScopes));
    }

    [Fact]
    public async Task ConnectionTestSkipsDirectoryProbesTheSignInCannotMake()
    {
        var handler = AnswerProbes();

        var result = await new IntuneService(() => Task.FromResult(Fakes.Token("DeviceManagementApps.ReadWrite.All")))
            .TestConnectionAsync(false);

        Assert.DoesNotContain(handler.Requests, r => IsDirectoryProbe(r.Url));
        Assert.True(result.Success);
        foreach (var name in new[] { GroupsProbe, DevicesProbe, UsersProbe })
        {
            var row = Assert.Single(result.Checks, c => c.Name == name);
            Assert.True(row.Ok);
            Assert.Equal("Requested when Advanced opens", row.Detail);
        }
    }

    [Fact]
    public async Task ConnectionTestProbesTheDirectoryWhenGranted()
    {
        var handler = AnswerProbes();

        var result = await new IntuneService(() => Task.FromResult(
                Fakes.Token("DeviceManagementApps.ReadWrite.All Group.ReadWrite.All Device.Read.All User.ReadBasic.All")))
            .TestConnectionAsync(false);

        Assert.Contains(handler.Requests, r => r.Url.StartsWith(GraphClient.Groups + "?"));
        Assert.Contains(handler.Requests, r => r.Url.StartsWith(GraphClient.Devices + "?"));
        Assert.Contains(handler.Requests, r => r.Url.StartsWith(GraphClient.Users + "?"));
        Assert.Equal("OK", Assert.Single(result.Checks, c => c.Name == DevicesProbe).Detail);
        Assert.Equal("OK", Assert.Single(result.Checks, c => c.Name == UsersProbe).Detail);
    }

    private static RecordingHandler AnswerProbes()
    {
        var handler = new RecordingHandler((_, _) => Fakes.Json(HttpStatusCode.OK, "{\"value\":[]}"));
        UseHandler(handler);
        return handler;
    }

    private static bool IsDirectoryProbe(string url) =>
        url.StartsWith(GraphClient.Groups, StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith(GraphClient.Devices, StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith(GraphClient.Users, StringComparison.OrdinalIgnoreCase);

    // ── Remote test staging (SEC-01) ──

    [Fact]
    public void RemoteStagingIsUnderProgramData() => Assert.Equal(@"C:\ProgramData\Packman", RemoteTestService.RemoteBasePath);

    [Fact]
    public void StagingScriptLocksTheBaseFolderToSystemAndAdministrators()
    {
        var script = RemoteTestService.BuildSecureStagingScript(RemoteTestService.RemoteBasePath);

        Assert.Contains("S-1-5-18", script);
        Assert.Contains("S-1-5-32-544", script);
        Assert.Contains("SetAccessRuleProtection($true, $false)", script);
        Assert.Contains("Rename-Item", script);
        Assert.Contains(@"'C:\ProgramData\Packman'", script);
        Assert.DoesNotContain("takeown", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("icacls", script, StringComparison.OrdinalIgnoreCase);
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void StagingScriptQuotesTheBasePath()
    {
        var script = RemoteTestService.BuildSecureStagingScript(@"C:\ProgramData\O'Brien");

        Assert.Contains(@"'C:\ProgramData\O''Brien'", script);
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
    }

    // ── Installer auto-fill (UI-04) ──

    private string JunkInstaller(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "not a Windows executable");
        return path;
    }

    [Fact]
    public async Task SecondInstallerReplacesAutoFilledName()
    {
        var vm = new CreatePackageViewModel();

        vm.LoadFromFile(JunkInstaller("Alpha.exe"));
        await vm.InstallerRead;
        Assert.Equal("Alpha", vm.AppName);

        vm.LoadFromFile(JunkInstaller("Beta.exe"));
        await vm.InstallerRead;
        Assert.Equal("Beta", vm.AppName);
    }

    [Fact]
    public async Task TypedFieldsSurviveTheNextInstaller()
    {
        var vm = new CreatePackageViewModel();
        vm.LoadFromFile(JunkInstaller("Alpha.exe"));
        await vm.InstallerRead;

        vm.AppName = "Typed Name";
        vm.Manufacturer = "Typed Vendor";
        vm.LoadFromFile(JunkInstaller("Beta.exe"));
        await vm.InstallerRead;

        Assert.Equal("Typed Name", vm.AppName);
        Assert.Equal("Typed Vendor", vm.Manufacturer);
    }

    // ── First-run card (UI-03) ──

    [Fact]
    public void WelcomeCardDismissalRoundTrips()
    {
        var path = Path.Combine(_folder, "appsettings.json");
        var service = new SettingsService(path);
        Assert.False(service.Settings.WelcomeCardDismissed);

        service.Settings.WelcomeCardDismissed = true;
        service.Save();

        Assert.True(new SettingsService(path).Settings.WelcomeCardDismissed);
    }

    [Fact]
    public void WelcomeCardExplainsTheExePlaceholders()
    {
        Assert.Contains("<silent flags>", SetupChecklistViewModel.ExeSwitchesText);
        Assert.Contains("<uninstall flags>", SetupChecklistViewModel.ExeSwitchesText);
    }
}
