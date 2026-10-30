using System.IO;
using Packman.Helpers;

namespace Packman.Services;

/// <summary>
/// Builds the app catalog from the configured IntuneApplications share and the tenant's apps,
/// and saves it next to the settings. The share is only read; the file never lands on it.
/// </summary>
public static class AppCatalogService
{
    public static string CatalogPath => Path.Combine(Path.GetDirectoryName(AppServices.Settings.SettingsPath)!, "app-catalog.json");

    /// <summary>Scans the share, matches packages to Intune apps, reads their relationships and writes the file. Returns its path and entry count.</summary>
    public static async Task<(string Path, int Count)> ExportAsync(CancellationToken ct = default)
    {
        var share = AppServices.Settings.Settings.NetworkPaths.IntuneApplications;
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidOperationException("Configure the IntuneApplications path in Settings first.");

        var packages = await Task.Run(() => ApplicationShareScanner.Scan(share), ct);
        var apps = await AppServices.Apps.GetApplicationsAsync(ct: ct);

        // Only apps that have a package on the share are worth a relationships call.
        var matched = AppCatalog.Match(packages, apps, new Dictionary<string, AppRelations>())
            .Where(e => e.AppId != null).Select(e => e.AppId!).Distinct().ToList();
        var relations = new System.Collections.Concurrent.ConcurrentDictionary<string, AppRelations>();
        await Parallel.ForEachAsync(matched, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (id, token) => relations[id] = await AppServices.Apps.GetAppRelationsAsync(id, token));

        var entries = AppCatalog.Match(packages, apps, relations);
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        await File.WriteAllTextAsync(CatalogPath, AppCatalog.ToJson(share, entries), ct);
        return (CatalogPath, entries.Count);
    }
}
