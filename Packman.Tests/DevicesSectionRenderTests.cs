using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// Lays out the real Application detail view with the Devices section fed from report JSON.
/// No window is shown and nothing signs in or calls Graph.
/// </summary>
public class DevicesSectionRenderTests
{
    [Fact]
    public void Devices_section_lays_out_rows_chips_detail_and_the_empty_state()
    {
        RunSta(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();

            var populated = Detail("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");
            var view = Layout(populated);

            Assert.Equal(6, Shown<ListBoxItem>(view).Count());
            var chips = Shown<RadioButton>(view).Select(r => r.Content as string).ToList();
            Assert.Contains("Failed 3", chips);
            Assert.Contains("Not applicable 1", chips);
            Assert.Contains(Shown<Button>(view), b => AutomationName(b) == "Export devices to CSV" && b.IsEnabled);
            Assert.Contains(Shown<TextBox>(view), t => AutomationName(t) == "Search devices or users");
            Assert.DoesNotContain(Shown<TextBlock>(view), t => t.Text == DeviceStatusViewModel.EmptyText);

            populated.Devices.Selected = populated.Devices.Rows.First(r => r.DeviceName == "SALES-LT-08");
            view.UpdateLayout();
            Assert.Contains(Shown<TextBlock>(view), t => t.Text == "0x87D30065 (-2016214939)");

            populated.Devices.Filter = "failed";
            view.UpdateLayout();
            Assert.Equal(3, Shown<ListBoxItem>(view).Count());

            var empty = Layout(Detail("device-status-empty.json"));
            Assert.Contains(Shown<TextBlock>(empty), t => t.Text == "No devices have reported for this app yet.");
            Assert.Empty(Shown<ListBoxItem>(empty));
            Assert.Contains(Shown<Button>(empty), b => AutomationName(b) == "Export devices to CSV" && !b.IsEnabled);
        });
    }

    private static ApplicationDetailViewModel Detail(params string[] fixtures)
    {
        var app = new IntuneApplication { Id = "sample-app", DisplayName = "Contoso Reader", Publisher = "Contoso", Version = "4.2.0" };
        var vm = new ApplicationDetailViewModel(app)
        {
            Devices = new DeviceStatusViewModel((_, _) => Task.FromResult(fixtures
                .SelectMany(f => IntuneService.ParseDeviceInstallStatusPage(DeviceInstallStatusReportTests.Fixture(f))).ToList())),
        };
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

    private static string AutomationName(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

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

    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
