using System.IO;
using System.Text.RegularExpressions;

namespace Packman.Helpers;

/// <summary>
/// Where Remote test stages a package on the target computer. The setting names a parent
/// folder; Packman always works in its own <see cref="FolderName"/> subfolder inside it. The
/// staging script renames an untrusted base folder aside, so it must only ever touch a folder
/// Packman owns, never the parent the packager typed (which could be C:\Users or a drive root).
/// </summary>
public static partial class RemoteStaging
{
    /// <summary>The parent used when the setting is empty.</summary>
    public const string DefaultParent = @"C:\ProgramData";

    /// <summary>The subfolder Packman secures, stages into and may rename aside.</summary>
    public const string FolderName = "Packman";

    [GeneratedRegex(@"^[A-Za-z]:(\\|$)")]
    private static partial Regex DrivePath();

    private static readonly char[] InvalidChars = ['<', '>', '"', '/', '|', '?', '*', '%', '$', '\''];

    /// <summary>Why <paramref name="parent"/> can't be used, or null when it can (empty means the default).</summary>
    public static string? Problem(string? parent)
    {
        var value = parent?.Trim() ?? "";
        if (value.Length == 0) return null;
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
            return "Use a local folder on the target computer, such as D:\\Staging, not a network path.";
        if (!DrivePath().IsMatch(value))
            return "Use a full local path that starts with a drive letter, such as D:\\Staging.";
        if (value[2..].IndexOfAny(InvalidChars) >= 0 || value.IndexOf(':', 2) >= 0)
            return "The path contains characters Windows folder names can't use. Environment variables are not expanded.";
        if (value.Split('\\').Any(segment => segment is "." or ".."))
            return "Use the folder's full path without . or .. parts.";
        return null;
    }

    /// <summary>The folder Packman stages into on the target, e.g. C:\ProgramData\Packman. Throws when <paramref name="parent"/> is not usable.</summary>
    public static string BasePath(string? parent)
    {
        var problem = Problem(parent);
        if (problem != null) throw new ArgumentException(problem, nameof(parent));

        var value = string.IsNullOrWhiteSpace(parent) ? DefaultParent : parent.Trim();
        return value.TrimEnd('\\') + "\\" + FolderName;
    }

    /// <summary>The admin-share path of a local path on <paramref name="computer"/>: D:\Staging\Packman → \\PC\D$\Staging\Packman.</summary>
    public static string AdminShare(string computer, string localPath)
    {
        if (!DrivePath().IsMatch(localPath))
            throw new ArgumentException($"'{localPath}' is not a local path with a drive letter.", nameof(localPath));
        return $@"\\{computer}\{char.ToUpperInvariant(localPath[0])}${localPath[2..]}";
    }
}
