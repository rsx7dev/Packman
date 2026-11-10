using Packman.Helpers;
using Packman.Models;
using System.Diagnostics;
using System.Text.Json;

namespace Packman.Services;

public partial class IntuneService
{
    // ── Detection rules ─────────────────────────────────
    // No per-rule endpoint: the whole detectionRules array is PATCHed.
    public async Task UpdateDetectionRulesAsync(string appId, IEnumerable<DetectionRule> rules, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.win32LobApp",
            ["detectionRules"] = rules.Select(DetectionRuleGraph.Serialize).ToList(),
        };
        await _graph.PatchAsync($"{Base}/{appId}", payload, "Update detection rules", ct);
    }

    // ── Assignments ─────────────────────────────────────
    public async Task AddAssignmentAsync(string appId, string groupId, string intent, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.mobileAppAssignment",
            ["intent"] = intent,
            ["target"] = new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.groupAssignmentTarget",
                ["groupId"] = groupId,
            },
        };
        await _graph.PostAsync($"{Base}/{appId}/assignments", payload, "Add assignment", ct);
    }

    public Task RemoveAssignmentAsync(string appId, string assignmentId, CancellationToken ct = default)
        => _graph.DeleteAsync($"{Base}/{appId}/assignments/{assignmentId}", "Remove assignment", ct);

    // ── Group members ───────────────────────────────────
    /// <summary>Every direct member of a group, all pages.</summary>
    public async Task<List<GroupMember>> GetGroupMembersAsync(string groupId, CancellationToken ct = default)
    {
        var members = new List<GroupMember>();
        var url = $"{GraphClient.Groups}/{groupId}/members?$select=id,displayName,userPrincipalName&$top=999";
        await foreach (var m in _graph.GetAllPagesAsync(url, "Load members", ct))
        {
            members.Add(new GroupMember
            {
                Id = m.GetSafeString("id"),
                DisplayName = m.GetSafeString("displayName"),
                Detail = m.GetSafeString("userPrincipalName"),
                Kind = m.GetSafeString("@odata.type") switch
                {
                    "#microsoft.graph.device" => "Device",
                    "#microsoft.graph.user" => "User",
                    "#microsoft.graph.group" => "Group",
                    var t => t.Replace("#microsoft.graph.", ""),
                },
            });
        }
        return members;
    }

    /// <summary>
    /// Adds a device or user to a group. Returns false when it was already a member,
    /// which Graph reports as a 400 "already exist". Needs GroupMember.ReadWrite.All.
    /// </summary>
    public async Task<bool> AddGroupMemberAsync(string groupId, string directoryObjectId, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{directoryObjectId}",
        };
        var response = await _graph.PostAsync($"{GraphClient.Groups}/{groupId}/members/$ref", payload, "Add member", ct, throwOnError: false);
        if (response.IsSuccess) return true;
        if (response.StatusCode == 400 && response.Body.Contains("already exist", StringComparison.OrdinalIgnoreCase)) return false;
        throw response.ToException("Add member");
    }

    public Task RemoveGroupMemberAsync(string groupId, string memberId, CancellationToken ct = default)
        => _graph.DeleteAsync($"{GraphClient.Groups}/{groupId}/members/{memberId}/$ref", "Remove member", ct);

    /// <summary>
    /// Searches one kind of directory object for the add-member picker. Users match on the
    /// start of their name, sign-in name, email or surname; devices on the start of their name.
    /// </summary>
    public async Task<List<GroupMember>> SearchMembersAsync(string query, MemberKind kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var literal = OData.Literal(query.Trim());
        return kind == MemberKind.Users
            ? await SearchDirectoryAsync(
                $"{GraphClient.Users}?$filter={Uri.EscapeDataString(GroupMember.UserFilter(literal))}&$select=id,displayName,userPrincipalName&$top={MemberSearchLimit}",
                GroupMember.User, "userPrincipalName", ct)
            : await SearchDirectoryAsync(
                $"{GraphClient.Devices}?$filter={Uri.EscapeDataString(GroupMember.DeviceFilter(literal))}&$select=id,displayName,operatingSystem&$top={MemberSearchLimit}",
                GroupMember.Device, "operatingSystem", ct);
    }

    /// <summary>How many search results the add-member picker asks for.</summary>
    public const int MemberSearchLimit = 20;

    private async Task<List<GroupMember>> SearchDirectoryAsync(string url, string kind, string detailProperty, CancellationToken ct)
    {
        var results = new List<GroupMember>();
        var response = await _graph.GetAsync(url, $"Search {kind.ToLowerInvariant()}s", ct, throwOnError: false);
        if (!response.IsSuccess)
        {
            // Typically a missing directory scope.
            Debug.WriteLine($"{kind} search failed: HTTP {response.StatusCode} {GraphException.ExtractMessage(response.Body)}");
            return results;
        }

        if (response.Json.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var m in arr.EnumerateArray())
                results.Add(new GroupMember
                {
                    Id = m.GetSafeString("id"),
                    DisplayName = m.GetSafeString("displayName"),
                    Detail = m.GetSafeString(detailProperty),
                    Kind = kind,
                });
        return results;
    }
}
