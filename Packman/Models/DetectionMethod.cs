namespace Packman.Models;

/// <summary>The detection methods Packman can build a Win32 detection rule from.</summary>
public static class DetectionMethod
{
    public const string FileExists = "File exists";
    public const string FileVersion = "File version";
    public const string RegistryKey = "Registry key exists";
    public const string MsiProductCode = "MSI product code";

    public static List<string> All => new() { FileExists, FileVersion, RegistryKey, MsiProductCode };
}

/// <summary>Registry root keys accepted by the Intune registry detection rule.</summary>
public static class RegistryHiveNames
{
    public const string LocalMachine = "HKEY_LOCAL_MACHINE";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        LocalMachine,
        "HKEY_CURRENT_USER",
        "HKEY_CLASSES_ROOT",
        "HKEY_USERS",
        "HKEY_CURRENT_CONFIG",
    };

    private static readonly (string Short, string Long)[] ShortNames =
    [
        ("HKLM", LocalMachine), ("HKCU", "HKEY_CURRENT_USER"), ("HKCR", "HKEY_CLASSES_ROOT"),
        ("HKU", "HKEY_USERS"), ("HKCC", "HKEY_CURRENT_CONFIG"),
    ];

    /// <summary>
    /// Joins a hive and a sub-key into the keyPath Graph expects. A sub-key pasted whole from
    /// regedit or PowerShell ("Computer\HKEY_LOCAL_MACHINE\SOFTWARE\X", "HKLM:\SOFTWARE\X")
    /// already names its hive: that hive is used and not repeated.
    /// </summary>
    public static string Combine(string hive, string subKey)
    {
        var key = subKey.Trim().Trim('\\');
        if (key.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase)) key = key["Computer\\".Length..];

        var cut = key.IndexOf('\\');
        var first = (cut < 0 ? key : key[..cut]).TrimEnd(':');
        var named = All.FirstOrDefault(h => h.Equals(first, StringComparison.OrdinalIgnoreCase))
                    ?? ShortNames.FirstOrDefault(n => n.Short.Equals(first, StringComparison.OrdinalIgnoreCase)).Long;
        if (named != null)
        {
            hive = named;
            key = cut < 0 ? "" : key[(cut + 1)..].Trim('\\');
        }
        return string.IsNullOrEmpty(key) ? hive : $"{hive}\\{key}";
    }
}

/// <summary>Checks on hand-typed detection values. Intune accepts a malformed one and then never detects the app.</summary>
public static partial class DetectionInput
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:\{(?<g>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}|(?<g>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}))$")]
    private static partial System.Text.RegularExpressions.Regex Guid();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial System.Text.RegularExpressions.Regex Version();

    public const string ProductCodeProblem = "The MSI product code must be a GUID like {12345678-ABCD-1234-ABCD-1234567890AB}.";
    public const string VersionProblem = "The version must be one to four numbers separated by dots, like 1.2.3.4.";

    /// <summary>The product code as Windows Installer registers it, "{UPPER-CASE-GUID}", or null when it is not a GUID.</summary>
    public static string? NormalizeProductCode(string? value)
    {
        var match = Guid().Match(value?.Trim() ?? "");
        return match.Success ? "{" + match.Groups["g"].Value.ToUpperInvariant() + "}" : null;
    }

    /// <summary>Whether Intune's version comparison can read <paramref name="value"/>: one to four numeric parts.</summary>
    public static bool IsVersion(string? value) => Version().IsMatch(value?.Trim() ?? "");
}
