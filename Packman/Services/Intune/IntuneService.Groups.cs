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

    /// <summary>The id of the group with exactly this display name, or null.</summary>
    public async Task<string?> FindGroupIdAsync(string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var filter = Uri.EscapeDataString($"displayName eq '{OData.Literal(displayName.Trim())}'");
        var page = (await _graph.GetAsync($"{GraphClient.Groups}?$filter={filter}&$select=id", "Group lookup", ct)).Json;
        if (page.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            return arr[0].GetSafeString("id") is { Length: > 0 } id ? id : null;
        return null;
    }
}
