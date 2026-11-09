using Packman.Helpers;
using Packman.Models;
using System.Text.Json;

namespace Packman.Services;

public partial class IntuneService
{
    /// <summary>Searches security groups by name prefix. Needs Group.Read.All or higher, granted on the Advanced screen.</summary>
    public async Task<List<EntraGroup>> SearchGroupsAsync(string query, CancellationToken ct = default)
    {
        var results = new List<EntraGroup>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        // No $orderby: on directory objects it needs the advanced-query headers alongside
        // $filter, so the twenty results are sorted here instead.
        var filter = Uri.EscapeDataString($"startswith(displayName,'{OData.Literal(query.Trim())}')");
        var url = $"{GraphClient.Groups}?$filter={filter}&$select=id,displayName&$top=20";

        var page = (await _graph.GetAsync(url, "Group search", ct)).Json;
        if (page.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var g in arr.EnumerateArray())
                results.Add(new EntraGroup
                {
                    Id = g.GetSafeString("id"),
                    DisplayName = g.GetSafeString("displayName"),
                });

        return results.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Looks up the group with exactly this display name. Its id when exactly one group has
    /// the name; otherwise null, with <c>Matches</c> 0 (none) or 2 (more than one, which a
    /// name alone cannot pick between).
    /// </summary>
    public async Task<(string? Id, int Matches)> FindGroupIdAsync(string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return (null, 0);

        var filter = Uri.EscapeDataString($"displayName eq '{OData.Literal(displayName.Trim())}'");
        var page = (await _graph.GetAsync($"{GraphClient.Groups}?$filter={filter}&$select=id&$top=2", "Group lookup", ct)).Json;
        if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (null, 0);
        if (arr.GetArrayLength() > 1) return (null, 2);
        if (arr.GetArrayLength() == 1 && arr[0].GetSafeString("id") is { Length: > 0 } id) return (id, 1);
        return (null, 0);
    }
}
