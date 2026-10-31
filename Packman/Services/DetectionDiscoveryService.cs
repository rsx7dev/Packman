using Packman.Models;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Packman.Services;

/// <summary>
/// Finds a file-based detection rule by searching a test machine (over C$, or this PC directly) for the
/// executable the package just installed. Mainly for EXE packages, which carry no
/// product code to detect on.
/// </summary>
public class DetectionDiscoveryService
{
    public class DiscoveryResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
        public List<DetectionRule> SuggestedRules { get; set; } = new();
        public string AppName { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public List<string> Messages { get; set; } = new();
    }

    private static readonly Regex ExcludedExeNames =
        new("unins|uninstall|uninst|setup|helper", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<DiscoveryResult> DiscoverAsync(
        string computerName, string appName, string appVersion, string sourceInstallerPath = "")
        => await Task.Run(() => Discover(computerName, appName, appVersion, sourceInstallerPath));

    private DiscoveryResult Discover(string computerName, string appName, string appVersion, string sourceInstallerPath)
    {
        var result = new DiscoveryResult();

        try
        {
            result.Messages.Add($"Searching for {appName} (version {appVersion}) on {computerName}");

            // Installer metadata helps pick the right executable.
            FileVersionInfo? sourceMetadata = null;
            if (!string.IsNullOrEmpty(sourceInstallerPath) && File.Exists(sourceInstallerPath))
            {
                try
                {
                    sourceMetadata = FileVersionInfo.GetVersionInfo(sourceInstallerPath);
                    result.Messages.Add($"Source metadata — product: {sourceMetadata.ProductName}, company: {sourceMetadata.CompanyName}, version: {sourceMetadata.FileVersion}");
                }
                catch (Exception ex)
                {
                    result.Messages.Add($"Could not read metadata from source: {ex.Message}");
                }
            }

            var searchTerms = BuildSearchTerms(appName, sourceMetadata);
            if (searchTerms.Count == 0)
            {
                result.ErrorMessage = $"Could not derive search terms from app name '{appName}'";
                return result;
            }
            result.Messages.Add($"Search terms: {string.Join(", ", searchTerms)}");

            // Score folders by app-name term matches.
            var scoredDirs = new List<(DirectoryInfo Dir, int Score)>();
            foreach (string root in GetSearchRoots(computerName))
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in GetDirectoriesWithChildren(root))
                {
                    int score = searchTerms.Count(t => dir.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
                    if (score >= 1) scoredDirs.Add((dir, score));
                }
            }

            scoredDirs = scoredDirs.OrderByDescending(d => d.Score).ToList();
            result.Messages.Add($"Found {scoredDirs.Count} candidate folders");

            // Drop weak matches when strong ones exist.
            if (scoredDirs.Count > 0 && scoredDirs[0].Score >= 3)
                scoredDirs = scoredDirs.Where(d => d.Score >= 2).ToList();

            FileInfo? foundExe = null;
            DirectoryInfo? foundDir = null;
            foreach (var (dir, score) in scoredDirs)
            {
                var exes = GetExecutables(dir);
                if (exes.Count == 0) continue;

                foundExe = sourceMetadata != null
                    ? PickBestExecutable(exes, sourceMetadata, result)
                    : exes.OrderByDescending(f => f.Length).First();
                foundDir = dir;
                result.Messages.Add($"Selected {foundExe.Name} in {dir.FullName} (folder score {score})");
                break;
            }

            if (foundExe == null || foundDir == null)
            {
                result.ErrorMessage = $"No matching application files found on {computerName}";
                return result;
            }

            string localDir = ToLocalPath(foundDir.FullName, computerName);
            var versionInfo = FileVersionInfo.GetVersionInfo(foundExe.FullName);
            // FileVersion is free text ("1, 0, 0, 1", "12.0 build 3"); Intune compares a.b.c.d.
            var fileVersion = NormalizedFileVersion(versionInfo);

            result.AppName = string.IsNullOrEmpty(versionInfo.ProductName) ? appName : versionInfo.ProductName;
            result.AppVersion = fileVersion ?? appVersion;
            result.Publisher = versionInfo.CompanyName ?? "";
            result.InstallLocation = localDir;

            result.SuggestedRules.Add(new DetectionRule
            {
                Type = DetectionRuleType.File,
                Path = localDir,
                FileOrFolderName = foundExe.Name,
                DetectionType = "exists",
                CheckVersion = false,
                Check32BitOn64System = true
            });

            if (fileVersion != null)
            {
                result.SuggestedRules.Add(new DetectionRule
                {
                    Type = DetectionRuleType.File,
                    Path = localDir,
                    FileOrFolderName = foundExe.Name,
                    DetectionType = "version",
                    DetectionValue = fileVersion,
                    Operator = "greaterThanOrEqual",
                    CheckVersion = true,
                    Check32BitOn64System = true
                });
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Discovery failed: {ex.Message}";
            Debug.WriteLine($"Detection discovery error: {ex}");
        }

        return result;
    }

    // Machine-wide locations plus AppData\Local for user-context installs.
    internal static List<string> GetSearchRoots(string computerName)
    {
        // This PC is read directly; its admin share may be disabled.
        string cShare = computerName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? @"C:\"
            : $@"\\{computerName}\C$";
        var roots = new List<string>
        {
            Path.Combine(cShare, "Program Files"),
            Path.Combine(cShare, "Program Files (x86)"),
            Path.Combine(cShare, "ProgramData")
        };

        string usersDir = Path.Combine(cShare, "Users");
        if (Directory.Exists(usersDir))
        {
            foreach (string profile in SafeGetDirectoryPaths(usersDir))
            {
                string name = Path.GetFileName(profile);
                if (name is "Public" or "Default" or "Default User" or "All Users") continue;
                roots.Add(Path.Combine(profile, "AppData", "Local"));
            }
        }

        return roots;
    }

    // One nested level, for Vendor\Product layouts.
    private static List<DirectoryInfo> GetDirectoriesWithChildren(string root)
    {
        var dirs = new List<DirectoryInfo>();
        foreach (var dir in SafeGetDirectories(new DirectoryInfo(root)))
        {
            dirs.Add(dir);
            dirs.AddRange(SafeGetDirectories(dir));
        }
        return dirs;
    }

    // Executables one level down, minus installers/uninstallers.
    private static List<FileInfo> GetExecutables(DirectoryInfo dir)
    {
        var files = SafeGetExeFiles(dir);
        foreach (var sub in SafeGetDirectories(dir))
            files.AddRange(SafeGetExeFiles(sub));
        return files.Where(f => !ExcludedExeNames.IsMatch(f.Name)).ToList();
    }

    private static FileInfo PickBestExecutable(List<FileInfo> exes, FileVersionInfo sourceMetadata, DiscoveryResult result)
    {
        FileInfo? best = null;
        int bestScore = 0;

        foreach (var exe in exes)
        {
            FileVersionInfo vi;
            try { vi = FileVersionInfo.GetVersionInfo(exe.FullName); }
            catch { continue; }

            int score = 0;
            if (!string.IsNullOrEmpty(sourceMetadata.ProductName) && vi.ProductName == sourceMetadata.ProductName) score += 10;
            if (!string.IsNullOrEmpty(sourceMetadata.CompanyName) && vi.CompanyName == sourceMetadata.CompanyName) score += 5;
            if (!string.IsNullOrEmpty(sourceMetadata.FileVersion) && vi.FileVersion == sourceMetadata.FileVersion) score += 3;
            if (!string.IsNullOrEmpty(sourceMetadata.InternalName) && vi.InternalName == sourceMetadata.InternalName) score += 2;

            if (score > bestScore)
            {
                bestScore = score;
                best = exe;
            }
        }

        if (best != null)
        {
            result.Messages.Add($"Metadata-matched executable: {best.Name} (score {bestScore})");
            return best;
        }

        // Fall back to the largest non-installer executable.
        return exes.OrderByDescending(f => f.Length).First();
    }

    private static List<string> BuildSearchTerms(string appName, FileVersionInfo? sourceMetadata)
    {
        var terms = new List<string>();

        void AddTerms(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string cleaned = Regex.Replace(text.Replace('_', ' '), @"[^\w\s]", "");
            terms.AddRange(cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 2));
        }

        AddTerms(appName);
        AddTerms(sourceMetadata?.ProductName);
        AddTerms(sourceMetadata?.CompanyName);

        // Longer terms are more specific.
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length).ToList();
    }

    /// <summary>
    /// Local path as Intune should see it. A folder under the test user's profile becomes
    /// %LOCALAPPDATA%, so the rule works for every user rather than the one who tested.
    /// </summary>
    internal static string ToLocalPath(string uncPath, string computerName)
    {
        var local = uncPath.Replace($@"\\{computerName}\C$", "C:", StringComparison.OrdinalIgnoreCase);
        var profile = Regex.Match(local, @"^C:\\Users\\[^\\]+\\AppData\\Local(?<rest>\\.*)?$", RegexOptions.IgnoreCase);
        return profile.Success ? "%LOCALAPPDATA%" + profile.Groups["rest"].Value : local;
    }

    /// <summary>"a.b.c.d" from the numeric version parts, or null when the file has none.</summary>
    internal static string? NormalizedFileVersion(FileVersionInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.FileVersion)) return null;
        return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
    }

    private static List<DirectoryInfo> SafeGetDirectories(DirectoryInfo dir)
    {
        try { return dir.GetDirectories().ToList(); }
        catch { return new List<DirectoryInfo>(); }
    }

    private static List<string> SafeGetDirectoryPaths(string path)
    {
        try { return Directory.GetDirectories(path).ToList(); }
        catch { return new List<string>(); }
    }

    private static List<FileInfo> SafeGetExeFiles(DirectoryInfo dir)
    {
        try { return dir.GetFiles("*.exe").ToList(); }
        catch { return new List<FileInfo>(); }
    }
}
