using System.IO;

namespace Packman.Helpers;

/// <summary>One package found on the share: an app folder, one of its version folders and what is in it.</summary>
public sealed record SharePackage(string AppFolderName, string Version, string FolderPath, bool HasScript, bool HasIntuneWin);

/// <summary>
/// Lists the packages on the IntuneApplications share. Layout is {root}\{Vendor}_{AppName}\{version};
/// a version folder counts as a package when it holds the PSADT script (in the folder or its
/// Application subfolder) or a built .intunewin (in the folder or its Intune subfolder).
/// Read-only: it only enumerates folders and checks for files. Newest version comes first.
/// </summary>
public static class ApplicationShareScanner
{
    public static IReadOnlyList<SharePackage> Scan(string shareRoot)
    {
        var packages = new List<SharePackage>();
        if (string.IsNullOrWhiteSpace(shareRoot) || !Directory.Exists(shareRoot))
            return packages;

        foreach (var appFolder in Directory.EnumerateDirectories(shareRoot))
        {
            foreach (var versionFolder in Directory.EnumerateDirectories(appFolder))
            {
                var hasScript = File.Exists(Path.Combine(versionFolder, PsadtLayout.ScriptName))
                    || File.Exists(Path.Combine(versionFolder, "Application", PsadtLayout.ScriptName));
                var hasIntuneWin = HasIntuneWin(versionFolder) || HasIntuneWin(Path.Combine(versionFolder, "Intune"));
                if (hasScript || hasIntuneWin)
                    packages.Add(new SharePackage(Path.GetFileName(appFolder), Path.GetFileName(versionFolder),
                        versionFolder, hasScript, hasIntuneWin));
            }
        }

        // Numeric order, so 1.10 beats 1.9; anything unparsable sorts last, by name.
        return packages
            .OrderBy(p => p.AppFolderName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(p => Version.TryParse(p.Version, out var v) ? v : new Version(0, 0))
            .ThenByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasIntuneWin(string folder)
        => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.intunewin").Any();
}
