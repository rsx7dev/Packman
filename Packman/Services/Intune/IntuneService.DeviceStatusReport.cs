using Packman.Helpers;
using Packman.Models;
using System.Globalization;
using System.Text.Json;

namespace Packman.Services;

public partial class IntuneService
{
    /// <summary>
    /// Requests pages at increasing skip until TotalRowCount rows are read, or, without a
    /// total, until a short or empty page.
    /// </summary>
    public static async Task<List<DeviceInstallStatus>> ReadDeviceInstallStatusAsync(Func<int, Task<JsonElement>> fetchPage,
        int pageSize, IProgress<(int Read, int Total)>? progress = null, CancellationToken ct = default)
    {
        var rows = new List<DeviceInstallStatus>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await fetchPage(rows.Count);
            var pageRows = ParseDeviceInstallStatusPage(page);
            rows.AddRange(pageRows);

            var total = page.GetSafeInt("TotalRowCount");
            progress?.Report((rows.Count, Math.Max(total, rows.Count)));
            var done = total > 0 ? rows.Count >= total : pageRows.Count < pageSize;
            if (pageRows.Count == 0 || done) return rows;
        }
    }

    /// <summary>The rows of one report page. Columns are read by name from its Schema.</summary>
    public static List<DeviceInstallStatus> ParseDeviceInstallStatusPage(JsonElement root)
    {
        var rows = new List<DeviceInstallStatus>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Array)
            return rows;

        var columns = ReadReportColumns(root);
        foreach (var rowElement in values.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Array) continue;
            var row = rowElement.EnumerateArray().ToList();
            string Text(string column) =>
                columns.TryGetValue(column, out var i) && i < row.Count ? CellText(row[i]) : "";

            rows.Add(new DeviceInstallStatus
            {
                DeviceName = Text("DeviceName"),
                UserPrincipalName = Text("UserPrincipalName"),
                // The enum column is locale-independent; the _loc column is the fallback.
                State = ParseInstallState(FirstText(Text("AppInstallState"), Text("InstallState"), Text("AppInstallState_loc"))),
                // AppInstallStateDetails itself is an enum number; the readable text is in _loc.
                StateDetail = FirstText(Text("AppInstallStateDetails_loc"), Text("InstallStateDetail"), NonNumeric(Text("AppInstallStateDetails"))),
                ErrorCode = ParseErrorCode(Text("ErrorCode"), Text("HexErrorCode")),
                LastModified = DateTimeOffset.TryParse(Text("LastModifiedDateTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var modified) ? modified.UtcDateTime : null,
                OsVersion = Text("OSVersion"),
            });
        }
        return rows;
    }

    /// <summary>resultantAppState values, or their names. Uninstall failures count as failed.</summary>
    private static DeviceInstallState ParseInstallState(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n switch
            {
                1 => DeviceInstallState.Installed,
                2 or 4 => DeviceInstallState.Failed,
                3 => DeviceInstallState.NotInstalled,
                5 => DeviceInstallState.Pending,
                -1 => DeviceInstallState.NotApplicable,
                _ => DeviceInstallState.Unknown,
            };

        return value.Replace(" ", "").ToLowerInvariant() switch
        {
            "installed" => DeviceInstallState.Installed,
            "failed" or "uninstallfailed" => DeviceInstallState.Failed,
            "notinstalled" => DeviceInstallState.NotInstalled,
            "pending" or "pendinginstall" => DeviceInstallState.Pending,
            "notapplicable" => DeviceInstallState.NotApplicable,
            _ => DeviceInstallState.Unknown,
        };
    }

    /// <summary>The decimal column, read as signed or unsigned 32-bit; the hex column when it is missing.</summary>
    private static int ParseErrorCode(string dec, string hex)
    {
        if (long.TryParse(dec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= int.MinValue and <= uint.MaxValue)
            return unchecked((int)n);
        var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? unchecked((int)h) : 0;
    }

    private static string CellText(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => cell.GetString() ?? "",
        JsonValueKind.Number => cell.GetRawText(),
        _ => "",
    };

    private static string FirstText(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string NonNumeric(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "" : value;

    private static Dictionary<string, int> ReadReportColumns(JsonElement root)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Array)
            return columns;

        var index = 0;
        foreach (var column in schema.EnumerateArray())
        {
            var name = column.GetSafeString("Column");
            if (!string.IsNullOrEmpty(name)) columns[name] = index;
            index++;
        }
        return columns;
    }
}
