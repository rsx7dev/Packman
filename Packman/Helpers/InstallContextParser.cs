using System.Diagnostics;

namespace Packman.Helpers;

/// <summary>Reads RequireAdmin out of a package's $adtSession hashtable.</summary>
public static class InstallContextParser
{
    /// <summary>"User" or "System"; defaults to "System".</summary>
    public static string ExtractFromPackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            Debug.WriteLine("Error extracting install context: package path is empty");
            return "System";
        }

        var scriptPath = PsadtScript.Find(packagePath);
        if (scriptPath == null)
            return "System";

        try
        {
            return PsadtScript.Load(scriptPath).InstallContext;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error extracting install context: {ex.Message}");
            return "System";
        }
    }
}
