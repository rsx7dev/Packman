using System.Text;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>The CSV writer, and the Devices section's list, filters, search and export over report JSON.</summary>
public class DeviceStatusExportTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    [InlineData("", "")]
    public void Fields_are_quoted_per_rfc_4180(string value, string expected) =>
        Assert.Equal(expected, CsvWriter.Field(value));

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-2016345060", "'-2016345060")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData("a=b", "a=b")]
    public void Formula_like_fields_get_an_apostrophe(string value, string expected) =>
        Assert.Equal(expected, CsvWriter.Field(value));

    [Fact]
    public void Rows_end_with_crlf()
    {
        Assert.Equal("a,b\r\n\"c,d\",e\r\n", CsvWriter.Build(new[] { new[] { "a", "b" }, new[] { "c,d", "e" } }));
    }

    [Fact]
    public async Task Export_is_utf8_with_bom_and_keeps_accents()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json");
        var path = Path.Combine(Path.GetTempPath(), $"packman-devices-{Guid.NewGuid():N}.csv");
        try
        {
            vm.ExportCsv(path);
            var bytes = File.ReadAllBytes(path);

            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.Contains("KIOSK-ZOË,zoe.muller@contoso.com,Pending,Download in progress", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Chips_count_every_status_and_filter_the_list()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");

        Assert.Equal("All 6", vm.AllChip);
        Assert.Equal("Installed 1", vm.InstalledChip);
        Assert.Equal("Failed 3", vm.FailedChip);
        Assert.Equal("Pending 1", vm.PendingChip);
        Assert.Equal("Not installed 0", vm.NotInstalledChip);
        Assert.Equal("Not applicable 1", vm.NotApplicableChip);
        Assert.Equal(6, vm.Rows.Count);

        vm.Filter = "failed";
        Assert.Equal(new[] { "LAB-PC-02", "SALES-LT-07", "SALES-LT-08" }, vm.Rows.Select(r => r.DeviceName));
        Assert.Equal("Showing 3 of 6 devices", vm.CountText);

        vm.Filter = "notinstalled";
        Assert.Empty(vm.Rows);
        Assert.True(vm.HasNoMatches);
        Assert.False(vm.CanExport);
    }

    [Fact]
    public async Task Search_matches_device_or_user()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");

        vm.Search = "lab-pc";
        Assert.Equal(new[] { "LAB-PC-01", "LAB-PC-02" }, vm.Rows.Select(r => r.DeviceName));

        vm.Search = "megan";
        Assert.Equal(new[] { "SALES-LT-07" }, vm.Rows.Select(r => r.DeviceName));

        vm.Search = "contoso";
        vm.Filter = "notapplicable";
        Assert.Equal(new[] { "SALES-LT-09" }, vm.Rows.Select(r => r.DeviceName));
    }

    [Fact]
    public async Task Selection_survives_a_filter_that_keeps_the_row()
    {
        var vm = await LoadedAsync("device-status-reordered.json");
        vm.Selected = vm.Rows.First(r => r.DeviceName == "SALES-LT-08");

        vm.Filter = "failed";
        Assert.Same(vm.Rows.First(r => r.DeviceName == "SALES-LT-08"), vm.Selected);

        vm.Filter = "installed";
        Assert.Null(vm.Selected);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task Export_writes_only_the_filtered_rows()
    {
        var vm = await LoadedAsync("device-status-page1.json", "device-status-page2.json", "device-status-reordered.json");
        vm.Filter = "failed";
        vm.Search = "sales";

        var lines = vm.BuildCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal("Device,User,Status,Status detail,Error code (hex),Error code,Error description,Last modified (UTC),OS version", lines[0]);
        Assert.Equal("SALES-LT-07,megan.bowen@contoso.com,Failed,\"Installer exited with \"\"1603\"\", rolled back\",0x00000643,1603," +
                     "A fatal error occurred during installation.,2026-10-23 07:45:00,10.0.22631.4317", lines[1]);
        Assert.Equal("SALES-LT-08,alex.wilber@contoso.com,Failed,,0x87D30065,'-2016214939,,2026-10-24 12:00:00,10.0.26100.2033", lines[2]);
    }

    [Fact]
    public async Task An_empty_report_shows_the_empty_state()
    {
        var vm = await LoadedAsync("device-status-empty.json");

        Assert.Equal(DeviceStatusViewModel.EmptyText, vm.StateText);
        Assert.Equal("No devices have reported for this app yet.", vm.StateText);
        Assert.False(vm.HasRows);
        Assert.False(vm.HasNoMatches);
        Assert.Equal("", vm.ErrorText);
        Assert.Equal("All 0", vm.AllChip);
    }

    [Fact]
    public async Task A_failed_load_shows_the_error_and_no_rows()
    {
        var vm = new DeviceStatusViewModel((_, _) => throw new GraphException("Device install status report", 403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Denied\"}}", null));
        await vm.LoadAsync();

        Assert.StartsWith("Could not load device install status:", vm.ErrorText);
        Assert.Contains("Denied", vm.ErrorText);
        Assert.Equal("", vm.StateText);
        Assert.False(vm.HasRows);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Cancel_stops_the_load_and_says_so()
    {
        var started = new TaskCompletionSource();
        var vm = new DeviceStatusViewModel(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new List<DeviceInstallStatus>();
        });

        var load = vm.LoadAsync();
        await started.Task;
        Assert.True(vm.IsLoading);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await load;

        Assert.False(vm.IsLoading);
        Assert.Contains("cancelled", vm.ErrorText);
    }

    [Fact]
    public async Task The_dock_shows_only_while_a_multi_page_load_runs()
    {
        var dockSeen = new List<bool>();
        DeviceStatusViewModel? vm = null;
        vm = new DeviceStatusViewModel((progress, ct) => IntuneService.ReadDeviceInstallStatusAsync(skip =>
        {
            dockSeen.Add(vm!.IsDockActive);
            return Task.FromResult(DeviceInstallStatusReportTests.Fixture(skip == 0 ? "device-status-page1.json" : "device-status-page2.json"));
        }, pageSize: 2, progress, ct));

        await vm.LoadAsync();

        Assert.Equal(new[] { false, true }, dockSeen);
        Assert.False(vm.IsDockActive);
        Assert.Equal(3, vm.Rows.Count);
    }

    /// <summary>A view model loaded from fixture pages, read one after another as the report would page.</summary>
    private static async Task<DeviceStatusViewModel> LoadedAsync(params string[] fixtures)
    {
        var vm = new DeviceStatusViewModel((_, _) => Task.FromResult(
            fixtures.SelectMany(f => IntuneService.ParseDeviceInstallStatusPage(DeviceInstallStatusReportTests.Fixture(f))).ToList()));
        await vm.LoadAsync();
        return vm;
    }
}
