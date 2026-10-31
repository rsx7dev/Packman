using System.IO;

namespace Packman.Helpers;

/// <summary>Fixed file and folder names in a PSADT v4 package. v4 only.</summary>
public static class PsadtLayout
{
    public const string ApplicationFolderName = "Application";
    public const string FilesFolderName = "Files";
    public const string IntuneFolderName = "Intune";
    public const string IconFolderName = "Icon";

    /// <summary>The PSADT module folder inside a template or an Application folder.</summary>
    public const string ToolkitFolderName = "PSAppDeployToolkit";

    /// <summary>PSADT files and the installer; the only folder packed into the .intunewin.</summary>
    public static string ApplicationFolder(string packagePath) => Path.Combine(packagePath, ApplicationFolderName);

    /// <summary>The installer files PSADT deploys.</summary>
    public static string FilesFolder(string packagePath) => Path.Combine(ApplicationFolder(packagePath), FilesFolderName);

    /// <summary>The built .intunewin and packman.id.</summary>
    public static string IntuneFolder(string packagePath) => Path.Combine(packagePath, IntuneFolderName);

    public static string IconFolder(string packagePath) => Path.Combine(packagePath, IconFolderName);

    public static string ScriptPath(string packagePath) => Path.Combine(ApplicationFolder(packagePath), ScriptName);

    /// <summary>The folders a new package gets beyond Application and Intune: Icon when on, then the extras in order.</summary>
    public static List<string> OptionalFolders(bool iconFolder, IEnumerable<string> extraFolders)
    {
        var folders = new List<string>();
        if (iconFolder) folders.Add(IconFolderName);
        folders.AddRange(extraFolders);
        return folders;
    }

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly string[] ReservedDeviceNames =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).Select(i => $"COM{i}"), .. Enumerable.Range(0, 10).Select(i => $"LPT{i}")];

    /// <summary>
    /// Why <paramref name="name"/> cannot be an extra folder, as one line for the user, or null when it can.
    /// The first failing rule wins; <paramref name="name"/> is expected trimmed.
    /// </summary>
    public static string? ExtraFolderProblem(string name, IEnumerable<string> existing)
    {
        if (name.Length == 0) return "Enter a folder name.";
        if (name.Contains('\\') || name.Contains('/')) return "Use a single folder name, without \\ or /.";
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl))
            return "A folder name cannot contain < > : \" | ? * or control characters.";
        if (name.EndsWith('.')) return "A folder name cannot end with a dot.";
        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return $"{stem.ToUpperInvariant()} is a reserved Windows name.";
        if (new[] { ApplicationFolderName, IntuneFolderName, IconFolderName }.Contains(name, StringComparer.OrdinalIgnoreCase))
            return $"{name} is already part of every package.";
        if (existing.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"{name} is already in the list.";
        return null;
    }

    /// <summary>Entry point Intune launches; sent as setupFilePath.</summary>
    public const string SetupFileName = "Invoke-AppDeployToolkit.exe";

    /// <summary>Deployment script holding the metadata and install logic.</summary>
    public const string ScriptName = "Invoke-AppDeployToolkit.ps1";

    /// <summary>PSADT's own default; appends no -DeployMode switch.</summary>
    public const string DeployModeDefault = "Auto";

    /// <summary>The -DeployMode values the frontend script accepts, plus Auto for "leave it to PSADT".</summary>
    public static readonly string[] DeployModes = ["Auto", "Interactive", "NonInteractive", "Silent"];

    /// <summary>
    /// Appends -DeployMode to a command line. Auto is the PSADT default so it is left off,
    /// and a command that already sets the switch is used as written.
    /// </summary>
    public static string WithDeployMode(string? command, string deployMode)
    {
        command = (command ?? "").Trim();
        if (deployMode == DeployModeDefault) return command;
        if (command.Contains("-DeployMode", StringComparison.OrdinalIgnoreCase)) return command;
        return $"{command} -DeployMode {deployMode}";
    }
}
