using System.IO;
using System.Net;
using Packman.Models;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// A failed new upload removes the app it created. Graph and Azure Storage are answered by
/// <see cref="FakeIntune"/>; the converter is a .cmd stand-in (Windows only).
/// </summary>
public sealed class IntuneUploadServiceTests : GraphStaticsFixture
{
    private const string AppName = "PackmanTestsRollback";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-upload-").FullName;
    private readonly FakeIntune _intune = new();

    public IntuneUploadServiceTests() => UseHandler(_intune);

    public override void Dispose()
    {
        base.Dispose();
        Directory.Delete(_root, recursive: true);
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packman", "Logs", "Upload");
        if (Directory.Exists(logs))
            foreach (var log in Directory.GetFiles(logs, $"{AppName}-*.log"))
                File.Delete(log);
    }

    private Task<string> Upload(IntuneUploadService service)
    {
        var package = Fakes.Package(Path.Combine(_root, "Contoso_Rollback", "1.0"));
        var appInfo = new ApplicationInfo { Manufacturer = "Contoso", Name = AppName, Version = "1.0", DisplayName = AppName };
        var rules = new List<DetectionRule> { new() { Type = DetectionRuleType.File, Path = @"C:\Program Files\Contoso", FileOrFolderName = "app.exe" } };
        return Task.Run(() => service.UploadWin32ApplicationAsync(appInfo, package, rules,
            "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.exe -DeploymentType Uninstall", "test", "system"));
    }

    private IntuneUploadService Service() =>
        new(() => Task.FromResult("test-token"), Tenant, null, Fakes.IntuneWinAppUtil(Path.Combine(_root, ".tools")));

    private IReadOnlyList<(string Method, string Path)> Deletes =>
        _intune.Requests.Where(r => r.Method == "DELETE").ToList();

    [Fact]
    public async Task FailedContentVersionRemovesTheCreatedApp()
    {
        _intune.ContentVersionStatus = HttpStatusCode.BadRequest;
        var service = Service();

        var ex = await Assert.ThrowsAsync<GraphException>(() => Upload(service));

        Assert.Equal(400, ex.StatusCode);
        var delete = Assert.Single(Deletes);
        Assert.EndsWith($"/mobileApps/{FakeIntune.CreatedAppId}", delete.Path);
        Assert.True(service.RollbackSucceeded);
    }

    [Fact]
    public async Task RejectedRollbackIsReportedAndTheOriginalErrorIsThrown()
    {
        _intune.ContentVersionStatus = HttpStatusCode.BadRequest;
        _intune.DeleteStatus = HttpStatusCode.Forbidden;
        var service = Service();

        var ex = await Assert.ThrowsAsync<GraphException>(() => Upload(service));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Create content version", ex.Operation);
        Assert.Single(Deletes);
        Assert.False(service.RollbackSucceeded);
    }

    [Fact]
    public async Task FailedCreateHasNothingToRollBack()
    {
        _intune.CreateAppStatus = HttpStatusCode.BadRequest;
        var service = Service();

        var ex = await Assert.ThrowsAsync<GraphException>(() => Upload(service));

        Assert.Equal(400, ex.StatusCode);
        Assert.Empty(Deletes);
        Assert.Null(service.RollbackSucceeded);
    }
}
