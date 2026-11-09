using System.IO;
using System.Text;

namespace Packman.Helpers;

/// <summary>
/// Package folder naming on the share: {root}\{Vendor}_{AppName}\{version}. Every segment
/// is sanitised with Windows rules, and any path that gets deleted or overwritten is first
/// checked to sit strictly below the root. Manufacturer, name and version are free text
/// (MSI tables, version resources, the packager typing), so none of it is trusted as a path.
/// </summary>
public static class PackagePaths
{
    // Windows rules regardless of where the code runs, so behaviour matches the share.
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>"{Vendor}_{AppName}" with spaces underscored, as every package on the share is named.</summary>
    public static string AppFolderName(string manufacturer, string appName)
        => SanitizeSegment($"{Underscore(manufacturer)}_{Underscore(appName)}");

    /// <summary>Full path of one version folder under the root. Throws when it would land outside.</summary>
    public static string VersionFolder(string root, string appFolderName, string version)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The package root is not configured.", nameof(root));

        var path = Path.Combine(root, SanitizeSegment(appFolderName), SanitizeSegment(version));
        EnsureInside(root, path);
        return path;
    }

    /// <summary>
    /// One folder name: invalid characters become '_', trailing dots and spaces go, reserved
    /// device names get a prefix. Throws when nothing usable is left (empty, ".", "..").
    /// </summary>
    public static string SanitizeSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            sb.Append(c < 32 || Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);

        var cleaned = sb.ToString().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            throw new ArgumentException($"'{value}' does not make a valid folder name.", nameof(value));

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned)))
            cleaned = "_" + cleaned;

        return cleaned;
    }

    /// <summary>Throws unless <paramref name="candidate"/> resolves to somewhere strictly below <paramref name="root"/>.</summary>
    public static void EnsureInside(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullCandidate.Length <= fullRoot.Length || !fullCandidate.StartsWith(fullRoot, comparison))
            throw new InvalidOperationException($"'{candidate}' is not inside the package root '{root}'.");
    }

    /// <summary>Deletes a package folder, refusing anything that is not below the root.</summary>
    public static void DeleteInside(string root, string folder)
    {
        EnsureInside(root, folder);
        if (!Directory.Exists(folder)) return;
        // Read-only files (PSADT ships several) make Directory.Delete throw "access denied".
        DirectoryCopy.ClearReadOnlyRecursive(folder);
        Directory.Delete(folder, recursive: true);
    }

    private static string Underscore(string s) => (s ?? "").Trim().Replace(' ', '_');
}
