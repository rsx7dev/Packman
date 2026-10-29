#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:project ../Packman/Packman.csproj

using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Packman;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;

// Offline checks for the Devices section of Application detail: report parsing and paging,
// error text, CSV export, the view model, and the real view laid out from report JSON.
// No window is shown and nothing signs in or calls Graph. Windows only (WPF).
//   dotnet run --file tests/DeviceStatusTests.cs -c Release
// Exits non-zero when a check fails.
internal static class DeviceStatusTests
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    [STAThread]
    public static int Main()
    {
        // Logic checks run on the thread pool, so their awaits never wait on a dispatcher.
        foreach (var (name, check) in LogicChecks)
            Run(name, () => Task.Run(check).GetAwaiter().GetResult());
        // WPF needs this STA thread.
        Run("Devices section lays out rows, chips, detail and the empty state", RenderChecks);

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    private static readonly (string Name, Func<Task> Check)[] LogicChecks =
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
        ("CSV export is UTF-8 with BOM and keeps accents", CsvAccents),
        ("Chips count every status and filter the list", ChipsAndFilter),
        ("Search matches device or user", Search),
        ("Selection survives a filter that keeps the row", Selection),
        ("Export writes only the filtered rows", ExportFiltered),
        ("An empty report shows the empty state", EmptyState),
        ("A failed load shows the error and no rows", FailedLoad),
        ("A slow first request shows the dock, and Cancel stops the load", SlowFirstRequest),
        ("A multi-page load shows the dock at once and hides it when done", MultiPageDock),
        ("A fast load never shows the dock", FastLoadNoDock),
    ];

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

    private static async Task CsvAccents()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json");
        var path = Path.Combine(Path.GetTempPath(), $"packman-devices-{Guid.NewGuid():N}.csv");
        try
        {
            vm.ExportCsv(path);
            var bytes = File.ReadAllBytes(path);
            Sequence(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            True(text.Contains("KIOSK-ZOË,zoe.muller@contoso.com,Pending,Download in progress"), "export keeps 'KIOSK-ZOË'");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Devices section view model ──

    private static async Task ChipsAndFilter()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");
        Equal("All 6", vm.AllChip);
        Equal("Installed 1", vm.InstalledChip);
        Equal("Failed 3", vm.FailedChip);
        Equal("Pending 1", vm.PendingChip);
        Equal("Not installed 0", vm.NotInstalledChip);
        Equal("Not applicable 1", vm.NotApplicableChip);
        Equal(6, vm.Rows.Count);

        vm.Filter = "failed";
        Sequence(new[] { "LAB-PC-02", "SALES-LT-07", "SALES-LT-08" }, vm.Rows.Select(r => r.DeviceName));
        Equal("Showing 3 of 6 devices", vm.CountText);

        vm.Filter = "notinstalled";
        Equal(0, vm.Rows.Count);
        True(vm.HasNoMatches, "no-matches state");
        True(!vm.CanExport, "export disabled with no rows");
    }

    private static async Task Search()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");
        vm.Search = "lab-pc";
        Sequence(new[] { "LAB-PC-01", "LAB-PC-02" }, vm.Rows.Select(r => r.DeviceName));
        vm.Search = "megan";
        Sequence(new[] { "SALES-LT-07" }, vm.Rows.Select(r => r.DeviceName));
        vm.Search = "contoso";
        vm.Filter = "notapplicable";
        Sequence(new[] { "SALES-LT-09" }, vm.Rows.Select(r => r.DeviceName));
    }

    private static async Task Selection()
    {
        var vm = await LoadedAsync("device-status-reordered.json");
        var row = vm.Rows.First(r => r.DeviceName == "SALES-LT-08");
        vm.Selected = row;
        vm.Filter = "failed";
        True(ReferenceEquals(row, vm.Selected), "selection kept");
        vm.Filter = "installed";
        Equal(null, vm.Selected);
        True(!vm.HasSelection, "selection cleared");
    }

    private static async Task ExportFiltered()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");
        vm.Filter = "failed";
        vm.Search = "sales";
        var lines = vm.BuildCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Equal(3, lines.Length);
        Equal("Device,User,Status,Status detail,Error code (hex),Error code,Error description,Last modified (UTC),OS version", lines[0]);
        Equal("SALES-LT-07,megan.bowen@contoso.com,Failed,\"Installer exited with \"\"1603\"\", rolled back\",0x00000643,1603," +
              "A fatal error occurred during installation.,2026-10-23 07:45:00,10.0.22631.4317", lines[1]);
        Equal("SALES-LT-08,alex.wilber@contoso.com,Failed,,0x87D30065,'-2016214939,,2026-10-24 12:00:00,10.0.26100.2033", lines[2]);
    }

    private static async Task EmptyState()
    {
        var vm = await LoadedAsync("device-status-empty.json");
        Equal("No devices have reported for this app yet.", vm.StateText);
        True(!vm.HasRows && !vm.HasNoMatches, "no rows, no no-matches note");
        Equal("", vm.ErrorText);
        Equal("All 0", vm.AllChip);
    }

    private static async Task FailedLoad()
    {
        var vm = new DeviceStatusViewModel((_, _) =>
            throw new GraphException("Device install status report", 403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Denied\"}}", null));
        await vm.LoadAsync();

        True(vm.ErrorText.StartsWith("Could not load device install status:") && vm.ErrorText.Contains("Denied"), $"error text: {vm.ErrorText}");
        Equal("", vm.StateText);
        True(!vm.HasRows && !vm.IsLoading, "no rows, not loading");
    }

    private static async Task SlowFirstRequest()
    {
        // The first request never answers on its own: no page, no progress, no total.
        var vm = new DeviceStatusViewModel(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new List<DeviceInstallStatus>();
        }) { DockDelay = TimeSpan.FromMilliseconds(50) };

        var load = vm.LoadAsync();
        True(!vm.IsDockActive, "dock hidden at the start");
        await WaitUntil(() => vm.IsDockActive, "dock shown while the first request waits");
        True(vm.IsDockIndeterminate, "no progress known yet");
        Equal("Waiting for Intune…", vm.DockPhase);
        True(vm.CancelCommand.CanExecute(null), "Cancel available");

        vm.CancelCommand.Execute(null);
        await load;
        True(!vm.IsLoading && !vm.IsDockActive, "load stopped and dock hidden");
        True(vm.ErrorText.Contains("cancelled"), $"error text: {vm.ErrorText}");
    }

    private static async Task MultiPageDock()
    {
        var dockSeen = new List<bool>();
        DeviceStatusViewModel? vm = null;
        vm = new DeviceStatusViewModel((progress, ct) => IntuneService.ReadDeviceInstallStatusAsync(skip =>
        {
            dockSeen.Add(vm!.IsDockActive);
            return Task.FromResult(Fixture(skip == 0 ? "device-status-page1.json" : "device-status-page2.json"));
        }, pageSize: 2, progress, ct)) { DockDelay = TimeSpan.FromHours(1) };

        await vm.LoadAsync();
        Sequence(new[] { false, true }, dockSeen);
        True(!vm.IsDockActive, "dock hidden when done");
        Equal(3, vm.Rows.Count);
    }

    private static async Task FastLoadNoDock()
    {
        var vm = new DeviceStatusViewModel((_, _) => Task.FromResult(new List<DeviceInstallStatus>())) { DockDelay = TimeSpan.FromMilliseconds(20) };
        await vm.LoadAsync();
        await Task.Delay(100);
        True(!vm.IsDockActive, "a finished load does not show the dock later");
    }

    // ── The real view, laid out without a window ──

    private static void RenderChecks()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();

        var populated = Detail("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");
        var view = Layout(populated);

        Equal(6, Shown<ListBoxItem>(view).Count());
        var chips = Shown<RadioButton>(view).Select(r => r.Content as string).ToList();
        True(chips.Contains("Failed 3") && chips.Contains("Not applicable 1"), "status chips with counts");
        True(Shown<Button>(view).Any(b => AutomationProperties.GetName(b) == "Export devices to CSV" && b.IsEnabled), "export button enabled");
        True(Shown<Button>(view).Any(b => AutomationProperties.GetName(b) == "Refresh devices"), "refresh button named");
        True(Shown<TextBox>(view).Any(t => AutomationProperties.GetName(t) == "Search devices or users"), "search box named");
        True(!Shown<TextBlock>(view).Any(t => t.Text == DeviceStatusViewModel.EmptyText), "no empty state with rows");

        populated.Devices.Selected = populated.Devices.Rows.First(r => r.DeviceName == "SALES-LT-08");
        view.UpdateLayout();
        True(Shown<TextBlock>(view).Any(t => t.Text == "0x87D30065 (-2016214939)"), "selected device detail shows the hex code");

        populated.Devices.Filter = "failed";
        view.UpdateLayout();
        Equal(3, Shown<ListBoxItem>(view).Count());

        var empty = Layout(Detail("device-status-empty.json"));
        True(Shown<TextBlock>(empty).Any(t => t.Text == "No devices have reported for this app yet."), "empty state shown");
        Equal(0, Shown<ListBoxItem>(empty).Count());
        True(Shown<Button>(empty).Any(b => AutomationProperties.GetName(b) == "Export devices to CSV" && !b.IsEnabled), "export disabled when empty");
    }

    private static ApplicationDetailViewModel Detail(params string[] fixtures)
    {
        var app = new IntuneApplication { Id = "sample-app", DisplayName = "Contoso Reader", Publisher = "Contoso", Version = "4.2.0" };
        var vm = new ApplicationDetailViewModel(app) { Devices = FixtureViewModel(fixtures) };
        vm.Devices.LoadAsync().GetAwaiter().GetResult();
        return vm;
    }

    private static ApplicationDetailView Layout(ApplicationDetailViewModel vm)
    {
        var view = new ApplicationDetailView { DataContext = vm };
        view.Measure(new Size(1320, 2400));
        view.Arrange(new Rect(0, 0, 1320, 2400));
        view.UpdateLayout();
        return view;
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

    // ── Fixtures and assertions ──

    private static DeviceStatusViewModel FixtureViewModel(string[] fixtures) =>
        new((_, _) => Task.FromResult(fixtures.SelectMany(f => IntuneService.ParseDeviceInstallStatusPage(Fixture(f))).ToList()));

    private static async Task<DeviceStatusViewModel> LoadedAsync(params string[] fixtures)
    {
        var vm = FixtureViewModel(fixtures);
        await vm.LoadAsync();
        return vm;
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

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new CheckFailed($"timed out: {what}");
            await Task.Delay(10);
        }
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
