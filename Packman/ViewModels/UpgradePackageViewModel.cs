using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.IO;

namespace Packman.ViewModels;

/// <summary>
/// The "Upgrade Existing" flow: pick a built package, a new source and version, and
/// produce the next version of it.
/// </summary>
public class UpgradePackageViewModel : ObservableObject
{
    private string _existingPackagePath = "";
    private string _newVersion = "";
    private string _newSourcePath = "";
    private string _packageDisplayName = "";
    private string _packageDisplayVersion = "";
    private string _packageDisplayContext = "";
    private string _statusText = "";
    private bool _isBusy;
    private bool _hasLoadedPackage;

    public PackageMetadata? LoadedMetadata { get; private set; }

    public string ExistingPackagePath
    {
        get => _existingPackagePath;
        set => Set(ref _existingPackagePath, value);
    }

    public string NewVersion
    {
        get => _newVersion;
        set => Set(ref _newVersion, value);
    }

    public string NewSourcePath
    {
        get => _newSourcePath;
        set => Set(ref _newSourcePath, value);
    }

    public string PackageDisplayName
    {
        get => _packageDisplayName;
        set => Set(ref _packageDisplayName, value);
    }

    public string PackageDisplayVersion
    {
        get => _packageDisplayVersion;
        set => Set(ref _packageDisplayVersion, value);
    }

    public string PackageDisplayContext
    {
        get => _packageDisplayContext;
        set => Set(ref _packageDisplayContext, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public bool HasLoadedPackage
    {
        get => _hasLoadedPackage;
        private set => Set(ref _hasLoadedPackage, value);
    }

    /// <summary>Loads metadata from an existing package folder.</summary>
    public void LoadPackage(string packagePath)
    {
        ExistingPackagePath = packagePath;
        HasLoadedPackage = false;

        var applicationFolder = PsadtLayout.ApplicationFolder(packagePath);
        var scriptPath = FolderBrowserHelper.GetPSADTScriptPath(applicationFolder)
            ?? FolderBrowserHelper.GetPSADTScriptPath(packagePath);

        if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
        {
            StatusText = "PSADT script not found — not a valid package folder.";
            return;
        }

        var metadata = MetadataExtractor.ExtractMetadataFromScript(scriptPath);
        var manufacturer = metadata.GetValueOrDefault("Vendor", "");
        var appName = metadata.GetValueOrDefault("AppName", "");
        var version = metadata.GetValueOrDefault("Version", "");
        var installContext = InstallContextParser.ExtractFromPackage(packagePath);

        LoadedMetadata = new PackageMetadata
        {
            Manufacturer = manufacturer,
            AppName = appName,
            Version = version,
            InstallContext = installContext
        };

        PackageDisplayName = $"{manufacturer} {appName}".Trim();
        PackageDisplayVersion = string.IsNullOrEmpty(version) ? "Version unknown" : $"Version {version}";
        PackageDisplayContext = $"{installContext} Install";
        StatusText = "";
        HasLoadedPackage = true;
    }

    /// <summary>Sets the new source file and reads the version off it.</summary>
    public void SetNewSource(string sourcePath)
    {
        NewSourcePath = sourcePath;
        var ext = Path.GetExtension(sourcePath).ToLower();

        if (ext == ".msi")
        {
            var info = MsiInfoService.ExtractMsiInfo(sourcePath);
            if (info.IsValid && !string.IsNullOrEmpty(info.ProductVersion))
                NewVersion = info.ProductVersion;
        }
        else if (ext == ".exe")
        {
            var (_, _, version) = MetadataExtractor.ExtractExeMetadata(sourcePath);
            var filenameVersion = MetadataExtractor.ExtractVersionFromFilename(Path.GetFileNameWithoutExtension(sourcePath));
            if (!string.IsNullOrEmpty(filenameVersion) && (string.IsNullOrEmpty(version) || filenameVersion.Length > version.Length))
                NewVersion = filenameVersion;
            else if (!string.IsNullOrEmpty(version))
                NewVersion = version;
        }
    }

    /// <summary>Clears the form for the next package.</summary>
    public void Reset()
    {
        ExistingPackagePath = "";
        NewVersion = "";
        NewSourcePath = "";
        PackageDisplayName = "";
        PackageDisplayVersion = "";
        PackageDisplayContext = "";
        StatusText = "";
        LoadedMetadata = null;
        HasLoadedPackage = false;
    }

    public bool CanUpgrade =>
        HasLoadedPackage &&
        Directory.Exists(ExistingPackagePath) &&
        !string.IsNullOrWhiteSpace(NewVersion) &&
        !string.IsNullOrWhiteSpace(NewSourcePath) &&
        File.Exists(NewSourcePath);

    /// <summary>Creates the new version. Returns its path, or null on failure.</summary>
    public async Task<string?> UpgradeAsync(AppSettings settings)
    {
        if (!CanUpgrade)
        {
            StatusText = "Select a package, a new source file, and a new version first.";
            return null;
        }

        var outputPath = settings.NetworkPaths.IntuneApplications;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            StatusText = "Configure the IntuneApplications path in Settings first.";
            return null;
        }

        IsBusy = true;
        StatusText = $"Building version {NewVersion}…";

        try
        {
            var service = new PackageUpgradeService(outputPath, settings.PackageLayout);
            var result = await service.UpgradePackageAsync(ExistingPackagePath, NewVersion.Trim(), NewSourcePath.Trim());

            StatusText = result.Warnings.Count == 0
                ? $"New version {NewVersion} created."
                : $"New version {NewVersion} created · check Files: {string.Join(" ", result.Warnings)}";
            return result.PackagePath;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
