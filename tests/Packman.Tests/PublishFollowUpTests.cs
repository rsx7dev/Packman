using System.IO;
using Packman.Models;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// What a publish says when Intune is still processing the content after the wait, the label
/// the app gets in Intune, and the signing line in the publish log.
/// </summary>
public sealed class PublishFollowUpTests : PublishPipelineFixture
{
    private IntuneUploadService SlowTenantService()
    {
        Intune.PublishingState = "processing";
        return new IntuneUploadService(() => Task.FromResult("test-token"), Tenant, null, Fakes.IntuneWinAppUtil(Path.Combine(Root, ".tools")))
        {
            PublishWait = TimeSpan.FromMilliseconds(300),
        };
    }

    private Task<string> Upload(IntuneUploadService service, IReadOnlyList<SupersedenceLink>? supersedence = null,
        AppSettings.GroupAssignmentConfig? groupAssignment = null, IEnumerable<AssignedGroup>? pickedGroups = null) =>
        Task.Run(() => service.UploadWin32ApplicationAsync(Info(), Fakes.Package(Path.Combine(Root, "Contoso_FollowUp")), Rules(),
            AppSettings.IntuneDefaultsConfig.DefaultInstallCommand, AppSettings.IntuneDefaultsConfig.DefaultUninstallCommand,
            "scripted tenant", "system", supersedence: supersedence, groupAssignment: groupAssignment, pickedGroups: pickedGroups));

    [Fact]
    public async Task AnAppIntuneIsStillProcessingNamesEverythingThatWasNotDone()
    {
        var config = new AppSettings.GroupAssignmentConfig
        {
            CreateGroupPerPackage = true,
            ExistingGroups = [new AppSettings.ExistingGroupAssignment { GroupName = "Contoso Pilot", Intent = AssignmentIntent.Available }],
        };
        AssignedGroup[] picked = [new() { GroupId = "group-1", GroupName = "Finance laptops", AssignmentType = "required" }];

        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(() => Upload(SlowTenantService(),
            supersedence: [new SupersedenceLink("old-app", "update")], groupAssignment: config, pickedGroups: picked));

        Assert.Equal(ScriptedIntune.AppId, ex.AppId);
        Assert.Contains("did not report it as published", ex.Message);
        Assert.Contains("supersedence of 1 app", ex.Message);
        Assert.Contains("Finance laptops", ex.Message);
        Assert.Contains("Contoso Pilot", ex.Message);
        Assert.Contains($"Contoso_{AppName}_1.0_Install (not created)", ex.Message);
        Assert.Contains("Wait for Intune processing", ex.Message);
        // Nothing that needs a published app was attempted, and the app was kept.
        Assert.Empty(Intune.Where("POST", "/updateRelationships"));
        Assert.Empty(Intune.Where("POST", "/assignments"));
        Assert.Empty(Intune.Where("POST", "/groups"));
        Assert.Empty(Intune.Deletes);
    }

    [Fact]
    public async Task WithNothingLeftToDoTheMessageSaysSo()
    {
        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(() => Upload(SlowTenantService()));

        Assert.Contains("Nothing else was left to do", ex.Message);
        Assert.DoesNotContain("Not done", ex.Message);
    }

    [Fact]
    public void TheSkippedListLeavesOutWhatWasNeverAskedFor()
    {
        Assert.Empty(IntuneUploadService.SkippedFollowUp(Info(), null, null, []));
        Assert.Empty(IntuneUploadService.SkippedFollowUp(Info(), [], new AppSettings.GroupAssignmentConfig(), []));

        var skipped = IntuneUploadService.SkippedFollowUp(Info(),
            [new SupersedenceLink("a", "update"), new SupersedenceLink("b", "replace")], null,
            [new AssignedGroup { GroupId = "id-only" }]);

        Assert.Equal(["supersedence of 2 apps", "assignment to the group id-only"], skipped);
    }

    [Fact]
    public void TheWaitIsReadFromSettingsWithinItsLimits()
    {
        var defaults = new AppSettings.IntuneDefaultsConfig();
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.PublishWait);

        defaults.PublishWaitMinutes = 45;
        Assert.Equal(TimeSpan.FromMinutes(45), defaults.PublishWait);
        defaults.PublishWaitMinutes = 0;
        Assert.Equal(TimeSpan.FromMinutes(1), defaults.PublishWait);
        defaults.PublishWaitMinutes = 100_000;
        Assert.Equal(TimeSpan.FromMinutes(AppSettings.IntuneDefaultsConfig.MaxPublishWaitMinutes), defaults.PublishWait);
    }

    [Theory]
    [InlineData("Contoso", "Reader", "4.2.0", "Contoso_Reader_4.2.0.intunewin")]
    [InlineData("O'Reilly Media", "Safari: Books", "1.0", "O'Reilly_Media_Safari__Books_1.0.intunewin")]
    [InlineData("", "Reader", "", "Reader.intunewin")]
    [InlineData("", "", "", "IntunePackage.intunewin")]
    public void TheAppIsLabelledWithItsVendorNameAndVersion(string vendor, string name, string version, string expected) =>
        Assert.Equal(expected, IntuneUploadService.PackageFileLabel(new ApplicationInfo { Manufacturer = vendor, Name = name, Version = version }));

    [Fact]
    public void AVeryLongLabelIsCutShort()
    {
        var label = IntuneUploadService.PackageFileLabel(new ApplicationInfo { Manufacturer = new string('a', 200), Name = "Reader", Version = "1.0" });

        Assert.True(label.Length <= 120 + ".intunewin".Length);
        Assert.EndsWith(".intunewin", label);
    }

    [Theory]
    [InlineData(1, "Invoke-AppDeployToolkit.ps1 signed successfully")]
    [InlineData(2, "Invoke-AppDeployToolkit.ps1 signed successfully, with 1 other PowerShell file")]
    [InlineData(31, "Invoke-AppDeployToolkit.ps1 signed successfully, with 30 other PowerShell files")]
    public void TheSigningLineSaysHowManyFilesWereSigned(int files, string expected) =>
        Assert.Equal(expected, IntuneUploadService.SignedSummary("Invoke-AppDeployToolkit.ps1", files));
}
