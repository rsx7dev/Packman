using System.IO;
using Packman.Helpers;

namespace Packman.Services;

/// <summary>
/// Builds the app catalog from every app in the tenant and the configured IntuneApplications share,
/// and saves it next to the settings. The share is only read; the file never lands on it.
/// </summary>
public static class AppCatalogService
{
    public static string CatalogPath => Path.Combine(Path.GetDirectoryName(AppServices.Settings.SettingsPath)!, "app-catalog.json");

    /// <summary>Lists all Intune apps, reads their relationships, joins them to the share's packages and writes the file. Returns its path and entry count.</summary>
    public static async Task<(string Path, int Count)> ExportAsync(CancellationToken ct = default)
    {
        var share = AppServices.Settings.Settings.NetworkPaths.IntuneApplications;
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidOperationException("Configure the IntuneApplications path in Settings first.");

        var packages = await Task.Run(() => ApplicationShareScanner.Scan(share), ct);
        var apps = await AppServices.Apps.GetApplicationsAsync(ct: ct);

        var relations = new System.Collections.Concurrent.ConcurrentDictionary<string, AppRelations>();
        await Parallel.ForEachAsync(apps, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (app, token) => relations[app.Id] = await AppServices.Apps.GetAppRelationsAsync(app.Id, token));

        var entries = AppCatalog.Build(packages, apps, relations);
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        await File.WriteAllTextAsync(CatalogPath, AppCatalog.ToJson(share, entries), ct);
        return (CatalogPath, entries.Count);
    }
}
