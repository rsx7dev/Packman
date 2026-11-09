using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>The title bar's tenant label and the rail's feature Add button.</summary>
public sealed class InstallerDetailsTests
{
    [Theory]
    [InlineData("admin@contoso.onmicrosoft.com", "", "tid", "contoso")]
    [InlineData("pat@fabrikam.com", "", "tid", "fabrikam")]
    [InlineData("App registration 123", "contoso.onmicrosoft.com", "tid", "contoso")]
    [InlineData("App registration 123", "3fa85f64-5717-4562-b3fc-2c963f66afa6", "3fa85f64-5717-4562-b3fc-2c963f66afa6", "3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("test", "", "tenant-guid", "tenant-guid")]
    [InlineData("user@", "", "tenant-guid", "tenant-guid")]
    public void TenantLabelNamesTheTenantNeverYour(string user, string configured, string tenantId, string expected) =>
        Assert.Equal(expected, IntuneAuthService.TenantLabel(user, configured, tenantId));

    [Fact]
    public void TenantNameIsEmptyWhenSignedOut() => Assert.Equal("", new IntuneAuthService().TenantName);

    [Theory]
    [InlineData("", "Core", "ADDLOCAL=Core")]
    [InlineData("ALLUSERS=1", "Core", "ALLUSERS=1 ADDLOCAL=Core")]
    [InlineData("ADDLOCAL=Core /qn", "Docs", "ADDLOCAL=Core,Docs /qn")]
    [InlineData("ADDLOCAL=Core,Docs", "Docs", "ADDLOCAL=Core,Docs")]
    public void AddFeatureExtendsAddLocal(string line, string feature, string expected) =>
        Assert.Equal(expected, CreatePackageViewModel.AddFeature(line, feature));

    [Fact]
    public void UnknownExeOffersNoSwitches()
    {
        var vm = new CreatePackageViewModel();
        Assert.Empty(vm.InstallSwitches);
        Assert.Empty(vm.UninstallSwitches);
    }
}
