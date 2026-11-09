using System.IO;
using System.Text.RegularExpressions;

namespace Packman.Helpers;

/// <summary>Values Packman reads from a PSADT v4 template's Config\config.psd1 without running it.</summary>
public static partial class PsadtConfig
{
    /// <summary>PSADT's own default for Toolkit.LogPath, as Windows resolves it.</summary>
    public const string DefaultLogFolder = @"C:\Windows\Logs\Software";

    [GeneratedRegex(@"^\s*Toolkit\s*=\s*@\{", RegexOptions.Multiline)]
    private static partial Regex ToolkitSection();

    [GeneratedRegex(@"^\s*LogPath\s*=\s*['""]([^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex LogPathLine();

    [GeneratedRegex(@"\$env:(\w+)|\$env(WinDir|SystemRoot|ProgramData|ProgramFiles|Temp)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnvToken();

    /// <summary>
    /// Toolkit.LogPath from the template's config.psd1, with environment variables expanded the
    /// way they resolve on this machine; <see cref="DefaultLogFolder"/> when the file or value is
    /// missing or can't be expanded. <paramref name="template"/> is the template folder or its .ps1.
    /// </summary>
    public static string LogFolder(string? template)
    {
        try
        {
            var folder = template?.Trim() ?? "";
            if (folder.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder) ?? "";
            var config = Path.Combine(folder, "Config", "config.psd1");
            if (folder.Length == 0 || !File.Exists(config)) return DefaultLogFolder;
            return LogFolderFromText(File.ReadAllText(config)) ?? DefaultLogFolder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DefaultLogFolder;
        }
    }

    /// <summary>Toolkit.LogPath from config.psd1 text, expanded; null when absent or not expandable.</summary>
    public static string? LogFolderFromText(string psd1)
    {
        var section = ToolkitSection().Match(psd1);
        if (!section.Success) return null;
        var line = LogPathLine().Match(psd1, section.Index);
        if (!line.Success) return null;

        var unresolved = false;
        var expanded = EnvToken().Replace(line.Groups[1].Value, m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var value = name.Equals("WinDir", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("SystemRoot")
                : Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) unresolved = true;
            return value ?? "";
        });
        return unresolved || expanded.Contains('$') ? null : expanded.TrimEnd('\\');
    }
}
