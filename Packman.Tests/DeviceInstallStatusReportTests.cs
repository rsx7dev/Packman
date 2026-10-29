using System.Text.Json;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>Parsing and paging of getDeviceInstallStatusReport responses, from report JSON on disk.</summary>
public class DeviceInstallStatusReportTests
{
    internal static JsonElement Fixture(string name) =>
        JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void Reads_columns_by_name_from_the_schema()
    {
        var rows = IntuneService.ParseDeviceInstallStatusPage(Fixture("device-status-page1.json"));

        Assert.Equal(2, rows.Count);
        var installed = rows[0];
        Assert.Equal("LAB-PC-01", installed.DeviceName);
        Assert.Equal("adele.vance@contoso.com", installed.UserPrincipalName);
        Assert.Equal(DeviceInstallState.Installed, installed.State);
        Assert.Equal(0, installed.ErrorCode);
        Assert.Equal("", installed.HexErrorCode);
        Assert.Equal(new DateTime(2026, 10, 20, 8, 15, 0, DateTimeKind.Utc), installed.LastModified);

        var failed = rows[1];
        Assert.Equal(DeviceInstallState.Failed, failed.State);
        Assert.Equal("The application was not detected after installation completed successfully", failed.StateDetail);
        Assert.Equal(-2016345060, failed.ErrorCode);
        Assert.Equal("0x87D1041C", failed.HexErrorCode);
        Assert.Equal("", failed.OsVersion);
    }

    [Fact]
    public void A_reordered_schema_reads_the_same_fields()
    {
        var rows = IntuneService.ParseDeviceInstallStatusPage(Fixture("device-status-reordered.json"));

        Assert.Equal(3, rows.Count);
        var msi = rows[0];
        Assert.Equal("SALES-LT-07", msi.DeviceName);
        Assert.Equal("megan.bowen@contoso.com", msi.UserPrincipalName);
        Assert.Equal(DeviceInstallState.Failed, msi.State);
        Assert.Equal("Installer exited with \"1603\", rolled back", msi.StateDetail);
        Assert.Equal(1603, msi.ErrorCode);
        Assert.Equal("0x00000643", msi.HexErrorCode);
        Assert.Equal("10.0.22631.4317", msi.OsVersion);
        Assert.Equal(new DateTime(2026, 10, 23, 7, 45, 0, DateTimeKind.Utc), msi.LastModified);

        // ErrorCode as an unsigned decimal still reads as the signed 32-bit code.
        Assert.Equal(unchecked((int)0x87D30065), rows[1].ErrorCode);
        Assert.Equal("0x87D30065", rows[1].HexErrorCode);

        Assert.Equal(DeviceInstallState.NotApplicable, rows[2].State);
        Assert.Equal("Not applicable for this device", rows[2].StateDetail);
    }

    [Fact]
    public void Detail_text_puts_the_report_text_first_and_an_unknown_code_shows_only_its_value()
    {
        var rows = IntuneService.ParseDeviceInstallStatusPage(Fixture("device-status-reordered.json"));

        Assert.Equal("Installer exited with \"1603\", rolled back · 0x00000643 (1603) · A fatal error occurred during installation.",
            rows[0].DetailText);
        Assert.Equal("", rows[1].ErrorDescription);
        Assert.Equal("0x87D30065 (-2016214939)", rows[1].DetailText);
    }

    [Fact]
    public async Task Pages_with_skip_until_the_total_row_count_is_read()
    {
        var skips = new List<int>();
        var reports = new List<(int Read, int Total)>();
        var rows = await IntuneService.ReadDeviceInstallStatusAsync(skip =>
        {
            skips.Add(skip);
            return Task.FromResult(Fixture(skip == 0 ? "device-status-page1.json" : "device-status-page2.json"));
        }, pageSize: 2, new SyncProgress(reports.Add));

        Assert.Equal(new[] { 0, 2 }, skips);
        Assert.Equal(new[] { "LAB-PC-01", "LAB-PC-02", "KIOSK-ZOË" }, rows.Select(r => r.DeviceName));
        Assert.Equal(new[] { (2, 3), (3, 3) }, reports);
    }

    [Fact]
    public async Task Without_a_total_paging_stops_at_a_short_page()
    {
        var page1 = WithoutTotal(Fixture("device-status-page1.json"));
        var page2 = WithoutTotal(Fixture("device-status-page2.json"));
        var skips = new List<int>();

        var rows = await IntuneService.ReadDeviceInstallStatusAsync(skip =>
        {
            skips.Add(skip);
            return Task.FromResult(skip == 0 ? page1 : page2);
        }, pageSize: 2);

        Assert.Equal(new[] { 0, 2 }, skips);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task An_empty_report_is_one_request_and_no_rows()
    {
        var calls = 0;
        var rows = await IntuneService.ReadDeviceInstallStatusAsync(_ =>
        {
            calls++;
            return Task.FromResult(Fixture("device-status-empty.json"));
        }, pageSize: 50);

        Assert.Empty(rows);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1603, "A fatal error occurred during installation.")]
    [InlineData(unchecked((int)0x80070643), "A fatal error occurred during installation.")]
    [InlineData(3010, "A restart is required to complete the install. This message indicates success.")]
    [InlineData(5, "Access is denied.")]
    [InlineData(unchecked((int)0x87D1041C), "Application installation succeeded but application is not detected.")]
    public void Documented_codes_have_their_published_text(int code, string text) =>
        Assert.Equal(text, InstallErrorText.Describe(code));

    [Theory]
    [InlineData(unchecked((int)0x87D30065))]
    [InlineData(42424)]
    public void Undocumented_codes_have_no_description(int code) =>
        Assert.Null(InstallErrorText.Describe(code));

    private static JsonElement WithoutTotal(JsonElement page)
    {
        var copy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(page.GetRawText())!;
        copy.Remove("TotalRowCount");
        return JsonSerializer.SerializeToElement(copy);
    }

    /// <summary>Progress that reports in line, so the assertions see every report.</summary>
    internal sealed class SyncProgress : IProgress<(int Read, int Total)>
    {
        private readonly Action<(int Read, int Total)> _report;
        public SyncProgress(Action<(int Read, int Total)> report) => _report = report;
        public void Report((int Read, int Total) value) => _report(value);
    }
}
