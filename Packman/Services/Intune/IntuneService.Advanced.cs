using Packman.Helpers;
using Packman.Models;
using System.Text.Json;

namespace Packman.Services;

/// <summary>
/// Directory lookups the Intune portal doesn't offer: bulk PC name resolution, a device's
/// groups, and every app targeting a group. Graph has no reverse index for the last one.
/// </summary>
public partial class IntuneService
{
    private const string DeviceSelect = "id,displayName,operatingSystem,operatingSystemVersion,accountEnabled";

    /// <summary>
    /// Resolves PC names to Entra devices, in chunks to stay under the Graph filter length.
    /// Stale re-enrolments mean one name can match several records, so all are returned.
    /// </summary>
    public async Task<Dictionary<string, List<EntraDevice>>> FindDevicesByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var byName = new Dictionary<string, List<EntraDevice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) byName[name] = new List<EntraDevice>();

        foreach (var chunk in names.Chunk(15))
        {
            var clauses = chunk.Select(n => $"displayName eq '{OData.Literal(n)}'");
            var filter = Uri.EscapeDataString(string.Join(" or ", clauses));
            var url = $"{GraphClient.Devices}?$filter={filter}&$select={DeviceSelect}&$top=999";

            foreach (var device in await ReadDevicesAsync(url, allPages: true, ct))
                if (byName.TryGetValue(device.DisplayName, out var matches))
                    matches.Add(device);
        }
        return byName;
    }

    /// <summary>Searches devices by name prefix, for the PC lookup box.</summary>
    public async Task<List<EntraDevice>> SearchDevicesAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<EntraDevice>();
        var filter = Uri.EscapeDataString($"startswith(displayName,'{OData.Literal(query.Trim())}')");
        return await ReadDevicesAsync($"{GraphClient.Devices}?$filter={filter}&$select={DeviceSelect}&$top=10", allPages: false, ct);
    }

    /// <summary>Lists the groups a device is a direct member of.</summary>
    public async Task<List<DeviceGroupMembership>> GetDeviceGroupsAsync(string deviceObjectId, CancellationToken ct = default)
    {
        var groups = new List<DeviceGroupMembership>();
        var url = $"{GraphClient.Devices}/{deviceObjectId}/memberOf?$top=100";

        await foreach (var g in _graph.GetAllPagesAsync(url, "Read group membership", ct))
        {
            // memberOf also returns directory roles.
            if (g.GetSafeString("@odata.type") != "#microsoft.graph.group") continue;
            groups.Add(new DeviceGroupMembership
            {
                Id = g.GetSafeString("id"),
                DisplayName = g.GetSafeString("displayName"),
                Description = g.GetSafeString("description"),
                MembershipType = IsDynamic(g) ? "Dynamic" : "Assigned",
            });
        }
        return groups.OrderBy(g => g.DisplayName).ToList();
    }

    /// <summary>
    /// Finds every app assigned to a group. Graph only answers the reverse question, so the
    /// app list is walked with assignments expanded and filtered here.
    /// <paramref name="progress"/> reports apps scanned so far.
    /// </summary>
    public async Task<List<GroupAppAssignment>> GetGroupAppAssignmentsAsync(string groupId, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var matches = new List<GroupAppAssignment>();
        var url = $"{Base}?$expand=assignments&$top=50";

        await foreach (var app in _graph.GetAllPagesAsync(url, "Scan app assignments", ct, progress))
        {
            if (!app.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var a in assignments.EnumerateArray())
            {
                if (!a.TryGetProperty("target", out var target)) continue;
                if (!string.Equals(target.GetSafeString("groupId"), groupId, StringComparison.OrdinalIgnoreCase)) continue;

                matches.Add(new GroupAppAssignment
                {
                    AppId = app.GetSafeString("id"),
                    DisplayName = app.GetSafeString("displayName"),
                    Publisher = app.GetSafeString("publisher"),
                    AppType = FriendlyAppType(app.GetSafeString("@odata.type")),
                    Intent = a.GetSafeString("intent"),
                    IsExcluded = target.GetSafeString("@odata.type")
                        .Contains("exclusionGroupAssignmentTarget", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        return matches.OrderBy(m => m.DisplayName).ToList();
    }

    private async Task<List<EntraDevice>> ReadDevicesAsync(string url, bool allPages, CancellationToken ct)
    {
        var devices = new List<EntraDevice>();
        while (!string.IsNullOrEmpty(url))
        {
            var page = (await _graph.GetAsync(url, "Device lookup", ct)).Json;
            if (page.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var d in arr.EnumerateArray())
                    devices.Add(new EntraDevice
                    {
                        Id = d.GetSafeString("id"),
                        DisplayName = d.GetSafeString("displayName"),
                        OperatingSystem = d.GetSafeString("operatingSystem"),
                        OperatingSystemVersion = d.GetSafeString("operatingSystemVersion"),
                        Enabled = !d.TryGetProperty("accountEnabled", out var ae) || ae.ValueKind != JsonValueKind.False,
                    });

            url = allPages ? page.GetSafeString("@odata.nextLink") : "";
        }
        return devices;
    }

    private static bool IsDynamic(JsonElement group)
        => group.TryGetProperty("groupTypes", out var types) && types.ValueKind == JsonValueKind.Array
           && types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String
               && string.Equals(t.GetString(), "DynamicMembership", StringComparison.OrdinalIgnoreCase));

    private static string FriendlyAppType(string odataType) => odataType.Replace("#microsoft.graph.", "") switch
    {
        "win32LobApp" => "Win32",
        "winGetApp" => "WinGet",
        "windowsMobileMSI" => "MSI",
        "windowsUniversalAppX" => "UWP",
        "windowsStoreApp" => "Microsoft Store",
        "officeSuiteApp" => "Microsoft 365 Apps",
        "windowsWebApp" or "webApp" => "Web link",
        "" => "App",
        var other => other,
    };

    /// <summary>The app's supersedence and dependency links to other apps.</summary>
    public async Task<AppRelations> GetAppRelationsAsync(string appId, CancellationToken ct = default)
    {
        var page = (await _graph.GetAsync($"{Base}/{appId}/relationships", "Read app relationships", ct)).Json;
        return AppCatalog.ParseRelationships(page, appId);
    }
}
