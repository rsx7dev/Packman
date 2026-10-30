#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:project ../Packman/Packman.csproj

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Packman;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;
using Packman.Views.Controls;

// Offline checks for the Devices section of Application detail: report parsing and paging,
// error text, CSV export, the view model, and the real view laid out from report JSON.
// No window is shown and nothing signs in or calls Graph. Windows only (WPF).
//   dotnet run --file tests/DeviceStatusTests.cs -c Release
// Exits non-zero when a check fails.
internal static partial class DeviceStatusTests
{
    [STAThread]
    public static int Main()
    {
        // Logic checks run on the thread pool, so their awaits never wait on a dispatcher.
        foreach (var (name, check) in PortableChecks.Concat(LogicChecks))
            Run(name, () => Task.Run(check).GetAwaiter().GetResult());
        // WPF needs this STA thread.
        Run("Devices section lays out rows, chips, detail and the empty state", RenderChecks);
        Run("Hiding the left nav centers the page area; with the nav shown it fills the space", NavHiddenCentersPages);
        Run("Upload to Intune page shows publish progress in the right rail", UploadPageProgressInRail);
        Run("Create wizard review step shows publish progress in the right rail", WizardProgressInRail);

        return ReportResult();
    }

    private static readonly (string Name, Func<Task> Check)[] LogicChecks =
    [
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
        EnsureApp();

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

    private static void EnsureApp()
    {
        if (Application.Current != null) return;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
    }

    // ── Layout: centered pages and publish progress in the rail ──

    private static void NavHiddenCentersPages()
    {
        EnsureApp();
        var (root, main) = WindowContent(new MainWindow());
        var wasHidden = main.IsNavHidden;   // the setter saves the setting; put it back
        try
        {
            main.IsNavHidden = false;
            var (shown, shownRoot) = PagesBounds(root);
            main.IsNavHidden = true;
            var (hidden, hiddenRoot) = PagesBounds(root);

            True(shown.Left > 12, $"with the nav shown the pages start right of it (left {shown.Left})");
            True(shown.Width > 1240, $"with the nav shown the pages fill the space (width {shown.Width})");
            True(Math.Abs(shown.Right - (shownRoot - 12)) < 1, $"with the nav shown the right edge keeps its margin (right {shown.Right} of {shownRoot})");

            True(hidden.Width <= 1240.5, $"with the nav hidden the pages stop at 1240 (width {hidden.Width})");
            True(Math.Abs(hidden.Left - (hiddenRoot - hidden.Right)) < 1, $"centered: {hidden.Left} left, {hiddenRoot - hidden.Right} right");
        }
        finally
        {
            main.IsNavHidden = wasHidden;
        }
    }

    /// <summary>The page area's bounds in the window's content, and that content's width.</summary>
    private static (Rect Bounds, double RootWidth) PagesBounds(FrameworkElement root)
    {
        var host = (FrameworkElement)root.FindName("PagesHost");
        LayOut(root);
        return (host.TransformToAncestor(root).TransformBounds(new Rect(host.RenderSize)), root.ActualWidth);
    }

    /// <summary>
    /// A window that is never shown does not lay out its content, so the content is lifted out
    /// and laid out on its own at a fixed size, with the window's view model.
    /// </summary>
    private static (FrameworkElement Root, MainViewModel Main) WindowContent(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Width = 1800;
        root.Height = 900;
        var main = (MainViewModel)window.DataContext;
        root.DataContext = main;
        return (root, main);
    }

    private static void UploadPageProgressInRail()
    {
        EnsureApp();
        var view = new UploadToIntuneView();
        AssertProgressInRail(view, view.ViewModel.Publish);
    }

    private static void WizardProgressInRail()
    {
        EnsureApp();
        var (root, main) = WindowContent(new MainWindow());
        main.CurrentStepIndex = 2;
        AssertProgressInRail(root, main.Upload.Publish);
    }

    private static void AssertProgressInRail(FrameworkElement host, PublishRunViewModel publish)
    {
        LayOut(host);
        Equal(0, Shown<PublishStatusControl>(host).Count());

        // The work never finishes, so the run stays in its publishing state.
        _ = publish.RunAsync("Publishing Contoso Reader", "Contoso", (_, _) => new TaskCompletionSource<string>().Task,
            id => id, () => "Cancelled.");
        LayOut(host);

        var progress = Shown<PublishStatusControl>(host).ToList();
        Equal(1, progress.Count);
        var frame = Frames(host).First(f => progress[0].IsDescendantOf(f));
        var rail = (Border)frame.Template.FindName("rail", frame);
        True(progress[0].IsDescendantOf(rail), "publish progress sits in the right rail");
        True(Shown<TextBlock>(progress[0]).Any(t => t.Text == "Publishing Contoso Reader"), "the rail names the running publish");
    }

    private static void LayOut(FrameworkElement host)
    {
        var size = host.Width > 0 ? new Size(host.Width, host.Height) : new Size(1320, 900);
        host.Measure(size);
        host.Arrange(new Rect(new Point(), size));
        host.UpdateLayout();
    }

    private static IEnumerable<PageFrame> Frames(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is PageFrame frame) yield return frame;
            foreach (var nested in Frames(child)) yield return nested;
        }
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

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new CheckFailed($"timed out: {what}");
            await Task.Delay(10);
        }
    }

}
