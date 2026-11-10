using System.IO;

namespace Packman.Helpers;

/// <summary>
/// Finds an app's package folder on the share. Layout is {root}\{app folder}\{version}, where the
/// app folder is one level ({Vendor}_{AppName}) or two ({Vendor}\{AppName}) depending on the name
/// format in Settings; a folder belongs to an app only through its <see cref="PackageMarker"/>
/// link, never by name.
/// </summary>
public static class PackageSourceLocator
{
    /// <summary>
    /// Returns the version folder linked to this app in this tenant, or null when none is, or
    /// when more than one is (see <see cref="FindAllLinked"/>). A legacy link has no tenant and
    /// matches on the app id alone: the caller has just loaded that app from the connected
    /// tenant, which proves it exists there.
    /// </summary>
    public static string? FindLinked(string shareRoot, string appId, string? tenantId)
    {
        var folders = FindAllLinked(shareRoot, appId, tenantId);
        return folders.Count == 1 ? folders[0] : null;
    }

    /// <summary>
    /// Every version folder whose link names this app. Anyone who can write to the share can
    /// write a packman.id, so when two folders claim one app neither is trusted: nothing in
    /// the files can say which is real. A folder that cannot be read is skipped, so one
    /// restricted folder does not hide every other package.
    /// </summary>
    public static IReadOnlyList<string> FindAllLinked(string shareRoot, string appId, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(shareRoot) || string.IsNullOrWhiteSpace(appId))
            return [];

        var skipUnreadable = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        try
        {
            if (!Directory.Exists(shareRoot)) return [];
            return Directory.EnumerateDirectories(shareRoot, "*", skipUnreadable)
                .SelectMany(app => Directory.EnumerateDirectories(app, "*", skipUnreadable))
                .SelectMany(folder => VersionFolders(folder, skipUnreadable))
                .Where(folder => PackageMarker.Read(folder) is { } link && link.AppId == appId &&
                                 (link.IsLegacy || link.TenantId == tenantId))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];   // unreachable share; the page shows "not found"
        }
    }

    // A second-level folder is a version folder ({Vendor}_{AppName}\{version}) when it holds a
    // package, and otherwise an app folder of the two-level format, whose children are the versions.
    private static IEnumerable<string> VersionFolders(string folder, EnumerationOptions options)
    {
        if (Directory.Exists(PsadtLayout.ApplicationFolder(folder)) || Directory.Exists(PsadtLayout.IntuneFolder(folder)))
            return [folder];
        try
        {
            return Directory.EnumerateDirectories(folder, "*", options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
