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
        => Task.Run(() => CreatePackageCoreAsync(appInfo, overwriteExisting, cancellationToken), cancellationToken);

    private async Task<PackageCreationResult> CreatePackageCoreAsync(ApplicationInfo appInfo,
        bool overwriteExisting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var appFolderName = PackagePaths.AppFolderName(appInfo.Manufacturer, appInfo.Name);
        var packagePath = PackagePaths.VersionFolder(_baseOutputPath, appFolderName, appInfo.Version);

        // Fail on a bad template before anything on the share is touched.
        var template = ResolveTemplatePath();

        if (Directory.Exists(packagePath))
        {
            if (!overwriteExisting)
                throw new InvalidOperationException(
                    $"Package version {appInfo.Version} already exists for {appFolderName}.");

            PackagePaths.DeleteInside(_baseOutputPath, packagePath);
            // Deletes on a share settle a moment after the call returns.
            await Task.Delay(200, cancellationToken);
        }

        try
        {
            DirectoryCopy.Copy(template, PsadtLayout.ApplicationFolder(packagePath), cancellationToken);
            // Created up front so new and upgraded packages share one layout.
            Directory.CreateDirectory(PsadtLayout.IntuneFolder(packagePath));
            foreach (var folder in PsadtLayout.OptionalFolders(_layout.IconFolder, _layout.ExtraFolders))
                Directory.CreateDirectory(Path.Combine(packagePath, folder));
            CopySourceFiles(appInfo.SourcesPath, packagePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var warnings = ModifyScript(packagePath, appInfo);

            Debug.WriteLine($"Package created at: {packagePath}");
            return new PackageCreationResult(packagePath, warnings);
        }
        catch
        {
            // Never leave a half-built version folder behind; it would block the next attempt.
            try { PackagePaths.DeleteInside(_baseOutputPath, packagePath); }
            catch (Exception cleanup) { Debug.WriteLine($"Could not remove the half-built package: {cleanup.Message}"); }
            throw;
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

    // SourcesPath is a file, a ';'-separated list of files, or a folder.
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
                if (File.Exists(p)) File.Copy(p, Path.Combine(dest, Path.GetFileName(p)), true);
            }
        }
        else if (File.Exists(sourcesPath))
            File.Copy(sourcesPath, Path.Combine(dest, Path.GetFileName(sourcesPath)), true);
        else if (Directory.Exists(sourcesPath))
            DirectoryCopy.Copy(sourcesPath, dest, ct);
    }

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
