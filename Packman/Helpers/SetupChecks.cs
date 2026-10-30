using System.IO;

namespace Packman.Helpers;

/// <summary>Result of one setup check. Kind is "ok", "warn" or "bad"; only "bad" blocks the step.</summary>
public sealed record SetupCheck(string Kind, string Message);

/// <summary>Path checks for the first-run setup. The messages are what the user reads.</summary>
public static class SetupChecks
{
    public static SetupCheck ApplicationsRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new("bad", "Enter the folder where your packages are kept.");
        if (!Directory.Exists(path))
            return new("bad", "This folder does not exist or cannot be reached. Create it, or check the name and your access.");

        var probe = Path.Combine(path, $".packman-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "");
            return new("ok", "Folder found and writable.");
        }
        catch (Exception)
        {
            return new("warn", "Folder found, but it is read-only for you. Packman cannot write packman.id there, " +
                "so it will not link packages to their Intune app. You can continue and fix access later.");
        }
        finally
        {
            try { File.Delete(probe); } catch (Exception) { }
        }
    }

    /// <summary>Accepts the template folder or the Invoke-AppDeployToolkit.ps1 inside it.</summary>
    public static SetupCheck Template(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new("bad", "Enter the folder of your PSADT v4 template.");
        var folder = File.Exists(path) && Path.GetFileName(path).Equals(PsadtLayout.ScriptName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path)!
            : path;
        if (!Directory.Exists(folder))
            return new("bad", "This folder does not exist or cannot be reached.");

        var hasScript = File.Exists(Path.Combine(folder, PsadtLayout.ScriptName));
        var hasToolkit = Directory.Exists(Path.Combine(folder, PsadtLayout.ToolkitFolderName));
        if (hasScript && hasToolkit)
            return new("ok", "PSADT v4 template found.");
        var missing = !hasScript && !hasToolkit
            ? $"{PsadtLayout.ScriptName} and the {PsadtLayout.ToolkitFolderName} folder are"
            : hasScript ? $"the {PsadtLayout.ToolkitFolderName} folder is" : $"{PsadtLayout.ScriptName} is";
        return new("bad", $"This is not a PSADT v4 template: {missing} missing. Choose the folder you extracted from the PSADT release.");
    }

    public static SetupCheck IntuneWinAppUtil(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new("bad", "Enter the path of IntuneWinAppUtil.exe.");
        if (Directory.Exists(path))
            return new("bad", "This is a folder. Point to the IntuneWinAppUtil.exe file inside it.");
        if (!File.Exists(path))
            return new("bad", "This file does not exist or cannot be reached.");
        if (!Path.GetFileName(path).Equals("IntuneWinAppUtil.exe", StringComparison.OrdinalIgnoreCase))
            return new("bad", "This file is not IntuneWinAppUtil.exe.");
        return new("ok", "IntuneWinAppUtil.exe found.");
    }
}
