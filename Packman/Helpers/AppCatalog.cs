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

/// <summary>
/// One catalog line: an Intune app (AppId null for a package Intune doesn't have) and the
/// share package it was published from (Package null for an app with no folder on the share).
/// </summary>
public sealed record CatalogEntry(
    string? AppId, string? DisplayName, string? Publisher, string? Version,
    SharePackage? Package, AppRelations Relations);

/// <summary>
/// The JSON catalog of every Intune app ID, each with its share package when it has one and its
/// supersedence and dependency links to other app IDs. Pure data: scanning and Graph calls happen elsewhere.
/// </summary>
public static class AppCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// One entry per Intune app, in the order given, joined to its share package when one is named
    /// {Publisher}_{DisplayName}\{version}; then the packages no app claims. If an app was
    /// published more than once from the same folder, the newest one claims it.
    /// </summary>
    public static List<CatalogEntry> Build(IEnumerable<SharePackage> packages, IEnumerable<IntuneApplication> apps,
        IReadOnlyDictionary<string, AppRelations> relationsByAppId)
    {
        var appList = apps.ToList();
        var packageByKey = packages.ToDictionary(p => Key(p.AppFolderName, p.Version));
        var newestAppByKey = appList
            .Where(a => !string.IsNullOrWhiteSpace(a.Version))
            .GroupBy(FolderKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.LastModified).First().Id);

        var entries = new List<CatalogEntry>();
        var claimed = new HashSet<string>();
        foreach (var app in appList)
        {
            SharePackage? package = null;
            if (!string.IsNullOrWhiteSpace(app.Version) && newestAppByKey[FolderKey(app)] == app.Id
                && packageByKey.TryGetValue(FolderKey(app), out package))
                claimed.Add(FolderKey(app));
            var relations = relationsByAppId.TryGetValue(app.Id, out var r) ? r : AppRelations.None;
            entries.Add(new CatalogEntry(app.Id, app.DisplayName, app.Publisher, app.Version, package, relations));
        }

        entries.AddRange(packageByKey.Where(kv => !claimed.Contains(kv.Key))
            .Select(kv => new CatalogEntry(null, null, null, null, kv.Value, AppRelations.None)));
        return entries;
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

    private static string FolderKey(IntuneApplication app)
        => Key(PackagePaths.AppFolderName(app.Publisher, app.DisplayName), PackagePaths.SanitizeSegment(app.Version));
}
