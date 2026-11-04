using System.Diagnostics;
using System.IO;

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
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (excludeTopLevelFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            Copy(dir, Path.Combine(destination, name), ct);
        }
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
            }
        }
        return total;
    }
}
