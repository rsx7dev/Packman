using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Packman.Helpers;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// IntuneWinAppUtil.exe is downloaded, not shipped. Microsoft's server is answered by a handler
/// and the pinned hash is replaced by the hash of the test bytes; nothing reaches the network.
/// </summary>
public sealed class ContentPrepToolTests : IDisposable
{
    private static readonly byte[] Tool = "the pinned build"u8.ToArray();
    private static readonly string ToolHash = Convert.ToHexStringLower(SHA256.HashData(Tool));

    private readonly HttpClient _http = ContentPrepTool.Http;
    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-tool-").FullName;
    private string Target => Path.Combine(_folder, "Tools", "IntuneWinAppUtil.exe");

    public void Dispose()
    {
        ContentPrepTool.Http = _http;
        Directory.Delete(_folder, recursive: true);
    }

    private static RecordingHandler Serve(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        ContentPrepTool.Http = new HttpClient(handler);
        return handler;
    }

    [Fact]
    public async Task MissingToolIsDownloadedFromThePinnedAddress()
    {
        var handler = Serve(Tool);

        Assert.True(await ContentPrepTool.EnsureAsync(Target, ToolHash, CancellationToken.None));

        Assert.Equal(Tool, File.ReadAllBytes(Target));
        Assert.Equal(("GET", ContentPrepTool.DownloadUri.ToString()), Assert.Single(handler.Requests));
        Assert.Equal([Target], Directory.GetFiles(Path.GetDirectoryName(Target)!));
    }

    [Fact]
    public async Task VerifiedToolIsNotDownloadedAgain()
    {
        Serve(Tool);
        await ContentPrepTool.EnsureAsync(Target, ToolHash, CancellationToken.None);
        var handler = Serve(Tool);

        Assert.False(await ContentPrepTool.EnsureAsync(Target, ToolHash, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ChangedToolIsReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "something else");
        Serve(Tool);

        Assert.False(ContentPrepTool.IsVerified(Target, ToolHash));
        Assert.True(await ContentPrepTool.EnsureAsync(Target, ToolHash, CancellationToken.None));
        Assert.Equal(Tool, File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task DownloadWithAnotherHashIsNotSaved()
    {
        Serve("not the pinned build"u8.ToArray());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ContentPrepTool.EnsureAsync(Target, ToolHash, CancellationToken.None));

        Assert.Contains("was not saved", ex.Message);
        Assert.Contains(ToolHash, ex.Message);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task FailedDownloadSaysWhatToDo()
    {
        Serve([], HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ContentPrepTool.EnsureAsync(Target, ToolHash, CancellationToken.None));

        Assert.Contains("could not be downloaded", ex.Message);
        Assert.Contains("IntuneWinAppUtil Path", ex.Message);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public void OnlyTheDefaultPathIsManaged()
    {
        Assert.True(ContentPrepTool.IsManaged(BundledTools.IntuneWinAppUtil));
        Assert.True(ContentPrepTool.IsManaged(BundledTools.IntuneWinAppUtil.ToUpperInvariant()));
        Assert.False(ContentPrepTool.IsManaged(Target));
        Assert.False(ContentPrepTool.IsManaged(""));
        Assert.False(BundledTools.IntuneWinAppUtil.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }
}
