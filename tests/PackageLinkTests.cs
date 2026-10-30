#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Text.Json;
using Packman.Helpers;

// The packman.id link between a package folder and its Intune app: what an upload writes,
// a failed write, which folder the Application detail page resolves (its Republish content
// and Edit script actions are shown only for that folder), and the legacy .intune-appid.json
// fallback. Uses the production sources; no tenant, nothing calls Graph.
//   dotnet run --file tests/PackageLinkTests.cs -c Release
// Exits non-zero when a check fails.
internal static class PackageLinkTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "22222222-2222-2222-2222-222222222222";
    private const string LegacyJson = "{\n  \"IntuneAppId\": \"legacy-app\",\n  \"DisplayName\": \"Reader\",\n  \"Version\": \"1.0\",\n  \"Updated\": \"2026-01-01T00:00:00.0000000Z\"\n}";

    private static int _passed;
    private static readonly List<string> Failures = new();

    private static readonly (string Name, Func<Task> Check)[] Checks =
    [
        ("packman.id holds exactly the seven link fields", WritesLinkFields),
        ("The link is written to Intune\\, which is created when missing", WritesIntoIntuneFolder),
        ("Uploading to another app overwrites the link", OverwritesLink),
        ("A failed write returns a warning instead of throwing", FailedWriteWarns),
        ("A folder linked to the app in this tenant resolves, whatever its name", ResolvesLinkedFolder),
        ("A folder without a link does not resolve", MissingLinkHidden),
        ("A link from another tenant does not resolve", OtherTenantHidden),
        ("A link to another (deleted) app does not resolve for this app", OtherAppHidden),
        ("A packman.id without a tenantId is no link, not a legacy one", EmptyTenantRejected),
        ("The latest upload wins when several folders link the same app", LatestUploadWins),
        ("Without packman.id the legacy .intune-appid.json is read as a legacy link", LegacyFallback),
        ("packman.id takes precedence over the legacy file, which is never changed", PackmanIdWinsOverLegacy),
        ("A legacy link resolves on the app id alone", LegacyResolvesOnAppId),
        ("An upgrade uses a legacy predecessor only when the tenant has that app", LegacyPredecessorNeedsApp),
        ("An upgrade uses a packman.id predecessor without the existence check", PackmanIdPredecessor),
    ];

    public static int Main()
    {
        foreach (var (name, check) in Checks)
        {
            try
            {
                check().GetAwaiter().GetResult();
                _passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── Writing the link ──

    private static Task WritesLinkFields()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        Equal(null, PackageMarker.Save(package, NewLink("app-1", Tenant, "2026-10-30T12:00:00.0000000Z")));

        using var json = JsonDocument.Parse(File.ReadAllText(PackageMarker.PathFor(package)));
        var keys = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Equal("appId,tenantId,uploadedAt,displayName,displayVersion,intunewinFileName,unencryptedSize", string.Join(",", keys));

        var link = PackageMarker.Read(package)!;
        Equal("app-1", link.AppId);
        Equal(Tenant, link.TenantId);
        Equal("2026-10-30T12:00:00.0000000Z", link.UploadedAt);
        Equal("Contoso Reader 1.0", link.DisplayName);
        Equal("1.0", link.DisplayVersion);
        Equal("Invoke-AppDeployToolkit.intunewin", link.IntunewinFileName);
        Equal(123456L, link.UnencryptedSize);
        Equal(false, link.IsLegacy);
        return Task.CompletedTask;
    }

    private static Task WritesIntoIntuneFolder()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        Equal(false, Directory.Exists(Path.Combine(package, "Intune")));
        Equal(null, PackageMarker.Save(package, NewLink("app-1", Tenant)));
        Equal(true, File.Exists(Path.Combine(package, "Intune", PackageMarker.FileName)));
        Equal(false, File.Exists(Path.Combine(package, PackageMarker.FileName)));
        Equal(false, File.Exists(Path.Combine(package, "Application", PackageMarker.FileName)));
        return Task.CompletedTask;
    }

    private static Task OverwritesLink()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        PackageMarker.Save(package, NewLink("app-1", Tenant));
        Equal(null, PackageMarker.Save(package, NewLink("app-2", Tenant)));
        Equal("app-2", PackageMarker.Read(package)!.AppId);
        return Task.CompletedTask;
    }

    private static Task FailedWriteWarns()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        // A folder where the file should be makes the write fail on every platform, even as root.
        Directory.CreateDirectory(PackageMarker.PathFor(package));
        var warning = PackageMarker.Save(package, NewLink("app-1", Tenant));
        True(warning != null, "expected a warning");
        True(warning!.StartsWith("The upload succeeded"), warning);
        True(warning.Contains(PackageMarker.PathFor(package)), warning);
        return Task.CompletedTask;
    }

    // ── Which folder the detail page resolves ──

    private static Task ResolvesLinkedFolder()
    {
        // Default naming: the Intune display name "Contoso Reader 1.0" matches no folder name.
        var package = NewPackage(out var share, "Contoso_Reader", "1.0");
        NewPackage(share, "Contoso_Reader", "0.9");
        PackageMarker.Save(package, NewLink("app-1", Tenant));
        Equal(package, PackageSourceLocator.FindLinked(share, "app-1", Tenant));
        return Task.CompletedTask;
    }

    private static Task MissingLinkHidden()
    {
        NewPackage(out var share, "Contoso_Reader", "1.0");
        Equal(null, PackageSourceLocator.FindLinked(share, "app-1", Tenant));
        return Task.CompletedTask;
    }

    private static Task OtherTenantHidden()
    {
        var package = NewPackage(out var share, "Contoso_Reader", "1.0");
        PackageMarker.Save(package, NewLink("app-1", OtherTenant));
        Equal(null, PackageSourceLocator.FindLinked(share, "app-1", Tenant));
        Equal(null, PackageSourceLocator.FindLinked(share, "app-1", null));
        return Task.CompletedTask;
    }

    private static Task OtherAppHidden()
    {
        // The folder links a deleted app; the app opened in Packman (it exists in Intune) has another id.
        var package = NewPackage(out var share, "Contoso_Reader", "1.0");
        PackageMarker.Save(package, NewLink("deleted-app", Tenant));
        Equal(null, PackageSourceLocator.FindLinked(share, "app-1", Tenant));
        return Task.CompletedTask;
    }

    private static Task EmptyTenantRejected()
    {
        var package = NewPackage(out var share, "Contoso_Reader", "1.0");
        Directory.CreateDirectory(Path.Combine(package, "Intune"));
        File.WriteAllText(PackageMarker.PathFor(package), "{ \"appId\": \"app-1\" }");
        Equal(null, PackageMarker.Read(package));
        Equal(null, PackageSourceLocator.FindLinked(share, "app-1", Tenant));

        File.WriteAllText(PackageMarker.PathFor(package), "{ \"appId\": \"app-1\", \"tenantId\": \"\" }");
        Equal(null, PackageMarker.Read(package));
        return Task.CompletedTask;
    }

    private static Task LatestUploadWins()
    {
        var older = NewPackage(out var share, "Contoso_Reader", "1.0");
        var copy = NewPackage(share, "Contoso_Reader_Copy", "1.0");
        PackageMarker.Save(older, NewLink("app-1", Tenant, "2026-10-01T00:00:00.0000000Z"));
        PackageMarker.Save(copy, NewLink("app-1", Tenant, "2026-10-30T00:00:00.0000000Z"));
        Equal(copy, PackageSourceLocator.FindLinked(share, "app-1", Tenant));
        return Task.CompletedTask;
    }

    // ── Legacy .intune-appid.json ──

    private static Task LegacyFallback()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        File.WriteAllText(Path.Combine(package, ".intune-appid.json"), LegacyJson);
        var link = PackageMarker.Read(package)!;
        Equal("legacy-app", link.AppId);
        Equal("", link.TenantId);
        Equal(true, link.IsLegacy);
        Equal(LegacyJson, File.ReadAllText(Path.Combine(package, ".intune-appid.json")));
        Equal(false, Directory.Exists(Path.Combine(package, "Intune")));

        File.WriteAllText(Path.Combine(package, ".intune-appid.json"), "{ \"IntuneAppId\": \"\" }");
        Equal(null, PackageMarker.Read(package));
        return Task.CompletedTask;
    }

    private static Task PackmanIdWinsOverLegacy()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        File.WriteAllText(Path.Combine(package, ".intune-appid.json"), LegacyJson);
        PackageMarker.Save(package, NewLink("app-1", Tenant));
        var link = PackageMarker.Read(package)!;
        Equal("app-1", link.AppId);
        Equal(false, link.IsLegacy);
        Equal(LegacyJson, File.ReadAllText(Path.Combine(package, ".intune-appid.json")));
        return Task.CompletedTask;
    }

    private static Task LegacyResolvesOnAppId()
    {
        // The detail page has just loaded "legacy-app" from the connected tenant, so it exists there.
        var package = NewPackage(out var share, "Contoso_Reader", "1.0");
        File.WriteAllText(Path.Combine(package, ".intune-appid.json"), LegacyJson);
        Equal(package, PackageSourceLocator.FindLinked(share, "legacy-app", Tenant));
        Equal(null, PackageSourceLocator.FindLinked(share, "app-1", Tenant));
        return Task.CompletedTask;
    }

    private static async Task LegacyPredecessorNeedsApp()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        File.WriteAllText(Path.Combine(package, ".intune-appid.json"), LegacyJson);
        var asked = new List<string>();
        Equal("legacy-app", await PackageMarker.PredecessorAppIdAsync(package, id => { asked.Add(id); return Task.FromResult(true); }));
        Equal("", await PackageMarker.PredecessorAppIdAsync(package, id => { asked.Add(id); return Task.FromResult(false); }));
        Equal("legacy-app,legacy-app", string.Join(",", asked));
    }

    private static async Task PackmanIdPredecessor()
    {
        var package = NewPackage(out _, "Contoso_Reader", "1.0");
        PackageMarker.Save(package, NewLink("app-1", Tenant));
        var asked = false;
        Equal("app-1", await PackageMarker.PredecessorAppIdAsync(package, _ => { asked = true; return Task.FromResult(false); }));
        Equal(false, asked);
        Equal("", await PackageMarker.PredecessorAppIdAsync(NewPackage(out _, "Contoso_Reader", "1.0"), _ => Task.FromResult(true)));
    }

    // ── Helpers ──

    private static PackageMarker.Link NewLink(string appId, string tenantId, string uploadedAt = "2026-10-30T12:00:00.0000000Z") => new()
    {
        AppId = appId,
        TenantId = tenantId,
        UploadedAt = uploadedAt,
        DisplayName = "Contoso Reader 1.0",
        DisplayVersion = "1.0",
        IntunewinFileName = "Invoke-AppDeployToolkit.intunewin",
        UnencryptedSize = 123456,
    };

    /// <summary>A {share}\{app}\{version} package with an Application folder, in a fresh share.</summary>
    private static string NewPackage(out string share, string appFolder, string version)
    {
        share = Directory.CreateTempSubdirectory("packman-link-tests-").FullName;
        return NewPackage(share, appFolder, version);
    }

    private static string NewPackage(string share, string appFolder, string version)
    {
        var package = Path.Combine(share, appFolder, version);
        Directory.CreateDirectory(Path.Combine(package, "Application"));
        return package;
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
