using System.IO;

namespace Packman.Helpers;

/// <summary>
/// Finds an app's package folder on the share. Layout is {root}\{Vendor}_{AppName}\{version};
/// a folder belongs to an app only through its <see cref="PackageMarker"/> link, never by name.
/// </summary>
public static class PackageSourceLocator
{
    /// <summary>
    /// Returns the version folder linked to this app in this tenant, or null when none is. A legacy
    /// link has no tenant and matches on the app id alone: the caller has just loaded that app
    /// from the connected tenant, which proves it exists there. When several folders are linked,
    /// the latest upload wins.
    /// </summary>
    public static string? FindLinked(string shareRoot, string appId, string? tenantId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(shareRoot) || !Directory.Exists(shareRoot))
                return null;

            return Directory.EnumerateDirectories(shareRoot)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(folder => (folder, link: PackageMarker.Read(folder)))
                .Where(x => x.link != null && x.link.AppId == appId && (x.link.IsLegacy || x.link.TenantId == tenantId))
                .OrderByDescending(x => x.link!.UploadedAt, StringComparer.Ordinal)
                .Select(x => x.folder)
                .FirstOrDefault();
        }
        catch
        {
            return null;   // unreachable share or no permission; the page shows "not found"
        }
    }
}
