using System.IO;

namespace Packman.Helpers;

/// <summary>Locates files in a PSADT v4 package.</summary>
public static class FolderBrowserHelper
{
    /// <summary>True for a package root or its Application folder.</summary>
    public static bool ValidatePackageStructure(string packagePath)
    {
        if (string.IsNullOrEmpty(packagePath) || !Directory.Exists(packagePath))
            return false;

        var applicationFolder = PsadtLayout.ApplicationFolder(packagePath);
        if (!Directory.Exists(applicationFolder))
            return HasPSADTExecutable(packagePath);

        return HasPSADTExecutable(applicationFolder);
    }

    private static bool HasPSADTExecutable(string folderPath)
    {
        var v4Exe = Path.Combine(folderPath, "Invoke-AppDeployToolkit.exe");
        var v4Ps1 = Path.Combine(folderPath, "Invoke-AppDeployToolkit.ps1");
        return File.Exists(v4Exe) && File.Exists(v4Ps1);
    }

    /// <summary>Path to Invoke-AppDeployToolkit.exe, or null.</summary>
    public static string? GetPSADTExecutablePath(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return null;

        var v4Exe = Path.Combine(folderPath, "Invoke-AppDeployToolkit.exe");
        return File.Exists(v4Exe) ? v4Exe : null;
    }

    /// <summary>Path to Invoke-AppDeployToolkit.ps1, or null.</summary>
    public static string? GetPSADTScriptPath(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return null;

        var script = Path.Combine(folderPath, PsadtLayout.ScriptName);
        return File.Exists(script) ? script : null;
    }

    /// <summary>Package root, whether the user picked it or its Application folder.</summary>
    public static string GetPackageRootPath(string selectedPath)
    {
        if (string.IsNullOrEmpty(selectedPath))
            return selectedPath;

        if (HasPSADTExecutable(selectedPath))
        {
            var parent = Directory.GetParent(selectedPath);
            if (parent != null)
                return parent.FullName;
        }

        return selectedPath;
    }
}
