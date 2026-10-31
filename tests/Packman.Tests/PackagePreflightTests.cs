using System.IO;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>The generator's EXE flag placeholders block a package only when the script uses them.</summary>
public sealed class PackagePreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-preflight-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void CompletePackagePasses() => Assert.Empty(PackagePreflight.Check(Fakes.Package(_root)));

    [Fact]
    public void PlaceholderInAStringIsFlagged()
    {
        var package = Fakes.Package(_root, "Start-ADTProcess -FilePath 'setup.exe' -ArgumentList '<silent flags>'\n");

        var issue = Assert.Single(PackagePreflight.Check(package));
        Assert.Contains("placeholder", issue);
    }

    [Fact]
    public void PlaceholderInACommentIsNotFlagged()
    {
        var package = Fakes.Package(_root, "# Replace '<silent flags>' and '<uninstall flags>' with the vendor's switches.\n$x = 1\n");

        Assert.Empty(PackagePreflight.Check(package));
    }

    [Fact]
    public void MissingRuntimeIsFlagged()
    {
        var package = Fakes.Package(_root);
        File.Delete(Path.Combine(package, "Application", "PSAppDeployToolkit", "PSAppDeployToolkit.psd1"));

        Assert.Contains(PackagePreflight.Check(package), issue => issue.Contains("PSAppDeployToolkit.psd1"));
    }
}
