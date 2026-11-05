using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Packman.Helpers;

public static class MetadataExtractor
{
    public static (string productName, string companyName, string version) ExtractExeMetadata(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return ("", "", "");

        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            return (
                CleanProductName(vi.ProductName ?? ""),
                vi.CompanyName ?? "",
                vi.ProductVersion ?? vi.FileVersion ?? ""
            );
        }
        catch
        {
            return ("", "", "");
        }
    }

    public static string CleanProductName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return name
            .Replace(" Setup", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Installer", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Installation", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public static string ExtractNameFromFilename(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return name
            .Replace("_setup", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Setup", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_installer", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Installer", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_install", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Install", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public static string ExtractVersionFromFilename(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        var match = Regex.Match(fileName, @"(\d+\.){1,}\d+");
        return match.Success ? match.Value : "";
    }

    /// <summary>
    /// Reads the $adtSession hashtable from a v4 script.
    /// Keys: Vendor, AppName, Version, ScriptDate, ScriptAuthor.
    /// </summary>
    public static Dictionary<string, string> ExtractMetadataFromScript(string scriptPath)
    {
        var metadata = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(scriptPath)) return metadata;

        try
        {
            var script = PsadtScript.Load(scriptPath);
            metadata["Vendor"] = script.Vendor;
            metadata["AppName"] = script.AppName;
            metadata["Version"] = script.AppVersion;
            metadata["ScriptDate"] = script.ScriptDate;
            metadata["ScriptAuthor"] = script.ScriptAuthor;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error extracting metadata from script: {ex.Message}");
        }

        return metadata;
    }
}
