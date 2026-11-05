using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Packman.Helpers;
using Packman.Services;

namespace Packman.Tests;

/// <summary>Saves the HTTP statics tests swap and puts them back, and makes retries instant.</summary>
public abstract class GraphStaticsFixture : IDisposable
{
    private readonly HttpClient _http = GraphClient.Http;
    private readonly HttpClient _azureBlob = IntuneUploadService.AzureBlob;
    private readonly double _retryDelayScale = GraphClient.RetryDelayScale;

    protected GraphStaticsFixture() => GraphClient.RetryDelayScale = 0;

    /// <summary>Answers Graph (and Azure Storage) with <paramref name="handler"/>.</summary>
    protected static void UseHandler(HttpMessageHandler handler)
    {
        GraphClient.Http = new HttpClient(handler);
        IntuneUploadService.AzureBlob = new HttpClient(handler);
    }

    public virtual void Dispose()
    {
        GraphClient.Http = _http;
        IntuneUploadService.AzureBlob = _azureBlob;
        GraphClient.RetryDelayScale = _retryDelayScale;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Answers each request through a callback given the 1-based call number; records every request.</summary>
public sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<(string Method, string Url)> _requests = [];

    public IReadOnlyList<(string Method, string Url)> Requests { get { lock (_requests) return _requests.ToList(); } }
    public int Calls => Requests.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        int call;
        lock (_requests)
        {
            _requests.Add((request.Method.Method, request.RequestUri!.ToString()));
            call = _requests.Count;
        }
        return Task.FromResult(respond(request, call));
    }
}

/// <summary>
/// Answers the Graph and Azure Storage calls of a new Win32 upload, ported from
/// tests/PackageLinkWpfTests.cs. The status properties make single steps fail.
/// </summary>
public sealed class FakeIntune : HttpMessageHandler
{
    public const string CreatedAppId = "created-app";
    public const string ContentVersionId = "cv-1";

    public HttpStatusCode CreateAppStatus { get; set; } = HttpStatusCode.Created;
    public HttpStatusCode ContentVersionStatus { get; set; } = HttpStatusCode.Created;
    public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;

    private readonly List<(string Method, string Path)> _requests = [];
    private readonly HashSet<string> _committedFiles = [];

    public IReadOnlyList<(string Method, string Path)> Requests { get { lock (_requests) return _requests.ToList(); } }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        lock (_requests) _requests.Add((method, path));

        if (request.RequestUri.Host == "fake.blob.core.windows.net")
            return Respond(HttpStatusCode.Created, "");
        if (method == "DELETE")
            return Respond(DeleteStatus, DeleteStatus == HttpStatusCode.NoContent ? "" : Error("Forbidden"));
        if (method == "POST" && path.EndsWith("/mobileApps"))
            return Respond(CreateAppStatus, CreateAppStatus == HttpStatusCode.Created ? $"{{\"id\":\"{CreatedAppId}\"}}" : Error("BadRequest"));
        if (method == "POST" && path.EndsWith("/contentVersions"))
            return Respond(ContentVersionStatus, ContentVersionStatus == HttpStatusCode.Created ? $"{{\"id\":\"{ContentVersionId}\"}}" : Error("BadRequest"));
        if (method == "POST" && path.EndsWith("/files"))
            return Respond(HttpStatusCode.Created, "{\"id\":\"file-1\"}");
        if (method == "POST" && path.EndsWith("/commit"))
        {
            lock (_committedFiles) _committedFiles.Add(path[..^"/commit".Length]);
            return Respond(HttpStatusCode.OK, "{}");
        }
        if (method == "GET" && path.Contains("/files/"))
        {
            bool committed;
            lock (_committedFiles) committed = _committedFiles.Contains(path);
            return Respond(HttpStatusCode.OK, committed
                ? "{\"uploadState\":\"commitFileSuccess\"}"
                : "{\"uploadState\":\"azureStorageUriRequestSuccess\",\"azureStorageUri\":\"https://fake.blob.core.windows.net/c/f?sig=1\"}");
        }
        if (method == "PATCH")
            return Respond(HttpStatusCode.NoContent, "");
        if (method == "GET" && path.Contains("/mobileApps/"))
            return Respond(HttpStatusCode.OK, "{\"publishingState\":\"published\"}");

        throw new InvalidOperationException($"Unexpected request: {method} {request.RequestUri}");
    }

    private static string Error(string code) => $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{code} from the fake\"}}}}";

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) => Task.FromResult(Fakes.Json(status, body));
}

public static class Fakes
{
    public const long UnencryptedSize = 4096;

    public static HttpResponseMessage Json(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An unsigned JWT carrying <paramref name="scp"/>; Packman only reads its claims.</summary>
    public static string Token(string scp)
    {
        static string Segment(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Segment(new { alg = "none", typ = "JWT" })}.{Segment(new { tid = "11111111-1111-1111-1111-111111111111", scp })}.";
    }

    /// <summary>A minimal PSADT v4 package that passes the upload preflight.</summary>
    public static string Package(string root, string script = "$adtSession = @{ AppName = 'Test' }\n")
    {
        var application = PsadtLayout.ApplicationFolder(root);
        Directory.CreateDirectory(Path.Combine(application, PsadtLayout.ToolkitFolderName));
        File.WriteAllText(Path.Combine(application, PsadtLayout.ScriptName), script);
        File.WriteAllText(Path.Combine(application, PsadtLayout.SetupFileName), "");
        File.WriteAllText(Path.Combine(application, PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psd1"), "@{}\n");
        return root;
    }

    /// <summary>A stand-in IntuneWinAppUtil that copies a prepared .intunewin into the -o folder.</summary>
    public static string IntuneWinAppUtil(string folder)
    {
        Directory.CreateDirectory(folder);
        using (var zip = ZipFile.Open(Path.Combine(folder, "fake.intunewin"), ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("IntuneWinPackage/Metadata/Detection.xml").Open()))
                writer.Write($"""
                    <ApplicationInfo>
                      <Name>Invoke-AppDeployToolkit.exe</Name>
                      <UnencryptedContentSize>{UnencryptedSize}</UnencryptedContentSize>
                      <FileName>IntunePackage.intunewin</FileName>
                      <SetupFile>Invoke-AppDeployToolkit.exe</SetupFile>
                      <EncryptionInfo>
                        <EncryptionKey>a2V5</EncryptionKey><MacKey>bWFj</MacKey><InitializationVector>aXY=</InitializationVector>
                        <Mac>bWFj</Mac><ProfileIdentifier>ProfileVersion1</ProfileIdentifier><FileDigest>ZGln</FileDigest>
                        <FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
                      </EncryptionInfo>
                    </ApplicationInfo>
                    """);
            using var content = zip.CreateEntry("IntuneWinPackage/Contents/IntunePackage.intunewin").Open();
            content.Write(new byte[2048]);
        }
        var tool = Path.Combine(folder, "IntuneWinAppUtil.cmd");
        // Arguments arrive as -c <source> -s <setup> -o <output> -q.
        File.WriteAllText(tool, "@copy /y \"%~dp0fake.intunewin\" \"%~6\\Invoke-AppDeployToolkit.intunewin\" >nul\r\n");
        return tool;
    }
}
