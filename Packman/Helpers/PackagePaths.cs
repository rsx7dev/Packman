using System.IO;
using System.Text;

namespace Packman.Helpers;

/// <summary>
/// Package folder naming on the share: {root}\{app folder}\{version}, where the app folder
/// follows the name format in Settings ▸ Package layout ({Vendor}_{AppName} by default). Every segment
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

    /// <summary>The package folder name format used when Settings ▸ Package layout sets none.</summary>
    public const string DefaultFolderTemplate = "%vendor%_%appName%";

    private const string VendorToken = "%vendor%";
    private const string AppNameToken = "%appName%";

    /// <summary>"{Vendor}_{AppName}" with spaces underscored: the default package folder name.</summary>
    public static string AppFolderName(string manufacturer, string appName)
        => AppFolder(DefaultFolderTemplate, manufacturer, appName);

    /// <summary>
    /// Why <paramref name="template"/> can't name package folders, or null when it can (blank
    /// means <see cref="DefaultFolderTemplate"/>). One or two folder levels of %vendor%,
    /// %appName% and plain text; the version is always its own subfolder below them.
    /// </summary>
    public static string? FolderTemplateProblem(string? template)
    {
        var value = template?.Trim() ?? "";
        if (value.Length == 0) return null;
        if (value.Contains("%appVersion%", StringComparison.OrdinalIgnoreCase))
            return "Leave out %appVersion%: each version always gets its own subfolder.";
        if (!value.Contains(AppNameToken, StringComparison.OrdinalIgnoreCase))
            return "Include %appName%, or different applications would share one folder.";

        var levels = value.Split('\\', '/');
        if (levels.Length > 2)
            return @"Use at most one \ (two folder levels), for example %vendor%\%appName%.";
        foreach (var level in levels)
        {
            var literal = level.Replace(VendorToken, "", StringComparison.OrdinalIgnoreCase)
                               .Replace(AppNameToken, "", StringComparison.OrdinalIgnoreCase);
            if (level.Trim().Length == 0) return @"A folder level is empty. Remove the extra \.";
            if (literal.Contains('%')) return "Unknown token. Use %vendor% and %appName%.";
            if (literal.Any(c => c < 32 || Array.IndexOf(InvalidChars, c) >= 0))
                return "The format contains characters Windows folder names can't use.";
            if (literal.Trim() is "." or "..") return "A folder level can't be . or ..";
        }
        return null;
    }

    /// <summary>
    /// The app's folder below the root for a name format: one or two sanitised levels joined
    /// by '\'. Token values have spaces underscored and can never add a level of their own.
    /// A level that comes out empty (no vendor, say) is dropped. Throws on an unusable format.
    /// </summary>
    public static string AppFolder(string? template, string manufacturer, string appName)
    {
        var format = string.IsNullOrWhiteSpace(template) ? DefaultFolderTemplate : template.Trim();
        if (FolderTemplateProblem(format) is { } problem)
            throw new ArgumentException(problem, nameof(template));

        // Split first, expand after: a '\' inside a vendor or app name is sanitised away, not followed.
        var levels = format.Split('\\', '/')
            .Select(level => level.Replace(VendorToken, Underscore(manufacturer), StringComparison.OrdinalIgnoreCase)
                                  .Replace(AppNameToken, Underscore(appName), StringComparison.OrdinalIgnoreCase))
            .Where(level => level.Trim().Length > 0)
            .Select(SanitizeSegment)
            .ToList();
        if (levels.Count == 0)
            throw new ArgumentException("The application name does not make a valid folder name.", nameof(appName));
        return string.Join('\\', levels);
    }

    /// <summary>Full path of one version folder for a name format. Throws when it would land outside the root.</summary>
    public static string PackageFolder(string root, string? template, string manufacturer, string appName, string version)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The package root is not configured.", nameof(root));

        var path = Path.Combine(root, AppFolder(template, manufacturer, appName), SanitizeSegment(version));
        EnsureInside(root, path);
        return path;
    }

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
