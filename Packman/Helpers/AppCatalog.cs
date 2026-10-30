using System.Text.Json;
using Packman.Models;

namespace Packman.Helpers;

/// <summary>Other apps an Intune app is tied to, as app IDs.</summary>
public sealed record AppRelations(
    IReadOnlyList<string> Supersedes, IReadOnlyList<string> SupersededBy,
    IReadOnlyList<string> DependsOn, IReadOnlyList<string> RequiredBy)
{
    public static readonly AppRelations None = new([], [], [], []);
}

/// <summary>One share package and, when Intune has it, the app it was published as. AppId is null otherwise.</summary>
public sealed record CatalogEntry(
    string AppFolder, string Version, string FolderPath, bool HasScript, bool HasIntuneWin,
    string? AppId, string? DisplayName, AppRelations Relations);

/// <summary>
/// The JSON catalog tying share packages to their Intune app IDs, with supersedence and
/// dependency links between those IDs. Pure data: scanning and Graph calls happen elsewhere.
/// </summary>
public static class AppCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Matches each package to the Intune app named {Publisher}_{DisplayName} with the same version,
    /// the way packages are named on the share. If an app was published more than once, the newest wins.
    /// </summary>
    public static List<CatalogEntry> Match(IEnumerable<SharePackage> packages, IEnumerable<IntuneApplication> apps,
        IReadOnlyDictionary<string, AppRelations> relationsByAppId)
    {
        var byKey = apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Version))
            .GroupBy(a => Key(PackagePaths.AppFolderName(a.Publisher, a.DisplayName), PackagePaths.SanitizeSegment(a.Version)))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.LastModified).First());

        return packages.Select(p =>
        {
            byKey.TryGetValue(Key(p.AppFolderName, p.Version), out var app);
            var relations = app != null && relationsByAppId.TryGetValue(app.Id, out var r) ? r : AppRelations.None;
            return new CatalogEntry(p.AppFolderName, p.Version, p.FolderPath, p.HasScript, p.HasIntuneWin,
                app?.Id, app?.DisplayName, relations);
        }).ToList();
    }

    /// <summary>
    /// Reads a Graph mobileApps/{id}/relationships page. The other app is the relationship's
    /// target: a child target is something this app supersedes or needs, a parent target is
    /// an app that supersedes or needs this one.
    /// </summary>
    public static AppRelations ParseRelationships(JsonElement page, string appId)
    {
        var supersedes = new List<string>();
        var supersededBy = new List<string>();
        var dependsOn = new List<string>();
        var requiredBy = new List<string>();

        if (page.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            foreach (var rel in value.EnumerateArray())
            {
                var target = rel.GetSafeString("targetId");
                if (target.Length == 0 || target == appId) continue;

                var isChild = rel.GetSafeString("targetType").Equals("child", StringComparison.OrdinalIgnoreCase);
                var type = rel.GetSafeString("@odata.type");
                if (type.EndsWith("mobileAppSupersedence", StringComparison.OrdinalIgnoreCase))
                    (isChild ? supersedes : supersededBy).Add(target);
                else if (type.EndsWith("mobileAppDependency", StringComparison.OrdinalIgnoreCase))
                    (isChild ? dependsOn : requiredBy).Add(target);
            }

        return new AppRelations(supersedes, supersededBy, dependsOn, requiredBy);
    }

    public static string ToJson(string shareRoot, IReadOnlyList<CatalogEntry> entries)
        => JsonSerializer.Serialize(new { share = shareRoot, apps = entries }, JsonOptions);

    private static string Key(string folder, string version) => $"{folder}\n{version}".ToLowerInvariant();
}
