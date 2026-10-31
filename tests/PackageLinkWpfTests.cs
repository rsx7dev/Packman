#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:property AssemblyName=PackageLinkWpfTests
#:project ../Packman/Packman.csproj

using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Packman;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;

// The packman.id link end to end, offline: the real upload service, Application detail view
// model and view, with Graph and Azure Storage answered by a fake handler, a fake
// IntuneWinAppUtil and a test sign-in. Covers a successful upload writing the link, a link
// that cannot be written (upload still succeeds, warning logged and shown), and Republish
// content / Edit script shown only for a folder linked to an existing app in the connected
// tenant. Nothing signs in or calls a real service. Windows only (WPF).
//   dotnet run --file tests/PackageLinkWpfTests.cs -c Release
// Exits non-zero when a check fails. Settings are changed in memory only, never saved.
internal static class PackageLinkWpfTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "22222222-2222-2222-2222-222222222222";
    private const string AppName = "PackmanLinkTest";
    private const long UnencryptedSize = 4096;

    private static int _passed;
    private static readonly List<string> Failures = new();
    private static readonly FakeIntune Intune = new();
    private static string _share = "";

    [STAThread]
    public static int Main()
    {
        _share = Directory.CreateTempSubdirectory("packman-link-wpf-").FullName;
        GraphClient.Http = new HttpClient(Intune);
        IntuneUploadService.AzureBlob = new HttpClient(Intune);
        AppServices.Auth.UseTestSession(Tenant, "test-token");
        var settings = AppServices.Settings.Settings;
        settings.NetworkPaths.IntuneApplications = _share;
        settings.NetworkPaths.IntuneWinAppUtil = FakeIntuneWinAppUtil();
        settings.CodeSigning.Enabled = false;

        try
        {
            // Upload and load checks run before the WPF Application exists: the detail view
            // model marshals progress to its dispatcher, which this thread would block.
            Run("A new upload writes Intune\\packman.id with the uploaded app's values", NewUploadWritesLink);
            Run("A content update overwrites packman.id with that app", UpdateOverwritesLink);
            Run("An upload writes every chosen supersedence in one updateRelationships call", UploadWritesSupersedence);
            Run("Rejected supersedence keeps the app and shows Intune's message as a warning", RejectedSupersedenceWarns);
            Run("The Supersedes search lists every matching app, not just the first 20", SearchListsEveryMatch);
            Run("An Upgrade's previous version is only offered; nothing is written until it is added", SuggestionIsOptIn);
            Run("Changing a type or removing an app reaches the review text and the upload payload", TypeAndRemovalPropagate);
            Run("The Supersedes field starts empty, defaults to Update and blocks itself and duplicates", SupersedenceFieldRules);
            Run("A read-only packman.id leaves the upload successful and logs a warning", ReadOnlyLinkStillSucceeds);

            var linked = Load("linked-app", link: ("linked-app", Tenant));
            var missing = Load("unlinked-app", link: null);
            var otherTenant = Load("foreign-app", link: ("foreign-app", OtherTenant));
            var deleted = Load("deleted-app", link: ("deleted-app", Tenant));
            var renamed = Load("renamed-app", link: ("renamed-app", Tenant), folder: "Contoso_Reader");
            var warned = Republished("warned-app");

            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();

            Run("A folder linked to the app in this tenant shows Republish content and Edit script", () =>
            {
                var view = Layout(linked);
                True(HasAction(view, "Republish content"), "Republish content shown");
                True(HasAction(view, "Edit script"), "Edit script shown");
            });
            Run("A package folder named differently from the Intune display name still shows the actions", () =>
                True(HasAction(Layout(renamed), "Republish content"), "Republish content shown"));
            Run("An app with no packman.id hides the actions", () => NoActions(Layout(missing)));
            Run("A packman.id from another tenant hides the actions", () => NoActions(Layout(otherTenant)));
            Run("A link to an app Intune no longer has hides the actions", () =>
            {
                True(deleted.StatusText.Contains("Could not load full details"), $"load error shown: '{deleted.StatusText}'");
                NoActions(Layout(deleted));
            });
            Run("A republish whose packman.id cannot be written shows the warning on the page", () =>
            {
                True(warned.StatusText.StartsWith("The upload succeeded"), $"warning in StatusText: '{warned.StatusText}'");
                True(Shown<TextBlock>(Layout(warned)).Any(t => t.Text == warned.StatusText), "warning banner visible");
                True(HasAction(Layout(warned), "Republish content"), "the old link still resolves");
            });
        }
        finally
        {
            Directory.Delete(_share, recursive: true);
            foreach (var kind in new[] { "Upload", "Update" })
                File.Delete(LogPath(kind));
            // The detail page logs its republish under the app's display name.
            foreach (var log in Directory.GetFiles(Path.GetDirectoryName(LogPath("Update"))!, "Contoso*warned-app*.log"))
                File.Delete(log);
        }

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── Upload service ──

    private static void NewUploadWritesLink()
    {
        var package = Package("Contoso_NewApp", "2.0");
        var service = new IntuneUploadService(() => Task.FromResult("test-token"), Tenant, null, FakeIntuneWinAppUtil());
        var appInfo = new ApplicationInfo { Manufacturer = "Contoso", Name = "NewApp", Version = "2.0", DisplayName = AppName };
        var rules = new List<DetectionRule> { new() { Type = DetectionRuleType.File, Path = @"C:\Program Files\Contoso", FileOrFolderName = "app.exe" } };

        var appId = Task.Run(() => service.UploadWin32ApplicationAsync(appInfo, package, rules,
            "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.exe -DeploymentType Uninstall", "test", "system")).GetAwaiter().GetResult();

        Equal(FakeIntune.CreatedAppId, appId);
        Equal(null, service.LinkWarning);
        var link = PackageMarker.Read(package)!;
        Equal(FakeIntune.CreatedAppId, link.AppId);
        Equal(Tenant, link.TenantId);
        Equal(AppName, link.DisplayName);
        Equal("2.0", link.DisplayVersion);
        Equal("Invoke-AppDeployToolkit.intunewin", link.IntunewinFileName);
        Equal(UnencryptedSize, link.UnencryptedSize);
        True(DateTime.UtcNow - DateTime.Parse(link.UploadedAt, null, System.Globalization.DateTimeStyles.RoundtripKind) < TimeSpan.FromMinutes(5), "uploadedAt is now, in UTC");
        True(!File.Exists(Path.Combine(package, "Application", PackageMarker.FileName)), "nothing in Application\\");
    }

    private static readonly SupersedenceLink[] TwoApps = [new("old-1", SupersedenceType.Update), new("old-2", SupersedenceType.Replace)];

    private static void UploadWritesSupersedence()
    {
        Intune.RelationshipBodies.Clear();
        var (service, appInfo, package, rules) = SupersedenceUpload("Contoso_Supersede");

        Task.Run(() => service.UploadWin32ApplicationAsync(appInfo, package, rules, "i", "u", "test", "system", supersedence: TwoApps)).GetAwaiter().GetResult();

        Equal(1, Intune.RelationshipBodies.Count);
        var relationships = JsonDocument.Parse(Intune.RelationshipBodies[0]).RootElement.GetProperty("relationships");
        Equal(2, relationships.GetArrayLength());
        Equal("old-1", relationships[0].GetProperty("targetId").GetString());
        Equal("update", relationships[0].GetProperty("supersedenceType").GetString());
        Equal("old-2", relationships[1].GetProperty("targetId").GetString());
        Equal("replace", relationships[1].GetProperty("supersedenceType").GetString());
    }

    private static void RejectedSupersedenceWarns()
    {
        Intune.RelationshipBodies.Clear();
        Intune.RejectRelationships = true;
        try
        {
            var (service, appInfo, package, rules) = SupersedenceUpload("Contoso_TooLarge");

            var ex = Assert(() => Task.Run(() => service.UploadWin32ApplicationAsync(appInfo, package, rules, "i", "u", "test", "system", supersedence: TwoApps)).GetAwaiter().GetResult());

            True(ex is IntuneFollowUpException { AppId: FakeIntune.CreatedAppId }, $"follow-up exception: {ex}");
            True(ex!.Message.Contains("supersedence graph is too large"), $"Intune's message shown: '{ex.Message}'");
        }
        finally
        {
            Intune.RejectRelationships = false;
        }
    }

    private static void SupersedenceFieldRules()
    {
        var field = new SupersedencePickerViewModel(() => "own-app");
        Equal(0, field.Selected.Count);

        field.AddCommand.Execute(new IntuneApplication { Id = "own-app", DisplayName = "Own" });
        Equal(0, field.Selected.Count);
        True(field.Hint.Contains("itself"), $"self hint: '{field.Hint}'");

        var other = new IntuneApplication { Id = "other", DisplayName = "Other", Version = "1.0", Publisher = "Contoso" };
        field.AddCommand.Execute(other);
        field.AddCommand.Execute(other);
        Equal(1, field.Selected.Count);
        Equal(SupersedenceType.Update, field.Selected[0].Type);
        True(field.Hint.Contains("already in the list"), $"duplicate hint: '{field.Hint}'");
    }

    private static void SearchListsEveryMatch()
    {
        Intune.ListedAppCount = 25;
        try
        {
            var field = new SupersedencePickerViewModel(() => "");
            field.Query = "Contoso Reader";
            Task.Run(field.SearchAsync).GetAwaiter().GetResult();

            Equal(25, field.Results.Count);
            Equal(25, field.Results.Select(a => a.Id).Distinct().Count());
        }
        finally
        {
            Intune.ListedAppCount = 0;
        }
    }

    private static void SuggestionIsOptIn()
    {
        Intune.RelationshipBodies.Clear();
        var field = new SupersedencePickerViewModel(() => "");
        Task.Run(() => field.SuggestAsync("pred-app")).GetAwaiter().GetResult();

        True(field.HasSuggestion, "previous version offered");
        True(field.SuggestionText.Contains("Contoso Reader pred-app") && field.SuggestionText.Contains("1.0"), $"suggestion text: '{field.SuggestionText}'");
        Equal(0, field.Selected.Count);
        Equal(0, field.Links.Count);

        var (service, appInfo, package, rules) = SupersedenceUpload("Contoso_OptIn");
        Task.Run(() => service.UploadWin32ApplicationAsync(appInfo, package, rules, "i", "u", "test", "system", supersedence: field.Links)).GetAwaiter().GetResult();
        Equal(0, Intune.RelationshipBodies.Count);

        field.AddSuggestionCommand.Execute(null);
        Equal(1, field.Selected.Count);
        Equal("pred-app", field.Selected[0].Id);
        Equal(SupersedenceType.Update, field.Selected[0].Type);
        True(!field.HasSuggestion, "offer gone once added");
    }

    private static void TypeAndRemovalPropagate()
    {
        Intune.RelationshipBodies.Clear();
        var upload = new UploadStepViewModel(new CreatePackageViewModel(), AppServices.Settings, AppServices.Auth);
        var changed = new List<string?>();
        upload.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Task.Run(() => upload.Supersedence.SuggestAsync("pred-app")).GetAwaiter().GetResult();
        Equal(false, upload.HasSupersedence);

        upload.Supersedence.AddSuggestionCommand.Execute(null);
        Equal(true, upload.HasSupersedence);
        Equal("Contoso Reader pred-app (Update)", upload.SupersedenceRailText);

        changed.Clear();
        upload.Supersedence.Selected[0].Type = SupersedenceType.Replace;
        True(changed.Contains(nameof(UploadStepViewModel.SupersedenceRailText)), "rail text change raised");
        Equal("Contoso Reader pred-app (Replace)", upload.SupersedenceRailText);
        Equal("Replace", upload.Supersedence.Selected[0].TypeLabel);

        var (service, appInfo, package, rules) = SupersedenceUpload("Contoso_TypeChange");
        Task.Run(() => service.UploadWin32ApplicationAsync(appInfo, package, rules, "i", "u", "test", "system", supersedence: upload.Supersedence.Links)).GetAwaiter().GetResult();
        Equal(1, Intune.RelationshipBodies.Count);
        var relationship = JsonDocument.Parse(Intune.RelationshipBodies[0]).RootElement.GetProperty("relationships")[0];
        Equal("pred-app", relationship.GetProperty("targetId").GetString());
        Equal("replace", relationship.GetProperty("supersedenceType").GetString());

        upload.Supersedence.RemoveCommand.Execute(upload.Supersedence.Selected[0]);
        Equal(false, upload.HasSupersedence);
        Equal("", upload.SupersedenceRailText);
        Equal(0, upload.Supersedence.Links.Count);
    }

    private static (IntuneUploadService Service, ApplicationInfo App, string Package, List<DetectionRule> Rules) SupersedenceUpload(string folder)
    {
        Intune.ForgetCommittedFiles();
        var service = new IntuneUploadService(() => Task.FromResult("test-token"), Tenant, null, FakeIntuneWinAppUtil());
        var appInfo = new ApplicationInfo { Manufacturer = "Contoso", Name = "Supersede", Version = "2.0", DisplayName = AppName };
        var rules = new List<DetectionRule> { new() { Type = DetectionRuleType.File, Path = @"C:\Program Files\Contoso", FileOrFolderName = "app.exe" } };
        return (service, appInfo, Package(folder, "2.0"), rules);
    }

    private static Exception? Assert(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static void UpdateOverwritesLink()
    {
        var package = Package("Contoso_Update", "1.0");
        PackageMarker.Save(package, new PackageMarker.Link { AppId = "old-app", TenantId = Tenant });
        var service = new IntuneUploadService(() => Task.FromResult("test-token"), Tenant, null, FakeIntuneWinAppUtil());

        var version = Task.Run(() => service.UpdatePackageContentAsync("other-app", AppName, package, "1.0")).GetAwaiter().GetResult();

        Equal(FakeIntune.ContentVersionId, version);
        Equal(null, service.LinkWarning);
        Equal("other-app", PackageMarker.Read(package)!.AppId);
    }

    private static void ReadOnlyLinkStillSucceeds()
    {
        var package = Package("Contoso_ReadOnly", "1.0");
        PackageMarker.Save(package, new PackageMarker.Link { AppId = "ro-app", TenantId = Tenant });
        File.SetAttributes(PackageMarker.PathFor(package), FileAttributes.ReadOnly);
        var service = new IntuneUploadService(() => Task.FromResult("test-token"), Tenant, null, FakeIntuneWinAppUtil());
        try
        {
            var version = Task.Run(() => service.UpdatePackageContentAsync("ro-app", AppName, package, "1.0")).GetAwaiter().GetResult();

            Equal(FakeIntune.ContentVersionId, version);
            True(service.LinkWarning?.StartsWith("The upload succeeded") == true, $"warning: '{service.LinkWarning}'");
            True(File.ReadAllLines(LogPath("Update")).Any(l => l.Contains("WARN") && l.Contains(service.LinkWarning!)), "warning in the update log");
        }
        finally
        {
            File.SetAttributes(PackageMarker.PathFor(package), FileAttributes.Normal);
        }
    }

    // ── Application detail ──

    /// <summary>Opens the detail page for an app the way the Applications list does, and loads it.</summary>
    private static ApplicationDetailViewModel Load(string appId, (string AppId, string TenantId)? link, string folder = "Contoso_App")
    {
        var package = Package($"{folder}_{appId}", "1.0");
        if (link is { } l)
            PackageMarker.Save(package, new PackageMarker.Link { AppId = l.AppId, TenantId = l.TenantId, UploadedAt = DateTime.UtcNow.ToString("o") });

        var vm = Detail(appId);
        Task.Run(vm.LoadAsync).GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>A linked app republished from the detail page while its packman.id is read-only.</summary>
    private static ApplicationDetailViewModel Republished(string appId)
    {
        var vm = Load(appId, link: (appId, Tenant));
        var path = PackageMarker.PathFor(vm.SourcePath!);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            True(Task.Run(vm.UpdatePackageContentAsync).GetAwaiter().GetResult(), $"republish succeeded: '{vm.StatusText}'");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        return vm;
    }

    private static ApplicationDetailViewModel Detail(string appId) =>
        new(new IntuneApplication { Id = appId, DisplayName = $"Contoso Reader {appId}", Publisher = "Contoso", Version = "1.0" })
        {
            Devices = new DeviceStatusViewModel((_, _) => Task.FromResult(new List<DeviceInstallStatus>())),
        };

    private static ApplicationDetailView Layout(ApplicationDetailViewModel vm)
    {
        var view = new ApplicationDetailView { DataContext = vm };
        view.Measure(new Size(1320, 2400));
        view.Arrange(new Rect(0, 0, 1320, 2400));
        view.UpdateLayout();
        return view;
    }

    private static bool HasAction(DependencyObject view, string text) =>
        Shown<Button>(view).Any(b => Shown<TextBlock>(b).Any(t => t.Text == text));

    private static void NoActions(DependencyObject view)
    {
        True(!HasAction(view, "Republish content"), "Republish content hidden");
        True(!HasAction(view, "Edit script"), "Edit script hidden");
    }

    /// <summary>Descendants that took up space in the layout; collapsed ones measure to zero.</summary>
    private static IEnumerable<T> Shown<T>(DependencyObject root) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is T match && match.ActualHeight > 0) yield return match;
            foreach (var nested in Shown<T>(child)) yield return nested;
        }
    }

    // ── Fixtures ──

    /// <summary>A minimal PSADT v4 package under the share that passes the upload preflight.</summary>
    private static string Package(string appFolder, string version)
    {
        var package = Path.Combine(_share, appFolder, version);
        var application = Path.Combine(package, "Application");
        Directory.CreateDirectory(Path.Combine(application, "PSAppDeployToolkit"));
        File.WriteAllText(Path.Combine(application, PsadtLayout.ScriptName), "$adtSession = @{ AppName = 'Test' }\n");
        File.WriteAllText(Path.Combine(application, PsadtLayout.SetupFileName), "");
        File.WriteAllText(Path.Combine(application, "PSAppDeployToolkit", "PSAppDeployToolkit.psd1"), "@{}\n");
        return package;
    }

    /// <summary>A stand-in IntuneWinAppUtil that copies a prepared .intunewin into the -o folder.</summary>
    private static string FakeIntuneWinAppUtil()
    {
        var folder = Path.Combine(_share, ".tools");
        var tool = Path.Combine(folder, "IntuneWinAppUtil.cmd");
        if (File.Exists(tool)) return tool;

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
        // Arguments arrive as -c <source> -s <setup> -o <output> -q.
        File.WriteAllText(tool, "@copy /y \"%~dp0fake.intunewin\" \"%~6\\Invoke-AppDeployToolkit.intunewin\" >nul\r\n");
        return tool;
    }

    private static string LogPath(string kind) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packman", "Logs", kind, $"{AppName}-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>
    /// Answers the Graph and Azure Storage calls an upload and the detail page make. Apps
    /// ending in "deleted-app" are gone from Intune and return 404.
    /// </summary>
    private sealed class FakeIntune : HttpMessageHandler
    {
        public const string CreatedAppId = "created-app";
        public const string ContentVersionId = "cv-1";
        private readonly HashSet<string> _committedFiles = new();

        /// <summary>Bodies of the updateRelationships calls, one per call.</summary>
        public List<string> RelationshipBodies { get; } = new();

        /// <summary>A new upload starts with no file committed, as at a fresh Intune content version.</summary>
        public void ForgetCommittedFiles()
        {
            lock (_committedFiles) _committedFiles.Clear();
        }

        /// <summary>How many same-named apps the app list query returns.</summary>
        public int ListedAppCount { get; set; }

        /// <summary>Answers updateRelationships with Intune's too-large error.</summary>
        public bool RejectRelationships { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var path = request.RequestUri.AbsolutePath;
            var method = request.Method.Method;

            if (request.RequestUri.Host == "fake.blob.core.windows.net")
                return Json(HttpStatusCode.Created, "");

            if (path.EndsWith("/deviceManagement/reports/getAppStatusOverviewReport"))
                return Json(HttpStatusCode.NotFound, "{}");
            if (method == "POST" && path.EndsWith("/mobileApps"))
                return Json(HttpStatusCode.Created, $"{{\"id\":\"{CreatedAppId}\"}}");
            if (method == "POST" && path.EndsWith("/contentVersions"))
                return Json(HttpStatusCode.Created, $"{{\"id\":\"{ContentVersionId}\"}}");
            if (method == "POST" && path.EndsWith("/files"))
                return Json(HttpStatusCode.Created, "{\"id\":\"file-1\"}");
            if (method == "POST" && path.EndsWith("/commit"))
            {
                lock (_committedFiles) _committedFiles.Add(path[..^"/commit".Length]);
                return Json(HttpStatusCode.OK, "{}");
            }
            if (method == "GET" && path.Contains("/files/"))
            {
                bool committed;
                lock (_committedFiles) committed = _committedFiles.Contains(path);
                return Json(HttpStatusCode.OK, committed
                    ? "{\"uploadState\":\"commitFileSuccess\"}"
                    : "{\"uploadState\":\"azureStorageUriRequestSuccess\",\"azureStorageUri\":\"https://fake.blob.core.windows.net/c/f?sig=1\"}");
            }
            if (method == "POST" && path.EndsWith("/updateRelationships"))
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                lock (RelationshipBodies) RelationshipBodies.Add(body);
                return RejectRelationships
                    ? Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"BadRequest\",\"message\":\"The supersedence graph is too large.\"}}")
                    : Json(HttpStatusCode.NoContent, "");
            }
            if (method == "PATCH")
                return Json(HttpStatusCode.NoContent, "");
            if (method == "GET" && path.EndsWith("/assignments"))
                return Json(HttpStatusCode.OK, "{\"value\":[]}");
            if (method == "GET" && path.EndsWith("/mobileApps"))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    value = Enumerable.Range(0, ListedAppCount).Select(i => new Dictionary<string, object>
                    {
                        ["id"] = $"listed-{i}",
                        ["displayName"] = "Contoso Reader",
                        ["displayVersion"] = $"1.{i}",
                        ["publisher"] = "Contoso",
                        ["publishingState"] = "published",
                    }),
                }));
            if (method == "GET" && path.Contains("/mobileApps/"))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                if (id.EndsWith("deleted-app"))
                    return Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"Not found\"}}");
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["displayName"] = $"Contoso Reader {id}",
                    ["displayVersion"] = "1.0",
                    ["publisher"] = "Contoso",
                    ["publishingState"] = "published",
                }));
            }

            await Task.CompletedTask;
            throw new InvalidOperationException($"Unexpected request: {method} {url}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    // ── Assertions ──

    private static void Run(string name, Action check)
    {
        try
        {
            check();
            _passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"FAIL  {name}: {ex}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected <{expected}>, got <{actual}>");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
