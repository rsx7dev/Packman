using Packman.Helpers;
using Packman.Models;
using System.Diagnostics;
using System.Text.Json;

namespace Packman.Services;

/// <summary>Reads and edits Win32 LOB applications in Intune via Graph.</summary>
public partial class IntuneService
{
    private const string Base = GraphClient.MobileApps;

    private readonly GraphClient _graph;

    private readonly object _cacheLock = new();
    private IReadOnlyList<IntuneApplication>? _listCache;
    private long _cacheGeneration;

    public IntuneService(Func<Task<string>> tokenProvider)
        => _graph = new GraphClient(tokenProvider);

    /// <summary>Discard cached tenant data and reject in-flight results from the previous sign-in.</summary>
    public void InvalidateApplicationCache()
    {
        lock (_cacheLock)
        {
            _listCache = null;
            _cacheGeneration++;
        }
    }

    // ── List ────────────────────────────────────────────
    public async Task<List<IntuneApplication>> GetApplicationsAsync(bool forceRefresh = false, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        long generation;
        lock (_cacheLock)
        {
            if (!forceRefresh && _listCache != null)
                return _listCache.ToList();
            generation = _cacheGeneration;
        }

        var apps = new List<IntuneApplication>();
        var url = $"{Base}?$filter=isof('microsoft.graph.win32LobApp')&$expand=categories&$top=100&$orderby=displayName";

        while (!string.IsNullOrEmpty(url))
        {
            EnsureCacheGeneration(generation);
            var response = await _graph.GetAsync(url, "Fetch applications", ct, throwOnError: false);
            EnsureCacheGeneration(generation);
            if (!response.IsSuccess)
            {
                // Graph rejects $expand=categories now and then with a 400; drop it and go on
                // without categories. Anything else is a real failure.
                if (response.StatusCode == 400 && url.Contains("&$expand=categories"))
                {
                    url = url.Replace("&$expand=categories", "");
                    continue;
                }
                throw response.ToException("Fetch applications");
            }

            var page = response.Json;
            if (page.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var el in value.EnumerateArray())
                    apps.Add(ParseListItem(el));

            progress?.Report(apps.Count);
            url = page.GetSafeString("@odata.nextLink");
        }

        var sorted = apps.OrderBy(a => a.DisplayName).ToList();
        lock (_cacheLock)
        {
            EnsureCacheGeneration(generation);
            _listCache = sorted;
            return sorted.ToList();
        }
    }

    private void EnsureCacheGeneration(long generation)
    {
        lock (_cacheLock)
            if (generation != _cacheGeneration)
                throw new InvalidOperationException("The Intune session or application list changed while loading. Refresh the applications list.");
    }

    /// <summary>True when the connected tenant has this app. Any failure counts as not found.</summary>
    public async Task<bool> ApplicationExistsAsync(string id, CancellationToken ct = default)
    {
        try { return (await _graph.GetAsync($"{Base}/{id}?$select=id", "Check app exists", ct, throwOnError: false)).IsSuccess; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    // ── Detail ──────────────────────────────────────────
    public async Task<ApplicationDetail> GetApplicationDetailAsync(string id, CancellationToken ct = default)
    {
        var app = (await _graph.GetAsync($"{Base}/{id}", "Get app details", ct)).Json;

        var detail = new ApplicationDetail
        {
            Id = app.GetSafeString("id"),
            DisplayName = app.GetSafeString("displayName") is { Length: > 0 } dn ? dn : "Unknown",
            Version = app.GetSafeString("displayVersion"),
            Publisher = app.GetSafeString("publisher"),
            Description = app.GetSafeString("description"),
            InstallCommand = app.GetSafeString("installCommandLine"),
            UninstallCommand = app.GetSafeString("uninstallCommandLine"),
            Owner = app.GetSafeString("owner"),
            Developer = app.GetSafeString("developer"),
            Notes = app.GetSafeString("notes"),
            FileName = app.GetSafeString("fileName"),
            SetupFilePath = app.GetSafeString("setupFilePath"),
            Size = app.GetSafeLong("size"),
            CreatedDateTime = app.GetSafeDateTime("createdDateTime"),
            LastModifiedDateTime = app.GetSafeDateTime("lastModifiedDateTime"),
            PublishingState = ReadStateString(app, "publishingState"),
            MinimumOperatingSystem = ReadMinimumOperatingSystem(app),
        };
        detail.LastModified = detail.LastModifiedDateTime;

        if (app.TryGetProperty("installExperience", out var ie) && ie.ValueKind == JsonValueKind.Object)
        {
            var runAs = ReadStateString(ie, "runAsAccount");
            detail.InstallContext = string.Equals(runAs, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "System";
            detail.RestartBehavior = ie.GetSafeString("deviceRestartBehavior");
            detail.MaxRunTimeMinutes = ie.GetSafeInt("maxRunTimeInMinutes");
        }
        detail.MinDiskSpaceMB = app.GetSafeInt("minimumFreeDiskSpaceInMB");
        detail.DetectionRules = DetectionRuleGraph.Parse(app);

        // Independent reads; together they are the slow half of opening a detail page.
        var groups = GetAssignedGroupsAsync(id, ct);
        var stats = GetInstallationStatisticsAsync(id, ct);
        await Task.WhenAll(groups, stats);
        detail.AssignedGroups = groups.Result;
        detail.Statistics = stats.Result;
        return detail;
    }

    // ── Assignments ─────────────────────────────────────
    /// <summary>The app's assignments. Empty when they cannot be read; the detail page tolerates that.</summary>
    public async Task<List<AssignedGroup>> GetAssignedGroupsAsync(string appId, CancellationToken ct = default)
    {
        var groups = new List<AssignedGroup>();
        var needNames = new List<(AssignedGroup group, string groupId)>();
        var url = $"{Base}/{appId}/assignments";
        while (!string.IsNullOrEmpty(url))
        {
            var response = await _graph.GetAsync(url, "Read assignments", ct, throwOnError: false);
            if (!response.IsSuccess)
            {
                Debug.WriteLine($"Assignments unavailable for {appId}: HTTP {response.StatusCode}");
                return new List<AssignedGroup>();
            }

            var root = response.Json;
            url = root.GetSafeString("@odata.nextLink");
            if (!root.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var a in arr.EnumerateArray())
            {
                var assignmentId = a.GetSafeString("id");
                var intent = a.GetSafeString("intent") is { Length: > 0 } i ? i : "Unknown";
                if (!a.TryGetProperty("target", out var t)) continue;
                var type = t.GetSafeString("@odata.type");

                switch (type)
                {
                    case "#microsoft.graph.groupAssignmentTarget":
                    case "#microsoft.graph.exclusionGroupAssignmentTarget":
                        var gid = t.GetSafeString("groupId");
                        if (string.IsNullOrEmpty(gid)) break;
                        var group = new AssignedGroup
                        {
                            AssignmentId = assignmentId, GroupId = gid,
                            GroupName = $"Group {Shorten(gid)}", AssignmentType = intent,
                            IsExcluded = type == "#microsoft.graph.exclusionGroupAssignmentTarget",
                        };
                        groups.Add(group);
                        needNames.Add((group, gid));
                        break;
                    case "#microsoft.graph.allLicensedUsersAssignmentTarget":
                        groups.Add(new AssignedGroup { AssignmentId = assignmentId, GroupName = "All Licensed Users", AssignmentType = intent });
                        break;
                    case "#microsoft.graph.allDevicesAssignmentTarget":
                        groups.Add(new AssignedGroup { AssignmentId = assignmentId, GroupName = "All Devices", AssignmentType = intent });
                        break;
                    default:
                        groups.Add(new AssignedGroup { AssignmentId = assignmentId, GroupName = type.Replace("#microsoft.graph.", ""), AssignmentType = intent });
                        break;
                }
            }
        }

        // Needs directory read permission; the id stays on display when it fails.
        await Task.WhenAll(needNames.Select(async item =>
        {
            var name = await GetGroupNameAsync(item.groupId, ct);
            if (!string.IsNullOrEmpty(name)) item.group.GroupName = name;
        }));
        return groups;
    }

    private async Task<string?> GetGroupNameAsync(string groupId, CancellationToken ct)
    {
        var response = await _graph.GetAsync($"{GraphClient.Groups}/{groupId}?$select=displayName", "Read group name", ct, throwOnError: false);
        return response.IsSuccess ? response.Json.GetSafeString("displayName") : null;
    }

    // ── Install statistics (reporting endpoint) ─────────
    /// <summary>Device install rollup. Zeros when the report is unavailable.</summary>
    public async Task<InstallationStatistics> GetInstallationStatisticsAsync(string appId, CancellationToken ct = default)
    {
        var response = await _graph.PostAsync(
            $"{GraphClient.Beta}/deviceManagement/reports/getAppStatusOverviewReport",
            new { filter = $"(ApplicationId eq '{OData.Literal(appId)}')" },
            "App status report", ct, throwOnError: false);
        if (!response.IsSuccess)
        {
            Debug.WriteLine($"App status report unavailable for {appId}: HTTP {response.StatusCode}");
            return new InstallationStatistics();
        }

        var root = response.Json;
        if (!root.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Array)
            return new InstallationStatistics();

        // Columns are read by name from the Schema the report ships, so a reordered or
        // added column cannot silently shift the numbers.
        var columns = ReadReportColumns(root);
        int Cell(List<JsonElement> row, string column, int fallbackIndex)
        {
            var index = columns.TryGetValue(column, out var i) ? i : fallbackIndex;
            return index >= 0 && index < row.Count ? row[index].GetSafeInt() : 0;
        }

        foreach (var rowElement in values.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Array) continue;
            var row = rowElement.EnumerateArray().ToList();
            var stats = new InstallationStatistics
            {
                FailedInstalls = Cell(row, "FailedDeviceCount", 1),
                PendingInstalls = Cell(row, "PendingInstallDeviceCount", 2),
                SuccessfulInstalls = Cell(row, "InstalledDeviceCount", 3),
                NotInstalled = Cell(row, "NotInstalledDeviceCount", 4),
                NotApplicable = Cell(row, "NotApplicableDeviceCount", 5),
            };
            stats.TotalDevices = stats.SuccessfulInstalls + stats.FailedInstalls
                + stats.PendingInstalls + stats.NotInstalled + stats.NotApplicable;
            return stats;
        }
        return new InstallationStatistics();
    }

    // ── Delete ──────────────────────────────────────────
    public async Task DeleteApplicationAsync(string id, CancellationToken ct = default)
    {
        await _graph.DeleteAsync($"{Base}/{id}", "Delete app", ct);

        lock (_cacheLock)
        {
            _cacheGeneration++;
            if (_listCache != null)
                _listCache = _listCache.Where(a => a.Id != id).ToList();
        }
    }

    // ── Parsing ─────────────────────────────────────────
    private static IntuneApplication ParseListItem(JsonElement app)
    {
        var category = "Uncategorized";
        if (app.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            var names = cats.EnumerateArray()
                .Select(c => c.GetSafeString("displayName"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (names.Count > 0) category = string.Join(", ", names);
        }

        return new IntuneApplication
        {
            Id = app.GetSafeString("id"),
            DisplayName = app.GetSafeString("displayName") is { Length: > 0 } dn ? dn : "Unknown",
            Version = app.GetSafeString("displayVersion"),
            Publisher = app.GetSafeString("publisher"),
            Category = category,
            LastModified = app.GetSafeDateTime("lastModifiedDateTime"),
            PublishingState = ReadStateString(app, "publishingState"),
        };
    }

    private static string ReadMinimumOperatingSystem(JsonElement app)
    {
        var release = app.GetSafeString("minimumSupportedWindowsRelease");
        if (!string.IsNullOrEmpty(release)) return RequirementInfo.FormatWindowsRelease(release);

        if (app.TryGetProperty("minimumSupportedOperatingSystem", out var legacy))
        {
            // Older apps can still expose only the boolean OS object. Prefer the newest
            // flagged release if the response includes more than one flag.
            foreach (var version in new[] { "21H1", "2H20", "2004", "1909", "1903", "1809", "1803", "1709", "1703", "1607" })
                if (legacy.GetSafeBool($"v10_{version}")) return RequirementInfo.FormatWindowsRelease(version);
            if (legacy.GetSafeBool("v10_0")) return "Windows 10";
            if (legacy.GetSafeBool("v8_1")) return "Windows 8.1";
            if (legacy.GetSafeBool("v8_0")) return "Windows 8";
        }
        return "Not specified";
    }

    // Enum-ish properties come back as strings on beta and as numbers on some older records.
    private static string ReadStateString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32().ToString() : v.GetString() ?? "";
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] + "…" : id;
}
