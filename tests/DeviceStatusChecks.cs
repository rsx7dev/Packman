using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;

// Shared by the portable unit harness and the Windows integration harness.
internal static partial class DeviceStatusTests
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static readonly (string Name, Func<Task> Check)[] PortableChecks =
    [
        ("Report columns are read by name from the schema", ReadsColumnsByName),
        ("A reordered schema reads the same fields", ReorderedSchema),
        ("Detail text puts the report text first; unknown codes show only their value", DetailText),
        ("Paging requests skip offsets until TotalRowCount is read", PagesUntilTotal),
        ("Without a total, paging stops at a short page", PagesWithoutTotal),
        ("An empty report is one request and no rows", EmptyReport),
        ("Documented codes have their published text; others none", ErrorTexts),
        ("CSV fields are quoted per RFC 4180", CsvQuoting),
        ("Formula-like CSV fields get an apostrophe", CsvFormulaGuard),
        ("CSV writer emits UTF-8 with BOM and keeps accents", CsvWriterAccents),
        ("Share scan finds packages by script or .intunewin, newest first, and changes nothing", ShareScan),
        ("Relationships read as supersedes, superseded by, depends on and required by", ParsesRelationships),
        ("Catalog lists every Intune app ID, joins share packages and keeps unclaimed ones", CatalogMatches),
    ];

    private static int ReportResult()
    {
        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── Report parsing and paging ──

    private static Task ReadsColumnsByName()
    {
        var rows = IntuneService.ParseDeviceInstallStatusPage(Fixture("device-status-page1.json"));
        Equal(2, rows.Count);
        var installed = rows[0];
        Equal("LAB-PC-01", installed.DeviceName);
        Equal("adele.vance@contoso.com", installed.UserPrincipalName);
        Equal(DeviceInstallState.Installed, installed.State);
        Equal(0, installed.ErrorCode);
        Equal("", installed.HexErrorCode);
        Equal(new DateTime(2026, 10, 20, 8, 15, 0, DateTimeKind.Utc), installed.LastModified);

        var failed = rows[1];
        Equal(DeviceInstallState.Failed, failed.State);
        Equal("The application was not detected after installation completed successfully", failed.StateDetail);
        Equal(-2016345060, failed.ErrorCode);
        Equal("0x87D1041C", failed.HexErrorCode);
        Equal("", failed.OsVersion);
        return Task.CompletedTask;
    }

    private static Task ReorderedSchema()
    {
        var rows = IntuneService.ParseDeviceInstallStatusPage(Fixture("device-status-reordered.json"));
        Equal(3, rows.Count);
        var msi = rows[0];
        Equal("SALES-LT-07", msi.DeviceName);
        Equal("megan.bowen@contoso.com", msi.UserPrincipalName);
        Equal(DeviceInstallState.Failed, msi.State);
        Equal("Installer exited with \"1603\", rolled back", msi.StateDetail);
        Equal(1603, msi.ErrorCode);
        Equal("0x00000643", msi.HexErrorCode);
        Equal("10.0.22631.4317", msi.OsVersion);
        Equal(new DateTime(2026, 10, 23, 7, 45, 0, DateTimeKind.Utc), msi.LastModified);

        // ErrorCode as an unsigned decimal still reads as the signed 32-bit code.
        Equal(unchecked((int)0x87D30065), rows[1].ErrorCode);
        Equal("0x87D30065", rows[1].HexErrorCode);

        Equal(DeviceInstallState.NotApplicable, rows[2].State);
        Equal("Not applicable for this device", rows[2].StateDetail);
        return Task.CompletedTask;
    }

    private static Task DetailText()
    {
        var rows = IntuneService.ParseDeviceInstallStatusPage(Fixture("device-status-reordered.json"));
        Equal("Installer exited with \"1603\", rolled back · 0x00000643 (1603) · A fatal error occurred during installation.", rows[0].DetailText);
        Equal("", rows[1].ErrorDescription);
        Equal("0x87D30065 (-2016214939)", rows[1].DetailText);
        return Task.CompletedTask;
    }

    private static async Task PagesUntilTotal()
    {
        var skips = new List<int>();
        var reports = new List<(int Read, int Total)>();
        var rows = await IntuneService.ReadDeviceInstallStatusAsync(skip =>
        {
            skips.Add(skip);
            return Task.FromResult(Fixture(skip == 0 ? "device-status-page1.json" : "device-status-page2.json"));
        }, pageSize: 2, new SyncProgress(reports.Add));

        Sequence(new[] { 0, 2 }, skips);
        Sequence(new[] { "LAB-PC-01", "LAB-PC-02", "KIOSK-ZOË" }, rows.Select(r => r.DeviceName));
        Sequence(new[] { (2, 3), (3, 3) }, reports);
    }

    private static async Task PagesWithoutTotal()
    {
        var page1 = WithoutTotal(Fixture("device-status-page1.json"));
        var page2 = WithoutTotal(Fixture("device-status-page2.json"));
        var skips = new List<int>();
        var rows = await IntuneService.ReadDeviceInstallStatusAsync(skip =>
        {
            skips.Add(skip);
            return Task.FromResult(skip == 0 ? page1 : page2);
        }, pageSize: 2);

        Sequence(new[] { 0, 2 }, skips);
        Equal(3, rows.Count);
    }

    private static async Task EmptyReport()
    {
        var calls = 0;
        var rows = await IntuneService.ReadDeviceInstallStatusAsync(_ =>
        {
            calls++;
            return Task.FromResult(Fixture("device-status-empty.json"));
        }, pageSize: 50);

        Equal(0, rows.Count);
        Equal(1, calls);
    }

    private static Task ErrorTexts()
    {
        Equal("A fatal error occurred during installation.", InstallErrorText.Describe(1603));
        Equal("A fatal error occurred during installation.", InstallErrorText.Describe(unchecked((int)0x80070643)));
        Equal("A restart is required to complete the install. This message indicates success.", InstallErrorText.Describe(3010));
        Equal("Access is denied.", InstallErrorText.Describe(5));
        Equal("Application installation succeeded but application is not detected.", InstallErrorText.Describe(unchecked((int)0x87D1041C)));
        Equal(null, InstallErrorText.Describe(unchecked((int)0x87D30065)));
        Equal(null, InstallErrorText.Describe(42424));
        return Task.CompletedTask;
    }

    // ── CSV ──

    private static Task CsvQuoting()
    {
        Equal("plain", CsvWriter.Field("plain"));
        Equal("\"a,b\"", CsvWriter.Field("a,b"));
        Equal("\"say \"\"hi\"\"\"", CsvWriter.Field("say \"hi\""));
        Equal("\"two\nlines\"", CsvWriter.Field("two\nlines"));
        Equal("\"two\r\nlines\"", CsvWriter.Field("two\r\nlines"));
        Equal("", CsvWriter.Field(""));
        Equal("a,b\r\n\"c,d\",e\r\n", CsvWriter.Build(new[] { new[] { "a", "b" }, new[] { "c,d", "e" } }));
        return Task.CompletedTask;
    }

    private static Task CsvFormulaGuard()
    {
        Equal("\"'=HYPERLINK(\"\"http://x\"\")\"", CsvWriter.Field("=HYPERLINK(\"http://x\")"));
        Equal("'+1", CsvWriter.Field("+1"));
        Equal("'-2016345060", CsvWriter.Field("-2016345060"));
        Equal("'@SUM(A1)", CsvWriter.Field("@SUM(A1)"));
        Equal("'\tcmd", CsvWriter.Field("\tcmd"));
        Equal("\"'=1,2\"", CsvWriter.Field("=1,2"));
        Equal("a=b", CsvWriter.Field("a=b"));
        return Task.CompletedTask;
    }

    private static Task ShareScan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"packman-share-{Guid.NewGuid():N}");
        try
        {
            Touch(root, "Contoso_Reader", "1.9", "Invoke-AppDeployToolkit.ps1");
            Touch(root, "Contoso_Reader", "1.10", "Application", "Invoke-AppDeployToolkit.ps1");
            Touch(root, "Contoso_Reader", "1.10", "Intune", "Reader.intunewin");
            Touch(root, "Fabrikam_Zip", "2.0", "Intune", "Zip.intunewin");
            Touch(root, "Fabrikam_Zip", "scratch", "notes.txt");
            Touch(root, "stray.txt");
            var before = Snapshot(root);

            var found = ApplicationShareScanner.Scan(root);

            Sequence(new[] { "Contoso_Reader 1.10", "Contoso_Reader 1.9", "Fabrikam_Zip 2.0" },
                found.Select(p => $"{p.AppFolderName} {p.Version}").ToArray());
            True(found[0].HasScript && found[0].HasIntuneWin, "1.10 has both the script and the .intunewin");
            True(found[2].HasIntuneWin && !found[2].HasScript, "Zip 2.0 has only the .intunewin");
            Sequence(before, Snapshot(root));
            Equal(0, ApplicationShareScanner.Scan(Path.Combine(root, "missing")).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Task ParsesRelationships()
    {
        using var doc = JsonDocument.Parse("""
            {"value":[
              {"@odata.type":"#microsoft.graph.mobileAppSupersedence","targetId":"old-1","targetType":"child","supersedenceType":"update"},
              {"@odata.type":"#microsoft.graph.mobileAppSupersedence","targetId":"new-1","targetType":"parent"},
              {"@odata.type":"#microsoft.graph.mobileAppDependency","targetId":"runtime-1","targetType":"child","dependencyType":"autoInstall"},
              {"@odata.type":"#microsoft.graph.mobileAppDependency","targetId":"plugin-1","targetType":"parent"},
              {"@odata.type":"#microsoft.graph.mobileAppDependency","targetId":"me","targetType":"child"},
              {"@odata.type":"#microsoft.graph.mobileAppRelationship","targetId":"other","targetType":"child"}
            ]}
            """);
        var relations = AppCatalog.ParseRelationships(doc.RootElement, "me");
        Sequence(new[] { "old-1" }, relations.Supersedes.ToArray());
        Sequence(new[] { "new-1" }, relations.SupersededBy.ToArray());
        Sequence(new[] { "runtime-1" }, relations.DependsOn.ToArray());
        Sequence(new[] { "plugin-1" }, relations.RequiredBy.ToArray());
        return Task.CompletedTask;
    }

    private static Task CatalogMatches()
    {
        var packages = new[]
        {
            new SharePackage("Contoso_Reader", "2.0", @"\\share\Contoso_Reader\2.0", true, true),
            new SharePackage("Fabrikam_Zip", "3.1", @"\\share\Fabrikam_Zip\3.1", false, true),
        };
        var apps = new[]
        {
            new IntuneApplication { Id = "reader-2", Publisher = "Contoso", DisplayName = "Reader", Version = "2.0", LastModified = new DateTime(2026, 2, 1) },
            new IntuneApplication { Id = "reader-2-old", Publisher = "Contoso", DisplayName = "Reader", Version = "2.0", LastModified = new DateTime(2026, 1, 1) },
            new IntuneApplication { Id = "reader-1", Publisher = "Contoso", DisplayName = "Reader", Version = "1.0" },
        };
        var relations = new Dictionary<string, AppRelations>
        {
            ["reader-2"] = new(["reader-1"], [], ["runtime-1"], []),
        };

        var entries = AppCatalog.Build(packages, apps, relations);

        // Every Intune app is listed; the share package belongs to the newest app that matches it,
        // and the Zip folder, which no app claims, comes last without an ID.
        Sequence(new string?[] { "reader-2", "reader-2-old", "reader-1", null }, entries.Select(e => e.AppId).ToArray());
        Equal("2.0", entries[0].Package?.Version);
        Equal(null, entries[1].Package);
        Equal(null, entries[2].Package);
        Equal("Fabrikam_Zip", entries[3].Package?.AppFolderName);
        Sequence(new[] { "reader-1" }, entries[0].Relations.Supersedes.ToArray());
        Sequence(new[] { "runtime-1" }, entries[0].Relations.DependsOn.ToArray());
        Equal(0, entries[1].Relations.Supersedes.Count);

        using var json = JsonDocument.Parse(AppCatalog.ToJson(@"\\share", entries));
        var first = json.RootElement.GetProperty("apps")[0];
        Equal("reader-2", first.GetProperty("appId").GetString());
        Equal("reader-1", first.GetProperty("relations").GetProperty("supersedes")[0].GetString());
        Equal("runtime-1", first.GetProperty("relations").GetProperty("dependsOn")[0].GetString());
        Equal(JsonValueKind.Null, json.RootElement.GetProperty("apps")[3].GetProperty("appId").ValueKind);
        Equal(JsonValueKind.Null, json.RootElement.GetProperty("apps")[2].GetProperty("package").ValueKind);
        return Task.CompletedTask;
    }

    private static void Touch(string root, params string[] parts)
    {
        var file = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
    }

    private static string[] Snapshot(string root) => Directory
        .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(e => $"{e} {File.GetLastWriteTimeUtc(e).Ticks}").Order().ToArray();

    private static Task CsvWriterAccents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"packman-csv-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, CsvWriter.Build(new[] { new[] { "KIOSK-ZOË", "André" } }), CsvWriter.Encoding);
            var bytes = File.ReadAllBytes(path);
            Sequence(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Equal("KIOSK-ZOË,André\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private static JsonElement Fixture(string name) =>
        JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(FixtureDirectory(), name)));

    private static string FixtureDirectory([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "fixtures");

    private static JsonElement WithoutTotal(JsonElement page)
    {
        var copy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(page.GetRawText())!;
        copy.Remove("TotalRowCount");
        return JsonSerializer.SerializeToElement(copy);
    }

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
            throw new CheckFailed($"expected <{expected}>, got <{actual}>");
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new CheckFailed($"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new CheckFailed($"not true: {what}");
    }

    private sealed class CheckFailed(string message) : Exception(message);

    /// <summary>Progress that reports in line, so the checks see every report.</summary>
    private sealed class SyncProgress(Action<(int Read, int Total)> report) : IProgress<(int Read, int Total)>
    {
        public void Report((int Read, int Total) value) => report(value);
    }
}
