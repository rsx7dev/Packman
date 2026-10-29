using Packman.Helpers;
using Packman.Models;

namespace Packman.Services;

public partial class IntuneService
{
    private const int DeviceReportPageSize = 50;

    // ── Device install status (reporting endpoint) ─────
    /// <summary>
    /// Every device's install status for one app, read page by page. Needs DeviceManagementApps.Read.All,
    /// which the requested ReadWrite.All scope covers. Progress reports (rows read, total rows).
    /// </summary>
    public Task<List<DeviceInstallStatus>> GetDeviceInstallStatusAsync(string appId,
        IProgress<(int Read, int Total)>? progress = null, CancellationToken ct = default)
        => ReadDeviceInstallStatusAsync(async skip => (await _graph.PostAsync(
                $"{GraphClient.Beta}/deviceManagement/reports/getDeviceInstallStatusReport",
                new { filter = $"(ApplicationId eq '{OData.Literal(appId)}')", top = DeviceReportPageSize, skip },
                "Device install status report", ct)).Json,
            DeviceReportPageSize, progress, ct);
}
