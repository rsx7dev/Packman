using System.IO;
using System.Text.RegularExpressions;

namespace Packman.Helpers;

/// <summary>Reads facts from a package's bundled PSAppDeployToolkit module manifest.</summary>
public static partial class PsadtManifest
{
    [GeneratedRegex(@"(?m)^\s*ModuleVersion\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex ModuleVersionLine();

    /// <summary>
    /// The ModuleVersion of &lt;applicationFolder&gt;/PSAppDeployToolkit/PSAppDeployToolkit.psd1,
    /// or null when the file is missing, unreadable or holds no valid version. The manifest is
    /// parsed as text, not run, and a commented-out line does not count.
    /// </summary>
    public static string? ReadModuleVersion(string applicationFolder)
    {
        try
        {
            var path = Path.Combine(applicationFolder, "PSAppDeployToolkit", "PSAppDeployToolkit.psd1");
            var match = ModuleVersionLine().Match(File.ReadAllText(path));
            return match.Success && Version.TryParse(match.Groups[1].Value, out _) ? match.Groups[1].Value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
