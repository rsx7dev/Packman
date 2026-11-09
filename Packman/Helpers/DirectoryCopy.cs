using System.Diagnostics;
using System.IO;
using Packman.Services;

namespace Packman.Helpers;

/// <summary>Recursive folder copy used by package create, upgrade and the size readouts.</summary>
public static class DirectoryCopy
{
    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/>, overwriting.
    /// Top-level folders named in <paramref name="excludeTopLevelFolders"/> are skipped.
    /// </summary>
    public static void Copy(string source, string destination, CancellationToken ct = default, params string[] excludeTopLevelFolders)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Folder not found: {source}");

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            ClearReadOnly(target);
            File.Copy(file, target, overwrite: true);
            // PSADT ships some files read-only; copies keep the flag, which later blocks
            // overwrite and delete of the package. Packages are ours to edit, so drop it.
            ClearReadOnly(target);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (excludeTopLevelFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            Copy(dir, Path.Combine(destination, name), ct);
        }
    }

    /// <summary>Removes the read-only attribute from <paramref name="file"/> if it exists and has it.</summary>
    public static void ClearReadOnly(string file)
    {
        var info = new FileInfo(file);
        if (info.Exists && info.IsReadOnly)
            info.IsReadOnly = false;
    }

    /// <summary>Clears read-only on every file below <paramref name="folder"/> so it can be deleted.</summary>
    public static void ClearReadOnlyRecursive(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            ClearReadOnly(file);
    }

    /// <summary>Total size in bytes of every file below <paramref name="folder"/>; 0 when it does not exist.</summary>
    public static long TotalSize(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Skipped {file} in size total: {ex.Message}");
                ActivityLog.Shared.Warning($"Could not read the size of {file}: {ex.Message}");
            }
        }
        return total;
    }
}
