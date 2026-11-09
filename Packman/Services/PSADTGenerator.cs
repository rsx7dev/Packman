using Packman.Helpers;
using Packman.Models;
using System.Diagnostics;
using System.IO;

namespace Packman.Services;

/// <summary>Creates a new PSADT v4 package from the template: copy, add sources, fill in the script.</summary>
public class PSADTGenerator
{
    private readonly string _baseOutputPath;
    private readonly string _templatePath;
    private readonly AppSettings.PackageLayoutConfig _layout;

    public PSADTGenerator(string baseOutputPath, string templatePath, AppSettings.PackageLayoutConfig layout)
    {
        _baseOutputPath = baseOutputPath;
        _templatePath = templatePath;
        _layout = layout;
    }

    public PackageValidationResult ValidatePackageCreation(ApplicationInfo appInfo)
    {
        var appFolderName = PackagePaths.AppFolderName(appInfo.Manufacturer, appInfo.Name);
        var packagePath = PackagePaths.VersionFolder(_baseOutputPath, appFolderName, appInfo.Version);
        return new PackageValidationResult
        {
            PackageExists = Directory.Exists(packagePath),
            ExistingPath = packagePath,
            ProposedPath = packagePath,
            AppFolderName = appFolderName,
            Version = appInfo.Version
        };
    }

    public Task<PackageCreationResult> CreatePackageAsync(ApplicationInfo appInfo,
        bool overwriteExisting = false, CancellationToken cancellationToken = default)
        => Task.Run(() => CreatePackageCore(appInfo, overwriteExisting, cancellationToken), cancellationToken);

    private PackageCreationResult CreatePackageCore(ApplicationInfo appInfo,
        bool overwriteExisting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var appFolderName = PackagePaths.AppFolderName(appInfo.Manufacturer, appInfo.Name);
        var packagePath = PackagePaths.VersionFolder(_baseOutputPath, appFolderName, appInfo.Version);

        // Fail on a bad template before anything on the share is touched.
        var template = ResolveTemplatePath();
        var activity = ActivityLog.Shared;
        activity.Info($"Package folder: {packagePath}");

        var replacing = Directory.Exists(packagePath);
        if (replacing && !overwriteExisting)
            throw new InvalidOperationException(
                $"Package version {appInfo.Version} already exists for {appFolderName}.");

        // A replacement is built beside the existing package and swapped in at the end, so
        // a cancel or a failed copy leaves the existing package exactly as it was.
        var buildPath = replacing ? packagePath + ReplacementSuffix : packagePath;
        if (replacing)
        {
            activity.Info("Building the replacement beside the existing package version…");
            PackagePaths.DeleteInside(_baseOutputPath, buildPath);   // left by an interrupted run
        }

        try
        {
            activity.Info($"Copying the PSADT template from {template}");
            DirectoryCopy.Copy(template, PsadtLayout.ApplicationFolder(buildPath), cancellationToken);
            // Created up front so new and upgraded packages share one layout.
            Directory.CreateDirectory(PsadtLayout.IntuneFolder(buildPath));
            foreach (var folder in PsadtLayout.OptionalFolders(_layout.IconFolder, _layout.ExtraFolders))
                Directory.CreateDirectory(Path.Combine(buildPath, folder));
            activity.Info("Copying the source files…");
            CopySourceFiles(appInfo.SourcesPath, buildPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            activity.Info("Writing the application details into the PSADT script…");
            var warnings = ModifyScript(buildPath, appInfo).ToList();

            // Past this point the new package is complete; the swap is not cancelled halfway.
            if (replacing)
            {
                activity.Info("Replacing the existing package version…");
                if (SwapIn(buildPath, packagePath) is { } leftover) warnings.Add(leftover);
            }
            foreach (var warning in warnings) activity.Warning(warning);

            Debug.WriteLine($"Package created at: {packagePath}");
            return new PackageCreationResult(packagePath, warnings);
        }
        catch
        {
            // Never leave a half-built version folder behind; it would block the next attempt.
            try { PackagePaths.DeleteInside(_baseOutputPath, buildPath); }
            catch (Exception cleanup) { Debug.WriteLine($"Could not remove the half-built package: {cleanup.Message}"); }
            throw;
        }
    }

    private const string ReplacementSuffix = ".packman-new";
    private const string ReplacedSuffix = ".packman-old";

    /// <summary>
    /// Puts the finished replacement where the existing package is. The existing one is moved
    /// aside first and only deleted once the new one is in place; if the new one cannot be
    /// moved in, the existing one goes back. Returns a warning when the old copy could not be
    /// removed afterwards, or null.
    /// </summary>
    private string? SwapIn(string buildPath, string packagePath)
    {
        var replaced = packagePath + ReplacedSuffix;
        PackagePaths.DeleteInside(_baseOutputPath, replaced);
        Directory.Move(packagePath, replaced);
        try
        {
            Directory.Move(buildPath, packagePath);
        }
        catch
        {
            Directory.Move(replaced, packagePath);
            throw;
        }

        try
        {
            PackagePaths.DeleteInside(_baseOutputPath, replaced);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The previous package could not be removed and is still at {replaced}: {ex.Message}";
        }
    }

    // Accepts either the .ps1 path or the folder holding it.
    private string ResolveTemplatePath()
    {
        var p = _templatePath?.Trim();
        if (!string.IsNullOrEmpty(p))
        {
            if (p.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                return Path.GetDirectoryName(p)!;
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, PsadtLayout.ScriptName)))
                return p;
        }

        throw new DirectoryNotFoundException(
            $"PSADT template not found. Searched: '{_templatePath}'. " +
            $"Set the PSADT Template Path in Settings > Network Paths to the PSADT folder containing {PsadtLayout.ScriptName}.");
    }

    // SourcesPath is a file, a ';'-separated list of files, or a folder. A source that is
    // named but not there fails the build: a package without its installer is not a package.
    private static void CopySourceFiles(string sourcesPath, string packagePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcesPath)) return;

        var dest = PsadtLayout.FilesFolder(packagePath);
        Directory.CreateDirectory(dest);

        if (sourcesPath.Contains(';'))
        {
            foreach (var p in sourcesPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(p)) throw SourceNotFound(p);
                File.Copy(p, Path.Combine(dest, Path.GetFileName(p)), true);
            }
        }
        else if (File.Exists(sourcesPath))
            File.Copy(sourcesPath, Path.Combine(dest, Path.GetFileName(sourcesPath)), true);
        else if (Directory.Exists(sourcesPath))
            DirectoryCopy.Copy(sourcesPath, dest, ct);
        else
            throw SourceNotFound(sourcesPath);
    }

    private static FileNotFoundException SourceNotFound(string path) =>
        new($"The source installer was not found: {path}. Check the path, or choose the file with Browse.");

    private static IReadOnlyList<string> ModifyScript(string packagePath, ApplicationInfo appInfo)
    {
        var scriptPath = PsadtLayout.ScriptPath(packagePath);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"{PsadtLayout.ScriptName} not found in: {packagePath}");

        var script = PsadtScript.Load(scriptPath);
        var warnings = new List<string>();

        // The template may carry the upstream signature; the edits below invalidate it anyway.
        script.StripSignatureBlock();

        void Set(string key, string value)
        {
            if (!script.SetSessionValue(key, value))
                warnings.Add($"{key} is not in the template's $adtSession block; set it by hand.");
        }

        Set("AppVendor", appInfo.Manufacturer);
        Set("AppName", appInfo.Name);
        Set("AppVersion", appInfo.Version);
        Set("AppArch", appInfo.Architecture);
        Set("AppScriptDate", PsadtScript.TodayStamp);
        Set("AppScriptAuthor", string.IsNullOrWhiteSpace(appInfo.Author) ? Environment.UserName : appInfo.Author);

        var userContext = appInfo.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase);
        if (!script.SetSessionBool("RequireAdmin", !userContext))
            warnings.Add("RequireAdmin is not in the template's $adtSession block; set it by hand.");

        var msi = appInfo.PackageType == "MSI";
        var installer = ResolveInstallerName(appInfo, PsadtLayout.FilesFolder(packagePath), msi)
                        ?? (msi ? appInfo.Name + ".msi" : "setup.exe");

        if (!script.InsertAfterSection(PsadtScript.InstallSection,
                "", msi ? "## MSI Installation" : "## EXE Installation", PsadtScript.InstallCommand(msi, installer, appInfo.InstallArguments)))
            warnings.Add($"No '<Perform {PsadtScript.InstallSection} tasks here>' marker in the template; add the install command by hand.");

        if (!script.InsertAfterSection(PsadtScript.UninstallSection,
                "", msi ? "## Uninstall MSI" : "## Uninstall EXE", PsadtScript.UninstallCommand(msi, installer, appInfo.MsiProductCode, appInfo.UninstallArguments)))
            warnings.Add($"No '<Perform {PsadtScript.UninstallSection} tasks here>' marker in the template; add the uninstall command by hand.");

        script.Save();
        return warnings;
    }

    // A single source file is referenced by its own name; a list or folder by the first
    // installer that landed in Files\.
    private static string? ResolveInstallerName(ApplicationInfo appInfo, string filesFolder, bool msi)
    {
        var sources = appInfo.SourcesPath;
        if (!string.IsNullOrWhiteSpace(sources) && !sources.Contains(';') && File.Exists(sources))
            return Path.GetFileName(sources);

        if (!Directory.Exists(filesFolder)) return null;

        var names = Directory.EnumerateFiles(filesFolder).Select(Path.GetFileName).OfType<string>().ToList();
        var preferred = msi ? ".msi" : ".exe";
        return names.FirstOrDefault(n => n.EndsWith(preferred, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                                       || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }
}
