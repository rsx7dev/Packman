using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

// The publish pipeline up to the tenant: Authenticode signing, the real bundled
// IntuneWinAppUtil, and the Graph/Azure Storage conversation driven by a scripted fake.
// Nothing signs in and no tenant is contacted. The only call that leaves the machine is the
// RFC 3161 timestamp request a real Authenticode signature needs: Packman cannot sign without
// one (NativeCodeSigner always timestamps and refuses an empty timestamp server), so the tests
// that make a real signature return early when http://timestamp.digicert.com cannot be reached
// - see RealTimestampServer. Windows only.

// ── The fake tenant ───────────────────────────────────────────────────────────

/// <summary>
/// Answers every Graph and Azure Storage call a publish or a content update makes, records
/// the bodies, and can fail or stall a single stage. One instance serves one test.
/// </summary>
internal sealed class ScriptedIntune : HttpMessageHandler
{
    public const string AppId = "pipeline-app";
    public const string FileId = "pipeline-file";
    public const string BlobHost = "fake.blob.core.windows.net";

    public sealed record Call(string Method, string Path, string Query, string Body);

    private readonly List<Call> _calls = [];
    private readonly HashSet<string> _committedFiles = [];
    private int _contentVersions;
    private int _groups;
    private int _sasVersion = 1;
    private bool _renewed;
    private long _blobBytes;
    private int _forbiddenBlocksLeft;

    public IReadOnlyList<Call> Calls { get { lock (_calls) return [.. _calls]; } }

    /// <summary>Total payload bytes the fake blob endpoint received across every block PUT.</summary>
    public long BlobBytesReceived { get { lock (_calls) return _blobBytes; } }

    public string LastContentVersionId { get { lock (_calls) return $"cv-{_contentVersions}"; } }

    // Per-stage failures; null means the stage succeeds.
    public HttpStatusCode? CreateAppFailure { get; set; }
    public HttpStatusCode? ContentVersionFailure { get; set; }
    public HttpStatusCode? FileEntryFailure { get; set; }
    public HttpStatusCode? CommitFileFailure { get; set; }
    public HttpStatusCode? CommitAppFailure { get; set; }
    public HttpStatusCode? SupersedenceFailure { get; set; }
    public HttpStatusCode? AssignmentFailure { get; set; }
    public HttpStatusCode? CreateGroupFailure { get; set; }
    public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;

    /// <summary>uploadState reported for the content file before it is committed.</summary>
    public string PendingFileState { get; set; } = "azureStorageUriRequestSuccess";

    /// <summary>Answer the Azure Storage URI request with no URI at all.</summary>
    public bool OmitSasUri { get; set; }

    /// <summary>Hands out a first SAS URI that expires inside the renewal margin.</summary>
    public bool FirstSasExpiresImmediately { get; set; }

    /// <summary>How many block PUTs Azure Storage rejects with 403 before accepting them.</summary>
    public int ForbiddenBlocks { get => _forbiddenBlocksLeft; set => _forbiddenBlocksLeft = value; }

    /// <summary>Status every block PUT gets; 201 unless a test wants a hard rejection.</summary>
    public HttpStatusCode BlockStatus { get; set; } = HttpStatusCode.Created;

    /// <summary>Entra group display name to id. A name that is absent is reported as not found.</summary>
    public Dictionary<string, string> ExistingGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int SasRenewals { get; private set; }

    /// <summary>Called for every request before it is answered, so a test can cancel mid-flight.</summary>
    public Action<Call>? OnCall { get; set; }

    public IEnumerable<Call> Where(string method, string pathSuffix) =>
        Calls.Where(c => c.Method == method && c.Path.EndsWith(pathSuffix, StringComparison.Ordinal));

    public IReadOnlyList<Call> Deletes => Calls.Where(c => c.Method == "DELETE").ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var isBlob = uri.Host == BlobHost;
        // Block PUTs carry raw payload bytes; only their length is interesting.
        var body = isBlob || request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var call = new Call(request.Method.Method, uri.AbsolutePath, uri.Query, body);
        lock (_calls) _calls.Add(call);
        OnCall?.Invoke(call);

        if (isBlob)
        {
            if (uri.Query.Contains("comp=blocklist", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created);
            var bytes = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct)).Length;
            if (_forbiddenBlocksLeft > 0)
            {
                _forbiddenBlocksLeft--;
                return Json(HttpStatusCode.Forbidden);
            }
            if (BlockStatus != HttpStatusCode.Created) return Json(BlockStatus);
            lock (_calls) _blobBytes += bytes;
            return Json(HttpStatusCode.Created);
        }

        ct.ThrowIfCancellationRequested();
        var path = call.Path;
        var method = call.Method;

        if (method == "DELETE")
            return Json(DeleteStatus, DeleteStatus == HttpStatusCode.NoContent ? "" : Error("Forbidden"));

        if (method == "POST")
        {
            if (path.EndsWith("/mobileApps", StringComparison.Ordinal))
                return Fail(CreateAppFailure) ?? Json(HttpStatusCode.Created, $"{{\"id\":\"{AppId}\"}}");
            if (path.EndsWith("/contentVersions", StringComparison.Ordinal))
                return Fail(ContentVersionFailure) ?? Json(HttpStatusCode.Created, $"{{\"id\":\"cv-{++_contentVersions}\"}}");
            if (path.EndsWith("/files", StringComparison.Ordinal))
                return Fail(FileEntryFailure) ?? Json(HttpStatusCode.Created, $"{{\"id\":\"{FileId}\"}}");
            if (path.EndsWith("/renewUpload", StringComparison.Ordinal))
            {
                SasRenewals++;
                _sasVersion++;
                _renewed = true;
                return Json(HttpStatusCode.OK);
            }
            if (path.EndsWith("/commit", StringComparison.Ordinal))
            {
                if (Fail(CommitFileFailure) is { } failure) return failure;
                lock (_calls) _committedFiles.Add(path[..^"/commit".Length]);
                return Json(HttpStatusCode.OK);
            }
            if (path.EndsWith("/updateRelationships", StringComparison.Ordinal))
                return Fail(SupersedenceFailure) ?? Json(HttpStatusCode.NoContent, "");
            if (path.EndsWith("/assignments", StringComparison.Ordinal))
                return Fail(AssignmentFailure) ?? Json(HttpStatusCode.Created, "{\"id\":\"assignment-1\"}");
            if (path.EndsWith("/groups", StringComparison.Ordinal))
                return Fail(CreateGroupFailure) ?? Json(HttpStatusCode.Created, $"{{\"id\":\"new-group-{++_groups}\"}}");
        }

        if (method == "PATCH" && path.Contains("/mobileApps/", StringComparison.Ordinal))
            return Fail(CommitAppFailure) ?? Json(HttpStatusCode.NoContent, "");

        if (method == "GET" && path.Contains("/files/", StringComparison.Ordinal))
        {
            bool committed;
            lock (_calls) committed = _committedFiles.Contains(path);
            if (committed) return Json(HttpStatusCode.OK, "{\"uploadState\":\"commitFileSuccess\"}");
            var state = _renewed ? "azureStorageUriRenewalSuccess" : PendingFileState;
            var sas = OmitSasUri ? "" : $",\"azureStorageUri\":\"{CurrentSas()}\"";
            return Json(HttpStatusCode.OK, $"{{\"uploadState\":\"{state}\"{sas}}}");
        }

        if (method == "GET" && path.EndsWith("/groups", StringComparison.Ordinal))
        {
            var name = DisplayNameFromFilter(call.Query);
            return Json(HttpStatusCode.OK, ExistingGroups.TryGetValue(name, out var id)
                ? $"{{\"value\":[{{\"id\":\"{id}\"}}]}}"
                : "{\"value\":[]}");
        }

        if (method == "GET" && path.Contains("/mobileApps/", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK,
                $"{{\"id\":\"{AppId}\",\"publishingState\":\"published\",\"committedContentVersion\":\"cv-{_contentVersions}\"}}");

        throw new InvalidOperationException($"Unexpected request: {method} {uri}");
    }

    private string CurrentSas()
    {
        var expiry = FirstSasExpiresImmediately && _sasVersion == 1
            ? DateTimeOffset.UtcNow.AddSeconds(20)
            : DateTimeOffset.UtcNow.AddHours(1);
        var se = Uri.EscapeDataString(expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        return $"https://{BlobHost}/container/blob?sig=v{_sasVersion}&se={se}";
    }

    // "$filter=displayName eq 'Name'", URL-escaped by the caller.
    private static string DisplayNameFromFilter(string query)
    {
        var decoded = Uri.UnescapeDataString(query);
        var open = decoded.IndexOf('\'');
        var close = decoded.LastIndexOf('\'');
        return open >= 0 && close > open ? decoded[(open + 1)..close].Replace("''", "'") : "";
    }

    private static HttpResponseMessage? Fail(HttpStatusCode? status) =>
        status is { } value ? Json(value, Error(value.ToString())) : null;

    private static string Error(string code) => $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{code} from the scripted tenant\"}}}}";

    private static HttpResponseMessage Json(HttpStatusCode status, string body = "{}") => Fakes.Json(status, body);
}

/// <summary>Shared setup for the publish tests: a temp workspace, a scripted tenant and local logs.</summary>
public abstract class PublishPipelineFixture : GraphStaticsFixture
{
    protected const string Tenant = "11111111-1111-1111-1111-111111111111";
    protected const string AppName = "PackmanPipelineTest";

    private readonly string _previousLogFolder = UploadLogger.ConfiguredFolder;

    protected string Root { get; } = Directory.CreateTempSubdirectory("packman-pipeline-").FullName;
    internal ScriptedIntune Intune { get; } = new();

    protected PublishPipelineFixture()
    {
        UseHandler(Intune);
        // Keep upload logs inside the temp workspace instead of %LocalAppData%.
        UploadLogger.ConfiguredFolder = Path.Combine(Root, "logs");
        ActivityLog.Shared.Clear();
    }

    /// <summary>A package whose Application folder is the real bundled PSADT v4 template.</summary>
    protected string TemplatePackage(string name)
    {
        var package = Path.Combine(Root, name);
        DirectoryCopy.Copy(BundledTools.PsadtTemplate, PsadtLayout.ApplicationFolder(package));
        Directory.CreateDirectory(PsadtLayout.FilesFolder(package));
        Directory.CreateDirectory(PsadtLayout.IntuneFolder(package));
        File.WriteAllText(Path.Combine(PsadtLayout.FilesFolder(package), "dummy-setup.exe"), "not a real installer");
        return package;
    }

    protected static ApplicationInfo Info(string architecture = "x64") => new()
    {
        Manufacturer = "Contoso",
        Name = AppName,
        Version = "1.0",
        DisplayName = $"Contoso {AppName} 1.0",
        Architecture = architecture,
    };

    protected static List<DetectionRule> Rules() =>
    [
        new() { Type = DetectionRuleType.File, Path = @"C:\Program Files\Contoso", FileOrFolderName = "app.exe" },
    ];

    internal IntuneUploadService Service(string converterPath, IFileSigner? signer = null) =>
        new(() => Task.FromResult("test-token"), Tenant, signer, converterPath);

    /// <summary>The body of the first call matching a method and path suffix, parsed as JSON.</summary>
    internal JsonElement Body(string method, string pathSuffix)
    {
        var call = Intune.Where(method, pathSuffix).FirstOrDefault()
            ?? throw new InvalidOperationException($"No {method} ...{pathSuffix} was sent. Calls: " +
                string.Join(", ", Intune.Calls.Select(c => $"{c.Method} {c.Path}")));
        return JsonSerializer.Deserialize<JsonElement>(call.Body);
    }

    protected static PackageMarker.Link? Link(string packagePath) => PackageMarker.Read(packagePath);

    public override void Dispose()
    {
        base.Dispose();
        UploadLogger.ConfiguredFolder = _previousLogFolder;
        try
        {
            DirectoryCopy.ClearReadOnlyRecursive(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray handle on a 22 MB template copy must not fail the test that passed.
        }
    }
}

// ── 1. The real .intunewin ────────────────────────────────────────────────────

/// <summary>
/// The Microsoft Win32 Content Prep Tool Packman downloads, run the way the service runs it,
/// on a package built from the bundled PSADT v4 template. Only packs a folder into a file.
/// The tool is not in the repository: CI fetches the pinned build before the tests, and a
/// machine that has not published with Packman yet skips the tests that need it.
/// </summary>
public sealed class PublishPipelineConverterTests : PublishPipelineFixture
{
    private static readonly string Converter = BundledTools.IntuneWinAppUtil;

    /// <summary>
    /// Only the pinned build counts: with anything else at that path the service would go and
    /// download the tool, and these tests do not touch the network.
    /// </summary>
    private static bool Available => ContentPrepTool.IsVerified(Converter);

    [Fact]
    public void InCiThePinnedConverterIsThereSoTheBuildTestsRun()
    {
        // Guards the skip in the tests below: CI downloads the tool first, so a missing or
        // different file there means the real converter was not exercised at all.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        Assert.True(Available, $"The pinned IntuneWinAppUtil.exe {ContentPrepTool.Version} is not at {Converter}; the build tests cannot run.");
    }

    [Fact]
    public async Task TheRealConverterProducesAnIntuneWinWithEverythingTheUploadNeeds()
    {
        if (!Available) return;
        var package = TemplatePackage("Contoso_Real");

        var appId = await UploadAsync(package);

        Assert.Equal(ScriptedIntune.AppId, appId);
        var built = Assert.Single(Directory.GetFiles(PsadtLayout.IntuneFolder(package), "*.intunewin"));
        Assert.Equal("Invoke-AppDeployToolkit.intunewin", Path.GetFileName(built));

        using var archive = ZipFile.OpenRead(built);
        var payload = archive.Entries.Single(e => e.FullName == "IntuneWinPackage/Contents/IntunePackage.intunewin");
        var detectionEntry = archive.Entries.Single(e => e.Name.Equals("detection.xml", StringComparison.OrdinalIgnoreCase));

        XElement detection;
        using (var stream = detectionEntry.Open()) detection = XDocument.Load(stream).Root!;
        Assert.Equal("ApplicationInfo", detection.Name.LocalName);
        Assert.StartsWith("1.8.7", detection.Attribute("ToolVersion")?.Value);
        Assert.Equal(PsadtLayout.SetupFileName, Value(detection, "Name"));
        Assert.EndsWith(PsadtLayout.SetupFileName, Value(detection, "SetupFile"));
        Assert.Equal("IntunePackage.intunewin", Value(detection, "FileName"));
        var unencryptedSize = long.Parse(Value(detection, "UnencryptedContentSize"));
        Assert.True(unencryptedSize > 0, "detection.xml reported no unencrypted content size");

        // Every field CommitFileAsync sends as fileEncryptionInfo, with the sizes Graph is told.
        var encryption = detection.Elements().Single(e => e.Name.LocalName == "EncryptionInfo");
        foreach (var (field, bytes) in new (string, int)[]
                 { ("EncryptionKey", 32), ("MacKey", 32), ("InitializationVector", 16), ("Mac", 32), ("FileDigest", 32) })
            Assert.Equal(bytes, Convert.FromBase64String(Value(encryption, field)).Length);
        Assert.Equal("ProfileVersion1", Value(encryption, "ProfileIdentifier"));
        Assert.Equal("SHA256", Value(encryption, "FileDigestAlgorithm"));

        // What the service read out of the archive and sent on.
        var app = Body("POST", "/mobileApps");
        Assert.Equal("IntunePackage.intunewin", app.GetProperty("fileName").GetString());
        Assert.Equal(PsadtLayout.SetupFileName, app.GetProperty("setupFilePath").GetString());

        var file = Body("POST", "/files");
        Assert.Equal("IntunePackage.intunewin", file.GetProperty("name").GetString());
        Assert.Equal(unencryptedSize, file.GetProperty("size").GetInt64());
        Assert.Equal(payload.Length, file.GetProperty("sizeEncrypted").GetInt64());

        var committed = Body("POST", "/commit").GetProperty("fileEncryptionInfo");
        Assert.Equal(Value(encryption, "EncryptionKey"), committed.GetProperty("encryptionKey").GetString());
        Assert.Equal(Value(encryption, "MacKey"), committed.GetProperty("macKey").GetString());
        Assert.Equal(Value(encryption, "InitializationVector"), committed.GetProperty("initializationVector").GetString());
        Assert.Equal(Value(encryption, "Mac"), committed.GetProperty("mac").GetString());
        Assert.Equal(Value(encryption, "FileDigest"), committed.GetProperty("fileDigest").GetString());
        Assert.Equal("ProfileVersion1", committed.GetProperty("profileIdentifier").GetString());
        Assert.Equal("SHA256", committed.GetProperty("fileDigestAlgorithm").GetString());

        // The whole encrypted payload was streamed to Azure, in more than one block.
        Assert.Equal(payload.Length, Intune.BlobBytesReceived);
        Assert.True(Intune.Where("PUT", "/blob").Count() > 1, "a 10 MB payload should take more than one block");

        var link = Link(package);
        Assert.NotNull(link);
        Assert.Equal(ScriptedIntune.AppId, link!.AppId);
        Assert.Equal(Tenant, link.TenantId);
        Assert.Equal("Invoke-AppDeployToolkit.intunewin", link.IntunewinFileName);
        Assert.Equal(unencryptedSize, link.UnencryptedSize);
    }

    [Fact]
    public async Task AStaleIntuneWinFromAnEarlierRunIsNeverUploadedAgain()
    {
        if (!Available) return;
        var package = TemplatePackage("Contoso_Stale");
        var stale = Path.Combine(PsadtLayout.IntuneFolder(package), "AAA-previous-run.intunewin");
        File.WriteAllText(stale, "not a real archive");

        await UploadAsync(package);

        Assert.False(File.Exists(stale));
        var built = Assert.Single(Directory.GetFiles(PsadtLayout.IntuneFolder(package), "*.intunewin"));
        Assert.Equal("Invoke-AppDeployToolkit.intunewin", Path.GetFileName(built));
    }

    [Fact]
    public async Task AMissingConverterStopsBeforeAnythingExistsInIntune()
    {
        var package = Fakes.Package(Path.Combine(Root, "Contoso_NoTool"));
        var service = Service(Path.Combine(Root, "nowhere", "IntuneWinAppUtil.exe"));

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => Upload(service, package));

        Assert.Contains("Set IntuneWinAppUtil Path under Settings", ex.Message);
        Assert.Empty(Intune.Where("POST", "/mobileApps"));
        Assert.Null(Link(package));
        Assert.Null(service.RollbackSucceeded);
    }

    [Fact]
    public async Task AnIncompleteTemplateIsRefusedByThePreflightBeforeTheConverterRuns()
    {
        if (!Available) return;
        var package = TemplatePackage("Contoso_Incomplete");
        File.Delete(Path.Combine(PsadtLayout.ApplicationFolder(package), PsadtLayout.SetupFileName));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(Service(Converter), package));

        Assert.Contains(PsadtLayout.SetupFileName, ex.Message);
        Assert.Empty(Directory.GetFiles(PsadtLayout.IntuneFolder(package), "*.intunewin"));
        Assert.Empty(Intune.Where("POST", "/mobileApps"));
    }

    // Settings lets a packager point at an IntuneWinAppUtil.exe of their own, so the
    // contract Packman expects from it has to fail loudly rather than publish nothing.

    [Fact]
    public async Task AConverterThatWritesNoIntuneWinIsReportedInsteadOfPublishingNothing()
    {
        var package = Fakes.Package(Path.Combine(Root, "Contoso_SilentTool"));
        var tool = StandInConverter("Contoso_SilentTool", "@exit 0\r\n");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => Upload(Service(tool), package));

        Assert.Contains("produced no .intunewin file", ex.Message);
        Assert.Empty(Intune.Where("POST", "/mobileApps"));
    }

    [Fact]
    public async Task AConverterThatFailsIsReportedWithItsExitCodeAndWhatItSaid()
    {
        var package = Fakes.Package(Path.Combine(Root, "Contoso_FailingTool"));
        var tool = StandInConverter("Contoso_FailingTool", "@echo source folder is not valid 1>&2\r\n@exit /b 3\r\n");

        var ex = await Assert.ThrowsAsync<Exception>(() => Upload(Service(tool), package));

        Assert.Contains("IntuneWinAppUtil failed with exit code 3", ex.Message);
        Assert.Contains("source folder is not valid", ex.Message);
        Assert.Empty(Intune.Where("POST", "/mobileApps"));
    }

    [Fact]
    public async Task AnArchiveWithoutDetectionXmlIsReportedWithWhatItDidContain()
    {
        var package = Fakes.Package(Path.Combine(Root, "Contoso_NoDetection"));
        var tool = StandInConverter("Contoso_NoDetection", archive =>
            archive.CreateEntry("IntuneWinPackage/Contents/IntunePackage.intunewin"));

        var ex = await Assert.ThrowsAsync<Exception>(() => Upload(Service(tool), package));

        Assert.Contains("detection.xml not found", ex.Message);
        Assert.Contains("IntunePackage.intunewin", ex.Message);
    }

    [Fact]
    public async Task AnArchiveWithoutAnEncryptionKeyIsRefusedBeforeTheAppIsCreated()
    {
        var package = Fakes.Package(Path.Combine(Root, "Contoso_NoKey"));
        var tool = StandInConverter("Contoso_NoKey", archive =>
        {
            using (var writer = new StreamWriter(archive.CreateEntry("IntuneWinPackage/Metadata/Detection.xml").Open()))
                writer.Write("<ApplicationInfo><FileName>IntunePackage.intunewin</FileName>" +
                             "<EncryptionInfo><MacKey>bWFj</MacKey></EncryptionInfo></ApplicationInfo>");
            using var payload = archive.CreateEntry("IntuneWinPackage/Contents/IntunePackage.intunewin").Open();
            payload.Write(new byte[2048]);
        });

        var ex = await Assert.ThrowsAsync<Exception>(() => Upload(Service(tool), package));

        Assert.Contains("EncryptionKey is missing from detection.xml", ex.Message);
        Assert.Empty(Intune.Where("POST", "/mobileApps"));
    }

    /// <summary>A .cmd standing in for the converter, running <paramref name="script"/> verbatim.</summary>
    private string StandInConverter(string name, string script)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Root, ".tools", name)).FullName;
        var tool = Path.Combine(folder, "IntuneWinAppUtil.cmd");
        File.WriteAllText(tool, script);
        return tool;
    }

    /// <summary>A stand-in converter that drops an archive built by <paramref name="fill"/> into -o.</summary>
    private string StandInConverter(string name, Action<ZipArchive> fill)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Root, ".tools", name)).FullName;
        using (var archive = ZipFile.Open(Path.Combine(folder, "prepared.intunewin"), ZipArchiveMode.Create))
            fill(archive);
        // Arguments arrive as -c <source> -s <setup> -o <output> -q.
        return StandInConverter(name,
            "@copy /y \"%~dp0prepared.intunewin\" \"%~6\\Invoke-AppDeployToolkit.intunewin\" >nul\r\n");
    }

    private Task<string> UploadAsync(string package) => Upload(Service(Converter), package);

    private static Task<string> Upload(IntuneUploadService service, string package) =>
        Task.Run(() => service.UploadWin32ApplicationAsync(Info(), package, Rules(),
            AppSettings.IntuneDefaultsConfig.DefaultInstallCommand,
            AppSettings.IntuneDefaultsConfig.DefaultUninstallCommand, "A real build", "system"));

    private static string Value(XElement parent, string localName) =>
        parent.Elements().First(e => e.Name.LocalName == localName).Value.Trim();
}

// ── 2. Code signing with a temporary certificate ──────────────────────────────

/// <summary>
/// The public RFC 3161 server the signing tests timestamp against. Packman always timestamps
/// and refuses an empty timestamp server, so a real Authenticode signature cannot be made
/// without reaching this host; the probe lets a build agent with no internet return early from
/// those tests instead of failing them. Each test class probes once, into a static field.
/// </summary>
internal static class RealTimestampServer
{
    public const string Url = "http://timestamp.digicert.com";

    private const string Host = "timestamp.digicert.com";

    /// <summary>True when a TCP connection to the timestamp host succeeds; probed once per run.</summary>
    public static bool Reachable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();
            // Short: a machine with no route must not hold the suite up.
            return client.ConnectAsync(Host, 80).Wait(TimeSpan.FromSeconds(5)) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Self-signed code-signing certificates that live only in CurrentUser\My for the length of
/// one test class. Nothing is ever added to Root, TrustedPublisher or a LocalMachine store,
/// so a signature made here is correctly reported as untrusted.
/// </summary>
internal sealed class TempSigningCertificates : IDisposable
{
    /// <summary>Every subject starts with this; teardown sweeps the store for it.</summary>
    public const string SubjectPrefix = "Packman Test Signing";

    public const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private readonly List<X509Certificate2> _installed = [];

    /// <summary>A usable signing certificate: code-signing EKU, private key, valid for a day.</summary>
    public X509Certificate2 Valid { get; }

    /// <summary>Same, but it expired yesterday.</summary>
    public X509Certificate2 Expired { get; }

    /// <summary>Same, but its validity only starts tomorrow.</summary>
    public X509Certificate2 NotYetValid { get; }

    /// <summary>The public half only: in the store, but with no key to sign with.</summary>
    public X509Certificate2 WithoutPrivateKey { get; }

    /// <summary>A server-authentication certificate: a key, but not for signing code.</summary>
    public X509Certificate2 WrongPurpose { get; }

    /// <summary>No Enhanced Key Usage extension at all, which allows every usage.</summary>
    public X509Certificate2 WithoutEnhancedKeyUsage { get; }

    public TempSigningCertificates()
    {
        // A comma in a common name would start a new X.500 relative name, so none is used.
        var now = DateTimeOffset.UtcNow;
        Valid = Install(Create($"{SubjectPrefix} (temporary)", CodeSigningOid, now.AddDays(-1), now.AddDays(1)));
        Expired = Install(Create($"{SubjectPrefix} (temporary - expired)", CodeSigningOid, now.AddDays(-31), now.AddDays(-1)));
        NotYetValid = Install(Create($"{SubjectPrefix} (temporary - not yet valid)", CodeSigningOid, now.AddDays(1), now.AddDays(31)));
        WrongPurpose = Install(Create($"{SubjectPrefix} (temporary - server auth)", ServerAuthOid, now.AddDays(-1), now.AddDays(1)));
        WithoutEnhancedKeyUsage = Install(Create($"{SubjectPrefix} (temporary - no eku)", null, now.AddDays(-1), now.AddDays(1)));

        using var keyed = Create($"{SubjectPrefix} (temporary - no private key)", CodeSigningOid, now.AddDays(-1), now.AddDays(1));
        WithoutPrivateKey = Install(X509CertificateLoader.LoadCertificate(keyed.Export(X509ContentType.Cert)));
        // Only the public half goes into the store, so teardown would never find this key again.
        DeleteKeyContainer(keyed);
    }

    /// <param name="ekuOid">The single Enhanced Key Usage to assert, or null for no EKU extension.</param>
    private static X509Certificate2 Create(string commonName, string? ekuOid, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        if (ekuOid != null)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ekuOid) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var ephemeral = request.CreateSelfSigned(notBefore, notAfter);

        // The signer reads the key from the store, so it has to be a persisted key.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, password), password,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    private static void DeleteKeyContainer(X509Certificate2 certificate)
    {
        try { (certificate.GetRSAPrivateKey() as RSACng)?.Key.Delete(); } catch (CryptographicException) { }
    }

    private X509Certificate2 Install(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        _installed.Add(certificate);
        return certificate;
    }

    /// <summary>Certificates left in CurrentUser\My whose subject carries <see cref="SubjectPrefix"/>.</summary>
    public static List<string> LeftInStore()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Where(c => c.Subject.Contains(SubjectPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Subject)
            .ToList();
    }

    /// <summary>Removes every certificate this class could have left behind, and its key material.</summary>
    public void Dispose()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var found in store.Certificates
                         .Where(c => c.Subject.Contains(SubjectPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { store.Remove(found); } catch (CryptographicException) { }
                // Removing the certificate leaves the persisted private key in the user's
                // key storage; delete the container too.
                DeleteKeyContainer(found);
                found.Dispose();
            }
        }
        catch (CryptographicException) { }

        foreach (var certificate in _installed) certificate.Dispose();
        _installed.Clear();
    }
}

/// <summary>
/// What Packman's Authenticode signer does to a PSADT package, with a throwaway certificate.
/// A signature made with an untrusted self-signed root is expected to verify as
/// UnknownError / "terminated in a root certificate which is not trusted"; that is the
/// certificate's trust, not the signature. The tests that make a real signature need the
/// timestamp server and return early without it; which certificate is refused, and which files
/// the stage picks, is decided before any request and is checked unconditionally.
/// </summary>
public sealed class PublishPipelineSigningTests : IDisposable
{
    /// <summary>Nothing listens here, so the RFC 3161 request fails without leaving the machine.</summary>
    private const string UnreachableTimestampServer = "http://127.0.0.1:9/timestamp";

    /// <summary>Probed once for this class; the tests that need a real timestamp return early without it.</summary>
    private static readonly bool CanTimestamp = RealTimestampServer.Reachable;

    private readonly TempSigningCertificates _certificates = new();
    private readonly string _root = Directory.CreateTempSubdirectory("packman-signing-").FullName;

    public void Dispose()
    {
        // try/finally through the fixture: the store is cleaned even when a test failed.
        _certificates.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A package whose deployment script is small enough to diff, plus the preflight files.</summary>
    private string Package(string name)
    {
        var package = Fakes.Package(Path.Combine(_root, name));
        File.WriteAllText(PsadtLayout.ScriptPath(package),
            "$adtSession = @{\r\n    AppName = 'Signing test'\r\n}\r\nWrite-Output $adtSession.AppName\r\n");
        return package;
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>, creating the folders, and returns the path.</summary>
    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ── Which files the signing stage picks ───────────────────────────────────

    [Fact]
    public void FilesToSignNamesTheDeploymentScriptFirstAndThenEveryUnsignedPowerShellFileBesideIt()
    {
        var package = Package("Selection");
        var application = PsadtLayout.ApplicationFolder(package);
        var script = PsadtLayout.ScriptPath(package);
        var helper = Write(Path.Combine(application, "Helpers.psm1"), "function Get-Thing { }\r\n");
        var data = Write(Path.Combine(application, "PSAppDeployToolkit.Extensions", "Extensions.psd1"), "@{}\r\n");
        // Neither of these is PowerShell, so neither can carry a signature block.
        Write(Path.Combine(application, "Notes.txt"), "read me");
        Write(Path.Combine(application, "tool.exe"), "MZ");

        var files = PackageSigningService.FilesToSign(script);

        Assert.Equal(script, files[0]);
        Assert.Contains(helper, files);
        Assert.Contains(data, files);
        // The toolkit manifest Fakes.Package writes carries no signature either.
        Assert.Contains(Path.Combine(application, PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psd1"), files);
        Assert.All(files, f => Assert.Contains(Path.GetExtension(f), new[] { ".ps1", ".psm1", ".psd1" }));
        Assert.Equal(files.Distinct(StringComparer.OrdinalIgnoreCase).Count(), files.Count);
    }

    [Fact]
    public void FilesToSignLeavesAPowerShellFileThatAlreadyCarriesASignatureBlockAlone()
    {
        var package = Package("AlreadySigned");
        var application = PsadtLayout.ApplicationFolder(package);
        var alreadySigned = Write(Path.Combine(application, "Vendor", "Vendor.psm1"),
            $"function Get-Vendor {{ }}\r\n{PsadtScript.SignatureBegin}\r\n# MIIabc\r\n{PsadtScript.SignatureEnd}\r\n");
        var unsigned = Write(Path.Combine(application, "Vendor", "Ours.psm1"), "function Get-Ours { }\r\n");

        var files = PackageSigningService.FilesToSign(PsadtLayout.ScriptPath(package));

        Assert.DoesNotContain(alreadySigned, files);
        Assert.Contains(unsigned, files);
    }

    [Fact]
    public void FilesToSignNeverReachesIntoTheVendorPayloadUnderFiles()
    {
        var package = Package("Payload");
        var payload = Write(Path.Combine(PsadtLayout.FilesFolder(package), "vendor-setup.ps1"), "Write-Output 'vendor'\r\n");
        var nested = Write(Path.Combine(PsadtLayout.FilesFolder(package), "Modules", "Vendor.psd1"), "@{}\r\n");

        var files = PackageSigningService.FilesToSign(PsadtLayout.ScriptPath(package));

        Assert.DoesNotContain(payload, files);
        Assert.DoesNotContain(nested, files);
        Assert.DoesNotContain(files, f => f.Contains($"{Path.DirectorySeparatorChar}{PsadtLayout.FilesFolderName}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    // ── Signing with a usable certificate ─────────────────────────────────────

    [Fact]
    public async Task TheSignerSignsTheDeploymentScriptAndTheUnsignedToolkitManifestAndLeavesTheRestAlone()
    {
        if (!CanTimestamp) return;   // No route to the timestamp server: see RealTimestampServer.
        var package = Package("Signed");
        var launcher = Path.Combine(PsadtLayout.ApplicationFolder(package), PsadtLayout.SetupFileName);
        var manifest = Path.Combine(PsadtLayout.ApplicationFolder(package), PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psd1");
        var launcherBefore = File.ReadAllBytes(launcher);

        var signed = await PackageSigningService.SignAsync(package, Signer());

        Assert.Equal(PsadtLayout.ScriptPath(package), signed);
        var script = PsadtScript.Load(signed!);
        Assert.True(script.HasSignatureBlock, "the deployment script has no signature block");
        Assert.Equal(_certificates.Valid.Thumbprint, SignerThumbprint(signed!));
        Assert.Contains("$adtSession", script.Content);

        // The deployment imports every PowerShell file beside it, so an unsigned one would fail
        // the import under AllSigned: this package's toolkit manifest carried no signature.
        Assert.Contains(PsadtScript.SignatureBegin, File.ReadAllText(manifest));
        Assert.Equal(_certificates.Valid.Thumbprint, SignerThumbprint(manifest));
        Assert.StartsWith("@{}", File.ReadAllText(manifest));
        // Nothing that is not PowerShell is touched, signed or unsigned.
        Assert.Equal(launcherBefore, File.ReadAllBytes(launcher));
    }

    [Fact]
    public async Task GetAuthenticodeSignatureNamesOurSignerAndReportsTheUntrustedRoot()
    {
        if (!CanTimestamp) return;   // No route to the timestamp server: see RealTimestampServer.
        var package = Package("Verified");

        await PackageSigningService.SignAsync(package, Signer());

        var (status, message, subject, thumbprint) = Verify(PsadtLayout.ScriptPath(package));
        Assert.Equal(_certificates.Valid.Thumbprint, thumbprint);
        Assert.Equal(_certificates.Valid.Subject, subject);
        // The signature itself is intact; only the self-signed root is untrusted, which
        // Windows reports as UnknownError (NotTrusted on some builds).
        Assert.True(status is "UnknownError" or "NotTrusted", $"unexpected status '{status}': {message}");
        Assert.Contains("not trusted", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReSigningAnAlreadySignedScriptReplacesTheBlockInsteadOfAppendingOne()
    {
        if (!CanTimestamp) return;   // No route to the timestamp server: see RealTimestampServer.
        var package = Package("Resigned");
        var scriptPath = PsadtLayout.ScriptPath(package);

        await PackageSigningService.SignAsync(package, Signer());
        var first = File.ReadAllText(scriptPath);
        await PackageSigningService.SignAsync(package, Signer());
        var second = File.ReadAllText(scriptPath);

        Assert.Equal(1, Occurrences(second, PsadtScript.SignatureBegin));
        Assert.Equal(1, Occurrences(second, PsadtScript.SignatureEnd));
        Assert.Equal(_certificates.Valid.Thumbprint, SignerThumbprint(scriptPath));
        // The payload is unchanged, so both runs sign the same script body.
        Assert.Equal(first[..first.IndexOf(PsadtScript.SignatureBegin, StringComparison.Ordinal)],
                     second[..second.IndexOf(PsadtScript.SignatureBegin, StringComparison.Ordinal)]);
        // The signature itself is intact; only the self-signed root is untrusted.
        Assert.True(Verify(scriptPath).Status is "UnknownError" or "NotTrusted");
    }

    // ── Certificates the signer refuses ───────────────────────────────────────

    [Fact]
    public void AUsableCodeSigningCertificateHasNoProblemAndIsOfferedAsAvailable()
    {
        var signer = new NativeCodeSigner(_certificates.Valid.Thumbprint, RealTimestampServer.Url);

        Assert.Null(signer.CertificateProblem());
        Assert.True(signer.IsCertificateAvailable());
    }

    [Fact]
    public async Task ACertificateWithNoEnhancedKeyUsageExtensionAtAllIsAccepted()
    {
        // No EKU extension means no restriction, so such a certificate may sign code.
        Assert.Empty(_certificates.WithoutEnhancedKeyUsage.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        var signer = new NativeCodeSigner(_certificates.WithoutEnhancedKeyUsage.Thumbprint, RealTimestampServer.Url);

        Assert.Null(signer.CertificateProblem());
        Assert.True(signer.IsCertificateAvailable());

        if (!CanTimestamp) return;   // No route to the timestamp server: see RealTimestampServer.
        var package = Package("NoEku");
        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(_certificates.WithoutEnhancedKeyUsage.Thumbprint, SignerThumbprint(PsadtLayout.ScriptPath(package)));
    }

    [Fact]
    public async Task NoThumbprintAtAllAsksForACertificateToBeChosenInSettings()
    {
        var package = Package("NoThumbprint");
        var signer = new NativeCodeSigner("");

        Assert.False(signer.IsCertificateAvailable());
        Assert.Contains("No signing certificate is selected", signer.CertificateProblem());

        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.Equal(signer.CertificateProblem(), result.ErrorMessage);
        AssertNothingWasWritten(package);
    }

    [Fact]
    public async Task AThumbprintThatIsInNoPersonalStoreIsReportedAndTheScriptIsLeftUnsigned()
    {
        var package = Package("WrongThumbprint");
        var thumbprint = new string('0', 40);
        var signer = new NativeCodeSigner(thumbprint);

        Assert.False(signer.IsCertificateAvailable());
        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.Contains($"The signing certificate {thumbprint} is not in the CurrentUser or LocalMachine Personal store", result.ErrorMessage);
        AssertNothingWasWritten(package);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PackageSigningService.SignAsync(package, signer));
        Assert.StartsWith("Code signing failed. Publishing stopped:", ex.Message);
        Assert.Contains("is not in the CurrentUser or LocalMachine Personal store", ex.Message);
    }

    [Fact]
    public async Task ACertificateWithoutAPrivateKeyIsReportedAsUnavailable()
    {
        var package = Package("NoPrivateKey");
        var signer = new NativeCodeSigner(_certificates.WithoutPrivateKey.Thumbprint, RealTimestampServer.Url);

        Assert.False(signer.IsCertificateAvailable());
        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.Contains("has no private key on this computer", result.ErrorMessage);
        Assert.Contains(TempSigningCertificates.SubjectPrefix, result.ErrorMessage);
        AssertNothingWasWritten(package);
    }

    [Fact]
    public async Task AnExpiredCertificateIsRefusedBeforeItIsEverUsedAndTheExpiryDateIsNamed()
    {
        var package = Package("Expired");
        var signer = new NativeCodeSigner(_certificates.Expired.Thumbprint, RealTimestampServer.Url);

        // Settings must not offer it, and signing must stop before anything is written.
        Assert.False(signer.IsCertificateAvailable());
        Assert.True(_certificates.Expired.NotAfter < DateTime.Now);

        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.Contains($"expired on {_certificates.Expired.NotAfter:yyyy-MM-dd}", result.ErrorMessage);
        // The certificate never reached SignerSignEx2, so there is no HRESULT to blame.
        Assert.Equal(0, result.HResult);
        AssertNothingWasWritten(package);
    }

    [Fact]
    public async Task ACertificateThatIsNotValidYetIsRefusedAndTheStartDateIsNamed()
    {
        var package = Package("NotYetValid");
        var signer = new NativeCodeSigner(_certificates.NotYetValid.Thumbprint, RealTimestampServer.Url);

        Assert.False(signer.IsCertificateAvailable());
        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.Contains($"is not valid until {_certificates.NotYetValid.NotBefore:yyyy-MM-dd}", result.ErrorMessage);
        Assert.Equal(0, result.HResult);
        AssertNothingWasWritten(package);
    }

    [Fact]
    public async Task ACertificateThatIsNotForCodeSigningIsRefused()
    {
        var package = Package("WrongPurpose");
        var signer = new NativeCodeSigner(_certificates.WrongPurpose.Thumbprint, RealTimestampServer.Url);

        // A TLS or Entra client certificate must never sign a package devices then reject.
        Assert.False(HasCodeSigningEku(_certificates.WrongPurpose), "the fixture certificate was built with the wrong EKU");
        Assert.False(signer.IsCertificateAvailable());

        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.Contains("is not issued for code signing", result.ErrorMessage);
        Assert.Equal(0, result.HResult);
        AssertNothingWasWritten(package);
    }

    // ── The timestamp server ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyTimestampServerIsRefusedWithoutContactingAnyHost(string? timestampServer)
    {
        var package = Package("NoTimestampServerConfigured");
        var before = File.ReadAllBytes(PsadtLayout.ScriptPath(package));
        var signer = new NativeCodeSigner(_certificates.Valid.Thumbprint, timestampServer);

        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        // Refused clearly, and never silently against a host the packager did not name.
        Assert.False(result.Success);
        Assert.StartsWith("No timestamp server is set.", result.ErrorMessage);
        Assert.Contains("Settings", result.ErrorMessage);
        // SignerSignEx2 makes the only network call there is, and it would have left an
        // HRESULT behind: a zero one proves the request was never attempted.
        Assert.Equal(0, result.HResult);
        Assert.Equal(before, File.ReadAllBytes(PsadtLayout.ScriptPath(package)));
        // The certificate itself is fine, so the timestamp server is the only complaint.
        Assert.Null(signer.CertificateProblem());
    }

    [Fact]
    public async Task AnUnreachableTimestampServerIsReportedWithTheUrlThatCouldNotBeReached()
    {
        var package = Package("NoTimestampFixed");
        var signer = new NativeCodeSigner(_certificates.Valid.Thumbprint, UnreachableTimestampServer);

        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.Contains($"Could not reach the timestamp server {UnreachableTimestampServer}", result.ErrorMessage);
        Assert.Contains("Settings", result.ErrorMessage);
        Assert.Contains("proxy", result.ErrorMessage);
        // A WinINet/WinHTTP failure inside FACILITY_WIN32, which is what names the network.
        Assert.Equal(unchecked((int)0x80070000), result.HResult & unchecked((int)0xFFFF0000));
        Assert.InRange(result.HResult & 0xFFFF, 12000, 12199);
        Assert.Contains($"0x{result.HResult:X8}", result.ErrorMessage);
    }

    [Fact]
    public async Task AnUnreachableTimestampServerStopsThePublishWithoutLeavingASignature()
    {
        var package = Package("NoTimestamp");
        var signer = new NativeCodeSigner(_certificates.Valid.Thumbprint, UnreachableTimestampServer);

        var result = await signer.SignFileAsync(PsadtLayout.ScriptPath(package));

        Assert.False(result.Success);
        Assert.NotEqual(0, result.HResult);
        // The important half: no signature is written, so a timestamp failure can never turn
        // into an untimestamped signature that outlives the certificate.
        Assert.False(PsadtScript.Load(PsadtLayout.ScriptPath(package)).HasSignatureBlock);
        Assert.Equal("NotSigned", Verify(PsadtLayout.ScriptPath(package)).Status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PackageSigningService.SignAsync(package, signer));
        Assert.StartsWith("Code signing failed. Publishing stopped:", ex.Message);
        Assert.Contains(UnreachableTimestampServer, ex.Message);
    }

    [Fact]
    public async Task AMissingDeploymentScriptIsReportedBeforeAnythingIsSigned()
    {
        var package = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(PsadtLayout.ApplicationFolder(package));

        await Assert.ThrowsAsync<FileNotFoundException>(() => PackageSigningService.SignAsync(package, Signer()));
    }

    [Fact]
    public async Task SigningDisabledSkipsTheStageWithoutTouchingTheScript()
    {
        var package = Package("Unsigned");
        var before = File.ReadAllText(PsadtLayout.ScriptPath(package));

        Assert.Null(await PackageSigningService.SignAsync(package, null));

        Assert.Equal(before, File.ReadAllText(PsadtLayout.ScriptPath(package)));
    }

    [Fact]
    public async Task ACancelledSigningStageNeverReturnsASuccessfulSignature()
    {
        var package = Package("Cancelled");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PackageSigningService.SignAsync(package, Signer(), cts.Token));

        Assert.False(PsadtScript.Load(PsadtLayout.ScriptPath(package)).HasSignatureBlock);
    }

    [Fact]
    public async Task EveryPowerShellFileTheRealTemplateImportsEndsUpSigned()
    {
        if (!CanTimestamp) return;   // No route to the timestamp server: see RealTimestampServer.
        var package = Path.Combine(_root, "RealTemplate");
        DirectoryCopy.Copy(BundledTools.PsadtTemplate, PsadtLayout.ApplicationFolder(package));
        var application = PsadtLayout.ApplicationFolder(package);
        // Invoke-AppDeployToolkit.ps1 imports every PSAppDeployToolkit.* folder beside it, and
        // the template ships that one unsigned, so an AllSigned device would fail the import.
        var extension = Path.Combine(application, "PSAppDeployToolkit.Extensions", "PSAppDeployToolkit.Extensions.psm1");
        Assert.True(File.Exists(extension), "the bundled template has no extension module any more");
        var toolkitSignature = File.ReadAllText(Path.Combine(application, PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psm1"));

        await PackageSigningService.SignAsync(package, Signer());

        var unsigned = Directory
            .GetFiles(application, "*.ps*1", SearchOption.AllDirectories)
            .Where(f => !File.ReadAllText(f).Contains(PsadtScript.SignatureBegin, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(unsigned);
        Assert.Equal(_certificates.Valid.Thumbprint, SignerThumbprint(extension));
        // PSADT signed its own module upstream, and that signature is kept, not replaced.
        Assert.Equal(toolkitSignature, File.ReadAllText(Path.Combine(application, PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psm1")));
    }

    [Fact]
    public void TheTemporaryCertificatesAreRemovedFromTheStoreOnTeardown()
    {
        // The fixture installed them, so they are visible here and gone after Dispose.
        Assert.NotEmpty(TempSigningCertificates.LeftInStore());

        using (var scoped = new TempSigningCertificates())
            Assert.NotEmpty(TempSigningCertificates.LeftInStore());

        // The nested fixture's Dispose swept every "Packman Test Signing" subject, including
        // this class's own, which proves the teardown this suite relies on.
        Assert.Empty(TempSigningCertificates.LeftInStore());
    }

    /// <summary>The usable certificate and a timestamp server, the only way Packman signs.</summary>
    private NativeCodeSigner Signer() => new(_certificates.Valid.Thumbprint, RealTimestampServer.Url);

    /// <summary>No PowerShell file in the package carries a signature: the refusal wrote nothing.</summary>
    private static void AssertNothingWasWritten(string package)
    {
        var written = Directory
            .GetFiles(PsadtLayout.ApplicationFolder(package), "*.ps*1", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(PsadtScript.SignatureBegin, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(written);
    }

    private static bool HasCodeSigningEku(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>())
            .Any(o => o.Value == TempSigningCertificates.CodeSigningOid);

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The thumbprint of the certificate inside the script's signature block.</summary>
    private static string SignerThumbprint(string scriptPath)
    {
        var text = File.ReadAllText(scriptPath);
        var begin = text.IndexOf(PsadtScript.SignatureBegin, StringComparison.Ordinal);
        var end = text.IndexOf(PsadtScript.SignatureEnd, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "the script carries no signature block");

        var base64 = new StringBuilder();
        foreach (var line in text[(begin + PsadtScript.SignatureBegin.Length)..end].Split('\n'))
            base64.Append(line.Trim().TrimStart('#').Trim());

        var cms = new SignedCms();
        cms.Decode(Convert.FromBase64String(base64.ToString()));
        return cms.SignerInfos[0].Certificate!.Thumbprint;
    }

    /// <summary>Get-AuthenticodeSignature through Windows PowerShell: status, message, signer.</summary>
    private static (string Status, string Message, string Subject, string Thumbprint) Verify(string path)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"$s = Get-AuthenticodeSignature -LiteralPath '{PowerShellLiteral.SingleQuoted(path)}'; " +
            "\"$($s.Status)`t$($s.StatusMessage)`t$($s.SignerCertificate.Subject)`t$($s.SignerCertificate.Thumbprint)\"");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "Get-AuthenticodeSignature did not finish");
        Assert.True(string.IsNullOrWhiteSpace(error), error);

        // Only the line break is trimmed: an unsigned file reports empty trailing fields.
        var fields = output.Trim('\r', '\n').Split('\t');
        Assert.Equal(4, fields.Length);
        return (fields[0], fields[1], fields[2], fields[3]);
    }
}

// ── 3. The Graph conversation ─────────────────────────────────────────────────

/// <summary>
/// Every publish and republish path against a scripted tenant: rollback at each stage, SAS
/// renewal, cancellation, assignments, supersedence and the packman.id link.
/// </summary>
public sealed class PublishPipelineGraphTests : PublishPipelineFixture
{
    /// <summary>Length of the payload entry in the stand-in converter's prepared .intunewin.</summary>
    private const long FakeEncryptedSize = 2048;

    /// <summary>Probed once for this class; the one test that signs for real returns early without it.</summary>
    private static readonly bool CanTimestamp = RealTimestampServer.Reachable;

    private readonly string _converter;

    public PublishPipelineGraphTests() => _converter = Fakes.IntuneWinAppUtil(Path.Combine(Root, ".tools"));

    private string NewPackage(string name = "Contoso_Graph") => Fakes.Package(Path.Combine(Root, name));

    private Task<string> Upload(
        IntuneUploadService service,
        string package,
        List<DetectionRule>? rules = null,
        IReadOnlyList<SupersedenceLink>? supersedence = null,
        AppSettings.GroupAssignmentConfig? groupAssignment = null,
        RequirementInfo? requirements = null,
        List<ReturnCodeInfo>? returnCodes = null,
        IEnumerable<AssignedGroup>? pickedGroups = null,
        string installContext = "system",
        string architecture = "x64",
        string? restartBehavior = null,
        int? maxRunTimeMinutes = null,
        CancellationToken ct = default) =>
        Task.Run(() => service.UploadWin32ApplicationAsync(Info(architecture), package, rules ?? Rules(),
            AppSettings.IntuneDefaultsConfig.DefaultInstallCommand,
            AppSettings.IntuneDefaultsConfig.DefaultUninstallCommand, "scripted tenant", installContext,
            iconPath: null, progress: null, supersedence: supersedence, groupAssignment: groupAssignment,
            requirements: requirements, returnCodes: returnCodes, privacyUrl: null, informationUrl: null,
            // Not passed to Task.Run: an already cancelled token must still reach the service.
            pickedGroups: pickedGroups, restartBehavior: restartBehavior, maxRunTimeMinutes: maxRunTimeMinutes, ct: ct));

    // ── Signing inside the pipeline ───────────────────────────────────────────

    /// <summary>Records what it was asked to sign and whether the .intunewin existed by then.</summary>
    private sealed class RecordingSigner(string packagePath, bool succeed, string failure = "") : IFileSigner
    {
        public List<string> Signed { get; } = [];
        public bool SawABuiltPackage { get; private set; }

        public Task<SigningResult> SignFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Signed.Add(filePath);
            SawABuiltPackage |= Directory.Exists(PsadtLayout.IntuneFolder(packagePath))
                && Directory.GetFiles(PsadtLayout.IntuneFolder(packagePath), "*.intunewin").Length > 0;
            return Task.FromResult(new SigningResult { FilePath = filePath, Success = succeed, ErrorMessage = failure });
        }
    }

    [Fact]
    public async Task EveryUnsignedScriptInThePackageIsSignedBeforeTheIntuneWinIsBuilt()
    {
        var package = NewPackage("Contoso_SignedFirst");
        var signer = new RecordingSigner(package, succeed: true);

        await Upload(Service(_converter, signer), package);

        // The deployment script first, then the other PowerShell files it imports: the
        // package's toolkit manifest here carries no signature of its own.
        Assert.Equal(PsadtLayout.ScriptPath(package), signer.Signed[0]);
        Assert.Equal(PackageSigningService.FilesToSign(PsadtLayout.ScriptPath(package)), signer.Signed);
        Assert.Contains(Path.Combine(PsadtLayout.ApplicationFolder(package), PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psd1"),
            signer.Signed);
        Assert.False(signer.SawABuiltPackage, "the script was signed after the .intunewin had already been built");
        Assert.Contains(ActivityLog.Shared.Lines, l =>
            l.Text.Contains($"{PsadtLayout.ScriptName} signed successfully", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARequiredSignatureThatFailsStopsThePublishBeforeAnythingIsBuilt()
    {
        var package = NewPackage("Contoso_SignFails");
        var signer = new RecordingSigner(package, succeed: false, failure: "the smartcard was removed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(Service(_converter, signer), package));

        Assert.Contains("Code signing failed. Publishing stopped: the smartcard was removed", ex.Message);
        // The converter never ran, so the output folder was not even created.
        Assert.False(Directory.Exists(PsadtLayout.IntuneFolder(package)));
        Assert.Empty(Intune.Calls);
        Assert.Null(Link(package));
    }

    [Fact]
    public async Task SigningTurnedOffIsSaidOutLoudRatherThanPassedOverInSilence()
    {
        await Upload(Service(_converter), NewPackage("Contoso_Unsigned"));

        Assert.Contains(ActivityLog.Shared.Lines, l =>
            l.Text.Contains("Code signing explicitly disabled in Settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARealCertificateSignsTheScriptThatGoesIntoTheUploadedIntuneWin()
    {
        if (!CanTimestamp) return;   // No route to the timestamp server: see RealTimestampServer.
        using var certificates = new TempSigningCertificates();
        var package = NewPackage("Contoso_RealSignature");
        var signer = new NativeCodeSigner(certificates.Valid.Thumbprint, RealTimestampServer.Url);

        var appId = await Upload(Service(_converter, signer), package);

        Assert.Equal(ScriptedIntune.AppId, appId);
        var script = PsadtScript.Load(PsadtLayout.ScriptPath(package));
        Assert.True(script.HasSignatureBlock, "the published package's script is unsigned");
        Assert.Contains(PsadtScript.SignatureEnd, script.Content);
        Assert.Equal(ScriptedIntune.AppId, Link(package)?.AppId);
    }

    // ── Rollback ──────────────────────────────────────────────────────────────

    public static TheoryData<string> StagesBeforeCommit() =>
        new("contentVersion", "fileEntry", "commitFile", "commitApp", "uploadState", "azureBlock");

    [Theory]
    [MemberData(nameof(StagesBeforeCommit))]
    public async Task AFailureAtAnyStageBeforeTheContentIsLiveRemovesTheCreatedApp(string stage)
    {
        switch (stage)
        {
            case "contentVersion": Intune.ContentVersionFailure = HttpStatusCode.BadRequest; break;
            case "fileEntry": Intune.FileEntryFailure = HttpStatusCode.BadRequest; break;
            case "commitFile": Intune.CommitFileFailure = HttpStatusCode.BadRequest; break;
            case "commitApp": Intune.CommitAppFailure = HttpStatusCode.BadRequest; break;
            case "uploadState": Intune.PendingFileState = "azureStorageUriRequestFailed"; break;
            case "azureBlock": Intune.BlockStatus = HttpStatusCode.BadRequest; break;
        }
        var package = NewPackage();
        var service = Service(_converter);

        await Assert.ThrowsAnyAsync<Exception>(() => Upload(service, package));

        var delete = Assert.Single(Intune.Deletes);
        Assert.EndsWith($"/mobileApps/{ScriptedIntune.AppId}", delete.Path);
        Assert.True(service.RollbackSucceeded);
        // Nothing may claim the package is linked to an app that was just deleted.
        Assert.Null(Link(package));
    }

    [Fact]
    public async Task ATerminalUploadStateIsReportedAsSuchAndNotWaitedOut()
    {
        Intune.PendingFileState = "azureStorageUriRequestFailed";

        var ex = await Assert.ThrowsAsync<UploadStateException>(() => Upload(Service(_converter), NewPackage()));

        Assert.Equal("azureStorageUriRequestFailed", ex.State);
        Assert.Contains("Check the app in the Intune admin center", ex.Message);
        // One poll, not a retry loop: a terminal state is final.
        Assert.Single(Intune.Where("GET", $"/files/{ScriptedIntune.FileId}"));
    }

    [Fact]
    public async Task AnAzureStorageUriRequestThatSucceedsWithoutAUriIsAnError()
    {
        Intune.OmitSasUri = true;

        var ex = await Assert.ThrowsAsync<UploadStateException>(() => Upload(Service(_converter), NewPackage()));

        Assert.Contains("azureStorageUriRequestSuccess without a URI", ex.Message);
        Assert.Single(Intune.Deletes);
    }

    [Fact]
    public async Task APackageWithNoDetectionRuleIsRefusedBeforeTheAppIsCreated()
    {
        var package = NewPackage();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(Service(_converter), package, rules: []));

        Assert.Contains("Set a detection rule on the Upload step", ex.Message);
        Assert.Empty(Intune.Where("POST", "/mobileApps"));
        Assert.Empty(Intune.Deletes);
        Assert.Null(Link(package));
    }

    [Fact]
    public async Task ARejectedRollbackLeavesTheOriginalFailureAndSaysTheAppMustBeDeletedByHand()
    {
        Intune.FileEntryFailure = HttpStatusCode.BadRequest;
        Intune.DeleteStatus = HttpStatusCode.Forbidden;
        var service = Service(_converter);

        var ex = await Assert.ThrowsAsync<GraphException>(() => Upload(service, NewPackage()));

        Assert.Equal("Create file entry", ex.Operation);
        Assert.False(service.RollbackSucceeded);
        Assert.Contains(ActivityLog.Shared.Lines, l =>
            l.Text.Contains("delete it in the Intune admin center", StringComparison.Ordinal));
    }

    // ── SAS renewal and the Azure transfer ────────────────────────────────────

    [Fact]
    public async Task ASasUriThatIsAboutToExpireIsRenewedBeforeTheFirstBlockIsSent()
    {
        Intune.FirstSasExpiresImmediately = true;

        await Upload(Service(_converter), NewPackage());

        Assert.Equal(1, Intune.SasRenewals);
        Assert.Single(Intune.Where("POST", "/renewUpload"));
        // Every block went to the renewed URI, never the stale one.
        Assert.All(Intune.Calls.Where(c => c.Method == "PUT"), c => Assert.Contains("sig=v2", c.Query));
        Assert.Contains(ActivityLog.Shared.Lines, l => l.Text.Contains("Upload URL renewed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AForbiddenBlockRenewsTheSasOnceAndKeepsTheSameBlockBytes()
    {
        Intune.ForbiddenBlocks = 1;

        await Upload(Service(_converter), NewPackage());

        Assert.Equal(1, Intune.SasRenewals);
        var blocks = Intune.Calls.Where(c => c.Method == "PUT" && !c.Query.Contains("comp=blocklist")).ToList();
        Assert.Equal(2, blocks.Count);
        // The retry keeps the block id, so Azure assembles one block, not two.
        Assert.Equal(blocks[0].Query.Split("blockid=")[1], blocks[1].Query.Split("blockid=")[1]);
        Assert.Equal(FakeEncryptedSize, Intune.BlobBytesReceived);
    }

    [Fact]
    public async Task AnAzureRejectionThatCannotBeRetriedStopsTheUploadImmediately()
    {
        Intune.BlockStatus = HttpStatusCode.BadRequest;

        var ex = await Assert.ThrowsAsync<Exception>(() => Upload(Service(_converter), NewPackage()));

        Assert.Equal("Azure Storage rejected block 1: HTTP 400 BadRequest", ex.Message);
        // One attempt only, and the SAS was not blamed for a 400.
        Assert.Single(Intune.Calls, c => c.Method == "PUT");
        Assert.Equal(0, Intune.SasRenewals);
    }

    [Fact]
    public async Task TheWholeEncryptedPayloadReachesAzureAndTheBlockListIsCommittedLast()
    {
        await Upload(Service(_converter), NewPackage());

        var puts = Intune.Calls.Where(c => c.Method == "PUT").ToList();
        Assert.Equal(2, puts.Count);
        Assert.Contains("comp=block&", puts[0].Query);
        Assert.Contains("comp=blocklist", puts[^1].Query);
        Assert.Equal(FakeEncryptedSize, Intune.BlobBytesReceived);
        // The block list is committed before Intune is told the file is complete.
        var order = Intune.Calls.ToList();
        Assert.True(order.FindIndex(c => c.Query.Contains("comp=blocklist")) <
                    order.FindIndex(c => c.Method == "POST" && c.Path.EndsWith("/commit", StringComparison.Ordinal)));
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CancellingDuringTheAzureTransferRollsBackAndWritesNoLink()
    {
        using var cts = new CancellationTokenSource();
        Intune.OnCall = call => { if (call.Method == "PUT") cts.Cancel(); };
        var package = NewPackage();
        var service = Service(_converter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Upload(service, package, ct: cts.Token));

        Assert.Single(Intune.Deletes);
        Assert.True(service.RollbackSucceeded);
        Assert.Null(Link(package));
        Assert.Empty(Intune.Where("PATCH", $"/mobileApps/{ScriptedIntune.AppId}"));
    }

    [Fact]
    public async Task CancellingAfterIntuneHasTheContentKeepsTheAppAndAsksForFollowUp()
    {
        using var cts = new CancellationTokenSource();
        // The publishing-state poll is the first call after the content version goes live.
        Intune.OnCall = call => { if (call.Method == "GET" && call.Query.Contains("publishingState")) cts.Cancel(); };
        var package = NewPackage();
        var service = Service(_converter);

        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(() => Upload(service, package, ct: cts.Token));

        Assert.Equal(ScriptedIntune.AppId, ex.AppId);
        Assert.Contains("cancelled after Intune accepted the content", ex.Message);
        Assert.Empty(Intune.Deletes);
        Assert.Null(service.RollbackSucceeded);
        // The content is live, so the folder is linked to it even though the run was cut short.
        Assert.Equal(ScriptedIntune.AppId, Link(package)?.AppId);
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenPublishesNothingAtAll()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var package = NewPackage();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Upload(Service(_converter), package, ct: cts.Token));

        Assert.Empty(Intune.Calls);
        Assert.Null(Link(package));
    }

    // ── The create payload ────────────────────────────────────────────────────

    [Fact]
    public async Task TheCreatePayloadCarriesTheRequirementsReturnCodesAndInstallExperienceAsked()
    {
        var requirements = new RequirementInfo
        {
            MinimumOperatingSystem = "Windows 11 22H2",
            MinimumFreeDiskSpaceMB = 500,
            MinimumMemoryMB = 4096,
            MinimumNumberOfProcessors = 2,
            MinimumCpuSpeedMHz = 1400,
        };
        var returnCodes = new List<ReturnCodeInfo>
        {
            new() { Code = 0, Type = ReturnCodeType.Success },
            new() { Code = 42, Type = ReturnCodeType.Retry },
        };

        await Upload(Service(_converter), NewPackage(), requirements: requirements, returnCodes: returnCodes,
            installContext: "User", architecture: "arm64", restartBehavior: "force", maxRunTimeMinutes: 90);

        var app = Body("POST", "/mobileApps");
        Assert.Equal("#microsoft.graph.win32LobApp", app.GetProperty("@odata.type").GetString());
        Assert.Equal("Windows11_22H2", app.GetProperty("minimumSupportedWindowsRelease").GetString());
        Assert.Equal(500, app.GetProperty("minimumFreeDiskSpaceInMB").GetInt32());
        Assert.Equal(4096, app.GetProperty("minimumMemoryInMB").GetInt32());
        Assert.Equal(2, app.GetProperty("minimumNumberOfProcessors").GetInt32());
        Assert.Equal(1400, app.GetProperty("minimumCpuSpeedInMHz").GetInt32());
        Assert.Equal("arm64", app.GetProperty("allowedArchitectures").GetString());
        Assert.Equal("none", app.GetProperty("applicableArchitectures").GetString());

        var experience = app.GetProperty("installExperience");
        Assert.Equal("user", experience.GetProperty("runAsAccount").GetString());
        Assert.Equal("force", experience.GetProperty("deviceRestartBehavior").GetString());
        Assert.Equal(90, experience.GetProperty("maxRunTimeInMinutes").GetInt32());

        var codes = app.GetProperty("returnCodes").EnumerateArray()
            .Select(c => (c.GetProperty("returnCode").GetInt32(), c.GetProperty("type").GetString())).ToList();
        Assert.Equal([(0, "success"), (42, "retry")], codes);
    }

    [Fact]
    public async Task NoReturnCodesOrRequirementsFallsBackToWhatIntuneWouldPreSelect()
    {
        await Upload(Service(_converter), NewPackage(), architecture: "", restartBehavior: "nonsense", maxRunTimeMinutes: 99_999);

        var app = Body("POST", "/mobileApps");
        Assert.Equal("1607", app.GetProperty("minimumSupportedWindowsRelease").GetString());
        Assert.Equal("x86,x64,arm64", app.GetProperty("allowedArchitectures").GetString());
        Assert.False(app.TryGetProperty("minimumFreeDiskSpaceInMB", out _));
        Assert.False(app.TryGetProperty("privacyInformationUrl", out _));

        var experience = app.GetProperty("installExperience");
        Assert.Equal("system", experience.GetProperty("runAsAccount").GetString());
        // A hand-edited settings file must not reach Graph, and 1440 minutes is the cap.
        Assert.Equal(AppSettings.IntuneDefaultsConfig.DefaultRestartBehavior, experience.GetProperty("deviceRestartBehavior").GetString());
        Assert.Equal(1440, experience.GetProperty("maxRunTimeInMinutes").GetInt32());

        var codes = app.GetProperty("returnCodes").EnumerateArray().Select(c => c.GetProperty("returnCode").GetInt32()).ToList();
        Assert.Equal(ReturnCodeInfo.Defaults().Select(d => d.Code), codes);
    }

    [Theory]
    [InlineData("x64", "x64,arm64")]
    [InlineData("AMD64", "x64,arm64")]
    [InlineData("arm64", "arm64")]
    [InlineData("x86", "x86,x64,arm64")]
    [InlineData(null, "x86,x64,arm64")]
    public void ThePackagesArchitectureOnlyRulesOutDevicesThatCannotRunIt(string? architecture, string expected) =>
        Assert.Equal(expected, IntuneUploadService.AllowedArchitectures(architecture));

    // ── Supersedence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EverySupersedenceLinkIsWrittenInOneUpdateRelationshipsCall()
    {
        var links = new List<SupersedenceLink> { new("old-app-1", "update"), new("old-app-2", "replace") };

        await Upload(Service(_converter), NewPackage(), supersedence: links);

        var call = Assert.Single(Intune.Where("POST", "/updateRelationships"));
        Assert.Contains($"/mobileApps/{ScriptedIntune.AppId}/updateRelationships", call.Path);
        var relationships = JsonSerializer.Deserialize<JsonElement>(call.Body).GetProperty("relationships");
        Assert.Equal(2, relationships.GetArrayLength());
        foreach (var (relationship, link) in relationships.EnumerateArray().Zip(links))
        {
            Assert.Equal("#microsoft.graph.mobileAppSupersedence", relationship.GetProperty("@odata.type").GetString());
            Assert.Equal(link.AppId, relationship.GetProperty("targetId").GetString());
            Assert.Equal("child", relationship.GetProperty("targetType").GetString());
            Assert.Equal(link.Type, relationship.GetProperty("supersedenceType").GetString());
        }
    }

    [Fact]
    public async Task RejectedSupersedenceIsAFollowUpWarningAndNeverRollsBackTheApp()
    {
        Intune.SupersedenceFailure = HttpStatusCode.BadRequest;
        var service = Service(_converter);
        var package = NewPackage();

        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(
            () => Upload(service, package, supersedence: [new SupersedenceLink("old-app", "update")]));

        Assert.Equal(ScriptedIntune.AppId, ex.AppId);
        Assert.Contains("Intune rejected the supersedence (HTTP 400)", ex.Message);
        Assert.Contains("BadRequest from the scripted tenant", ex.Message);
        Assert.Empty(Intune.Deletes);
        Assert.Equal(ScriptedIntune.AppId, Link(package)?.AppId);
    }

    [Fact]
    public async Task NoSupersedenceMeansNoRelationshipCallAtAll()
    {
        await Upload(Service(_converter), NewPackage(), supersedence: []);

        Assert.Empty(Intune.Where("POST", "/updateRelationships"));
    }

    // ── Assignments ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PickedExistingAndPerPackageGroupsAllReachIntuneWithTheirOwnIntent()
    {
        Intune.ExistingGroups["Contoso Pilot"] = "pilot-group";
        // The uninstall group already exists, the install group does not.
        Intune.ExistingGroups[$"Contoso_{AppName}_1.0_Uninstall"] = "existing-uninstall-group";
        var config = new AppSettings.GroupAssignmentConfig
        {
            ExistingGroups = [new AppSettings.ExistingGroupAssignment { GroupName = "Contoso Pilot", Intent = AssignmentIntent.Available }],
            CreateGroupPerPackage = true,
            NewGroupIntent = AssignmentIntent.Required,
            CreateUninstallGroupPerPackage = true,
        };
        var picked = new List<AssignedGroup>
        {
            new() { GroupId = "picked-group", GroupName = "Helpdesk", AssignmentType = "uninstall" },
            new() { GroupId = "", GroupName = "ignored, no id", AssignmentType = "required" },
        };

        await Upload(Service(_converter), NewPackage(), groupAssignment: config, pickedGroups: picked);

        var assignments = Intune.Where("POST", "/assignments")
            .Select(c => JsonSerializer.Deserialize<JsonElement>(c.Body))
            .Select(b => (Intent: b.GetProperty("intent").GetString(),
                          Group: b.GetProperty("target").GetProperty("groupId").GetString()))
            .ToList();

        Assert.Equal(4, assignments.Count);
        Assert.Contains(("uninstall", "picked-group"), assignments);
        Assert.Contains(("available", "pilot-group"), assignments);
        Assert.Contains(("required", "new-group-1"), assignments);
        Assert.Contains(("uninstall", "existing-uninstall-group"), assignments);

        // Only the install group had to be created, and with a Graph-safe mail nickname.
        var created = Assert.Single(Intune.Where("POST", "/groups"));
        var group = JsonSerializer.Deserialize<JsonElement>(created.Body);
        Assert.Equal($"Contoso_{AppName}_1.0_Install", group.GetProperty("displayName").GetString());
        Assert.Equal($"Contoso_{AppName}_10_Install", group.GetProperty("mailNickname").GetString());
        Assert.True(group.GetProperty("securityEnabled").GetBoolean());
        Assert.False(group.GetProperty("mailEnabled").GetBoolean());

        // Reusing a group nobody has vetted is called out.
        Assert.Contains(ActivityLog.Shared.Lines, l =>
            l.Text.Contains("already existed", StringComparison.Ordinal) && l.Text.Contains("Check who owns it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExistingGroupThatEntraDoesNotHaveIsSkippedAndReportedAfterwards()
    {
        var config = new AppSettings.GroupAssignmentConfig
        {
            ExistingGroups =
            [
                new AppSettings.ExistingGroupAssignment { GroupName = "Nowhere", Intent = AssignmentIntent.Required },
                new AppSettings.ExistingGroupAssignment { GroupName = "   ", Intent = AssignmentIntent.Required },
            ],
        };

        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(
            () => Upload(Service(_converter), NewPackage(), groupAssignment: config));

        Assert.Contains("Group 'Nowhere' not found in Entra ID - skipped", ex.Message);
        Assert.Empty(Intune.Where("POST", "/assignments"));
        // A blank name is not looked up, so there is only one lookup and no second warning.
        Assert.Single(Intune.Where("GET", "/groups"));
        Assert.Empty(Intune.Deletes);
    }

    [Fact]
    public async Task ARefusedAssignmentIsReportedWithIntunesOwnMessageAndTheAppIsKept()
    {
        Intune.AssignmentFailure = HttpStatusCode.Forbidden;
        var picked = new List<AssignedGroup> { new() { GroupId = "picked-group", GroupName = "Helpdesk", AssignmentType = "required" } };
        var package = NewPackage();

        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(
            () => Upload(Service(_converter), package, pickedGroups: picked));

        Assert.Contains("Could not assign 'Helpdesk' (HTTP 403)", ex.Message);
        Assert.Empty(Intune.Deletes);
        Assert.Equal(ScriptedIntune.AppId, Link(package)?.AppId);
    }

    [Fact]
    public async Task ARefusedGroupCreationNamesThePermissionItNeeds()
    {
        Intune.CreateGroupFailure = HttpStatusCode.Forbidden;
        var config = new AppSettings.GroupAssignmentConfig { CreateGroupPerPackage = true };

        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(
            () => Upload(Service(_converter), NewPackage(), groupAssignment: config));

        Assert.Contains("needs Group.ReadWrite.All", ex.Message);
        Assert.Empty(Intune.Where("POST", "/assignments"));
    }

    [Fact]
    public async Task APerPackageTemplateThatResolvesToNothingIsSkippedWithAWarning()
    {
        var config = new AppSettings.GroupAssignmentConfig { CreateGroupPerPackage = true, GroupNameTemplate = "   " };

        var ex = await Assert.ThrowsAsync<IntuneFollowUpException>(
            () => Upload(Service(_converter), NewPackage(), groupAssignment: config));

        Assert.Contains("resolved to empty - skipped", ex.Message);
        Assert.Empty(Intune.Where("POST", "/groups"));
        Assert.Empty(Intune.Where("GET", "/groups"));
    }

    [Fact]
    public async Task AGroupNameWithAQuoteIsEscapedForTheEntraFilterInsteadOfBreakingIt()
    {
        Intune.ExistingGroups["O'Brien Pilot"] = "obrien-group";
        var config = new AppSettings.GroupAssignmentConfig
        {
            ExistingGroups = [new AppSettings.ExistingGroupAssignment { GroupName = "O'Brien Pilot", Intent = AssignmentIntent.Required }],
        };

        await Upload(Service(_converter), NewPackage(), groupAssignment: config);

        var lookup = Assert.Single(Intune.Where("GET", "/groups"));
        Assert.Contains("displayName%20eq%20%27O%27%27Brien%20Pilot%27", lookup.Query);
        var assignment = JsonSerializer.Deserialize<JsonElement>(Assert.Single(Intune.Where("POST", "/assignments")).Body);
        Assert.Equal("obrien-group", assignment.GetProperty("target").GetProperty("groupId").GetString());
    }

    [Fact]
    public async Task NoGroupsConfiguredMeansNoGroupCallsAtAll()
    {
        await Upload(Service(_converter), NewPackage(), groupAssignment: new AppSettings.GroupAssignmentConfig());

        Assert.Empty(Intune.Where("GET", "/groups"));
        Assert.Empty(Intune.Where("POST", "/groups"));
        Assert.Empty(Intune.Where("POST", "/assignments"));
    }

    // ── Republish (content update) ────────────────────────────────────────────

    [Fact]
    public async Task RepublishingContentWritesANewContentVersionAndChangesNothingElse()
    {
        var package = NewPackage("Contoso_Republish");
        await Upload(Service(_converter), package);
        var firstVersion = Intune.LastContentVersionId;
        var service = Service(_converter);
        var callsBefore = Intune.Calls.Count;

        var version = await service.UpdatePackageContentAsync(ScriptedIntune.AppId, "Republished app", package, "2.0");

        Assert.Equal("cv-2", version);
        Assert.NotEqual(firstVersion, version);
        var update = Intune.Calls.Skip(callsBefore).ToList();
        Assert.DoesNotContain(update, c => c.Method == "POST" && c.Path.EndsWith("/mobileApps", StringComparison.Ordinal));
        Assert.DoesNotContain(update, c => c.Path.EndsWith("/assignments", StringComparison.Ordinal));
        Assert.DoesNotContain(update, c => c.Path.EndsWith("/updateRelationships", StringComparison.Ordinal));
        Assert.DoesNotContain(update, c => c.Method == "DELETE");

        // The only app-level write is the committed version; nothing else is PATCHed.
        var patch = Assert.Single(update, c => c.Method == "PATCH");
        var body = JsonSerializer.Deserialize<JsonElement>(patch.Body);
        Assert.Equal(["@odata.type", "committedContentVersion"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("cv-2", body.GetProperty("committedContentVersion").GetString());

        // The link now describes the republished content.
        var link = Link(package);
        Assert.Equal(ScriptedIntune.AppId, link?.AppId);
        Assert.Equal("2.0", link?.DisplayVersion);
        Assert.Equal("Republished app", link?.DisplayName);
        Assert.Equal(Fakes.UnencryptedSize, link?.UnencryptedSize);
    }

    [Fact]
    public async Task AFailedRepublishNeverDeletesTheAppItWasUpdating()
    {
        var package = NewPackage("Contoso_RepublishFails");
        Intune.ContentVersionFailure = HttpStatusCode.BadRequest;
        var service = Service(_converter);

        var ex = await Assert.ThrowsAsync<GraphException>(
            () => service.UpdatePackageContentAsync(ScriptedIntune.AppId, "Live app", package));

        Assert.Equal("Create content version", ex.Operation);
        Assert.Empty(Intune.Deletes);
        Assert.Null(service.RollbackSucceeded);
        // The app keeps its old content, so the folder keeps whatever link it had: none here.
        Assert.Null(Link(package));
    }

    [Fact]
    public async Task RepublishingRefusesAnEmptyAppIdAndAFolderThatIsNotThere()
    {
        var service = Service(_converter);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdatePackageContentAsync(" ", "App", NewPackage()));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => service.UpdatePackageContentAsync(ScriptedIntune.AppId, "App", Path.Combine(Root, "not-a-folder")));
        Assert.Empty(Intune.Calls);
    }

    [Fact]
    public async Task ACancelledRepublishLeavesTheLiveContentVersionCommitted()
    {
        var package = NewPackage("Contoso_RepublishCancel");
        using var cts = new CancellationTokenSource();
        Intune.OnCall = call => { if (call.Method == "PUT") cts.Cancel(); };
        var service = Service(_converter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.UpdatePackageContentAsync(ScriptedIntune.AppId, "Live app", package, "2.0", null, cts.Token));

        Assert.Empty(Intune.Deletes);
        Assert.Empty(Intune.Where("PATCH", $"/mobileApps/{ScriptedIntune.AppId}"));
        Assert.Null(Link(package));
    }

    // ── The link ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLinkIsWrittenNextToTheBuiltIntuneWinWithTheTenantThatAcceptedIt()
    {
        var package = NewPackage("Contoso_Link");

        await Upload(Service(_converter), package);

        var path = PackageMarker.PathFor(package);
        Assert.Equal(Path.Combine(PsadtLayout.IntuneFolder(package), PackageMarker.FileName), path);
        Assert.True(File.Exists(path));
        var link = Link(package)!;
        Assert.Equal(Tenant, link.TenantId);
        Assert.Equal(Info().DisplayName, link.DisplayName);
        Assert.Equal("1.0", link.DisplayVersion);
        Assert.Equal("Invoke-AppDeployToolkit.intunewin", link.IntunewinFileName);
        Assert.True(DateTimeOffset.TryParse(link.UploadedAt, out _), $"UploadedAt '{link.UploadedAt}' is not a timestamp");
    }

    [Fact]
    public async Task AnUnwritableIntuneFolderWarnsButStillCountsAsAPublish()
    {
        var package = NewPackage("Contoso_ReadOnlyLink");
        var marker = PackageMarker.PathFor(package);
        Directory.CreateDirectory(PsadtLayout.IntuneFolder(package));
        File.WriteAllText(marker, "{}");
        File.SetAttributes(marker, FileAttributes.ReadOnly);
        var service = Service(_converter);

        try
        {
            var appId = await Upload(service, package);

            Assert.Equal(ScriptedIntune.AppId, appId);
            Assert.NotNull(service.LinkWarning);
            Assert.Contains("could not be written", service.LinkWarning);
            Assert.Contains("Republish content", service.LinkWarning);
        }
        finally
        {
            File.SetAttributes(marker, FileAttributes.Normal);
        }
    }
}

// ── 4. The guard in front of a second publish ─────────────────────────────────

/// <summary>
/// PublishGuard decides whether a folder that already names an Intune app may create a
/// second one. The app lookup and the prompt are both supplied by the caller.
/// </summary>
public sealed class PublishGuardTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "22222222-2222-2222-2222-222222222222";

    private readonly string _root = Directory.CreateTempSubdirectory("packman-guard-").FullName;
    private readonly IntuneAuthService _auth = new();

    public PublishGuardTests() => _auth.UseTestSession(Tenant, "test-token");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class StubDialogs(bool answer) : IDialogService
    {
        public List<string> Asked { get; } = [];
        public bool Confirm(string message, string title, string action, string safe = "Cancel")
        {
            Asked.Add(message);
            return answer;
        }
        public bool? ConfirmOrCancel(string message, string title, string yes, string no) => throw new NotSupportedException();
        public void Warn(string message, string title) => throw new NotSupportedException();
        public void Info(string message, string title) => throw new NotSupportedException();
    }

    private string Package(string name, (string AppId, string TenantId)? link = null, bool legacy = false)
    {
        var package = Path.Combine(_root, name);
        Directory.CreateDirectory(PsadtLayout.IntuneFolder(package));
        if (legacy)
            File.WriteAllText(Path.Combine(package, ".intune-appid.json"), $"{{\"intuneAppId\":\"{link!.Value.AppId}\"}}");
        else if (link is { } value)
            PackageMarker.Save(package, new PackageMarker.Link
            {
                AppId = value.AppId,
                TenantId = value.TenantId,
                DisplayName = "Contoso Reader 1.0",
                UploadedAt = DateTimeOffset.UtcNow.ToString("o"),
            });
        return package;
    }

    private static Func<string, Task<bool>> Exists(bool exists) => _ => Task.FromResult(exists);

    [Fact]
    public async Task AFolderWithNoLinkPublishesWithoutAsking()
    {
        var dialogs = new StubDialogs(false);

        Assert.True(await PublishGuard.ConfirmNewAppAsync(Package("Unlinked"), _auth, Exists(true), dialogs));
        Assert.Empty(dialogs.Asked);
    }

    [Fact]
    public async Task AFolderLinkedToAnAppThatIsGonePublishesWithoutAsking()
    {
        var dialogs = new StubDialogs(false);
        var package = Package("Deleted", ("deleted-app", Tenant));

        Assert.True(await PublishGuard.ConfirmNewAppAsync(package, _auth, Exists(false), dialogs));
        Assert.Empty(dialogs.Asked);
    }

    [Fact]
    public async Task AFolderLinkedInAnotherTenantPublishesWithoutAsking()
    {
        var dialogs = new StubDialogs(false);
        var package = Package("Foreign", ("foreign-app", OtherTenant));

        Assert.True(await PublishGuard.ConfirmNewAppAsync(package, _auth, Exists(true), dialogs));
        Assert.Empty(dialogs.Asked);
    }

    [Fact]
    public async Task ALegacyMarkerWithNoTenantPublishesWithoutAsking()
    {
        var dialogs = new StubDialogs(false);
        var package = Package("Legacy", ("legacy-app", ""), legacy: true);

        Assert.True(await PublishGuard.ConfirmNewAppAsync(package, _auth, Exists(true), dialogs));
        Assert.Empty(dialogs.Asked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFolderLinkedToALiveAppInThisTenantAsksFirstAndObeysTheAnswer(bool publishAnyway)
    {
        var dialogs = new StubDialogs(publishAnyway);
        var package = Package("Linked", ("linked-app", Tenant));

        var allowed = await PublishGuard.ConfirmNewAppAsync(package, _auth, Exists(true), dialogs);

        Assert.Equal(publishAnyway, allowed);
        var message = Assert.Single(dialogs.Asked);
        Assert.Contains("Contoso Reader 1.0", message);
        Assert.Contains("Publishing again creates a second Intune app", message);
        Assert.Contains("Republish content", message);
    }

    [Fact]
    public async Task AMarkerWithIdsThatCouldChangeAGraphUrlIsNotALink()
    {
        var package = Path.Combine(_root, "Tampered");
        Directory.CreateDirectory(PsadtLayout.IntuneFolder(package));
        File.WriteAllText(PackageMarker.PathFor(package),
            $"{{\"appId\":\"app/../../groups\",\"tenantId\":\"{Tenant}\"}}");
        var dialogs = new StubDialogs(false);

        Assert.Null(PackageMarker.Read(package));
        Assert.True(await PublishGuard.ConfirmNewAppAsync(package, _auth, Exists(true), dialogs));
        Assert.Empty(dialogs.Asked);
    }

    [Theory]
    [InlineData("%vendor%_%appName%_%appVersion%_Install", "Contoso_Reader_1.0_Install")]
    [InlineData("%VENDOR% %appname%", "Contoso Reader")]
    [InlineData("  %vendor%  ", "Contoso")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Static name", "Static name")]
    public void TheGroupNameTemplateExpandsTheSameTokensTheSettingsPageOffers(string template, string expected) =>
        Assert.Equal(expected, GroupAssignmentNamer.Build(template, "Contoso", "Reader", "1.0"));

    [Fact]
    public void AGroupNameTemplateWithNothingToFillInCollapsesToEmpty() =>
        Assert.Equal("", GroupAssignmentNamer.Build("%vendor%%appName%%appVersion%", "", "", ""));
}
