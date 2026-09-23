using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.Diagnostics;
using System.IO;

namespace Packman.ViewModels;

public class CreatePackageViewModel : ObservableObject
{
    private string _sourcesPath = "";
    private string _appName = "";
    private string _manufacturer = "";
    private string _version = "";
    private bool _userInstall = false;
    private string _architecture = "x64";
    private MsiInfoService.MsiInfo? _currentMsiInfo;
    private string _currentPackagePath = "";
    private string _statusText = "";
    private bool _isGenerating = false;
    private string _extractedIconPath = "";
    private string _predecessorAppId = "";

    public string SourcesPath
    {
        get => _sourcesPath;
        set
        {
            if (!Set(ref _sourcesPath, value)) return;
            CurrentMsiInfo = null;
            RaiseAll(nameof(InstallerType), nameof(IsExeInstaller), nameof(HasSourceFile));
        }
    }

    public string AppName
    {
        get => _appName;
        set => Set(ref _appName, value, [nameof(PreviewTitle)]);
    }

    public string Manufacturer
    {
        get => _manufacturer;
        set => Set(ref _manufacturer, value);
    }

    public string Version
    {
        get => _version;
        set => Set(ref _version, value, [nameof(PreviewMeta)]);
    }

    public bool UserInstall
    {
        get => _userInstall;
        set { if (Set(ref _userInstall, value)) RaiseAll(nameof(InstallContextHelp), nameof(PreviewMeta)); }
    }

    /// <summary>One line under the SYSTEM / USER toggle.</summary>
    public string InstallContextHelp => _userInstall
        ? "Installs only for the current user, without requiring elevation."
        : "Installs for all users with elevated privileges (most common for managed apps).";

    /// <summary>Written to the script's AppArch field.</summary>
    public string Architecture
    {
        get => _architecture;
        set => Set(ref _architecture, value, [nameof(PreviewMeta)]);
    }

    public MsiInfoService.MsiInfo? CurrentMsiInfo
    {
        get => _currentMsiInfo;
        set => Set(ref _currentMsiInfo, value, [nameof(ProductCode), nameof(HasProductCode)]);
    }

    public string CurrentPackagePath
    {
        get => _currentPackagePath;
        set => Set(ref _currentPackagePath, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        set => Set(ref _isGenerating, value);
    }

    public string ExtractedIconPath
    {
        get => _extractedIconPath;
        set => Set(ref _extractedIconPath, value);
    }

    /// <summary>
    /// App id this package supersedes, set when it came from Upgrade. The upload writes
    /// the supersedence relationship from it.
    /// </summary>
    public string PredecessorAppId
    {
        get => _predecessorAppId;
        set => Set(ref _predecessorAppId, value);
    }

    // ── Package preview (the rail beside the Package step) ──────────────
    /// <summary>Installer kind read from the file extension.</summary>
    public string InstallerType => Path.GetExtension(_sourcesPath.Trim()).ToLowerInvariant() switch
    {
        ".msi" => "MSI",
        ".exe" => "EXE",
        _ when string.IsNullOrWhiteSpace(_sourcesPath) => "Not selected",
        var ext => ext.TrimStart('.').ToUpperInvariant(),
    };

    /// <summary>EXE installers need their silent switches written into the script by hand.</summary>
    public bool IsExeInstaller => InstallerType == "EXE";

    public bool HasSourceFile
    {
        get
        {
            try { return !string.IsNullOrWhiteSpace(_sourcesPath) && File.Exists(_sourcesPath.Trim()); }
            catch { return false; }
        }
    }

    public string ProductCode => _currentMsiInfo?.IsValid == true ? _currentMsiInfo.ProductCode : "";
    public bool HasProductCode => ProductCode.Length > 0;

    public string PreviewTitle => string.IsNullOrWhiteSpace(_appName) ? "New package" : _appName.Trim();
    public string PreviewMeta =>
        $"{(string.IsNullOrWhiteSpace(_version) ? "no version" : _version.Trim())} · {_architecture} · {(_userInstall ? "User" : "System")}";

    public void LoadFromFile(string filePath)
    {
        SourcesPath = filePath;
        // A different source must not inherit the previous MSI's detection metadata.
        CurrentMsiInfo = null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".msi")
        {
            var info = MsiInfoService.ExtractMsiInfo(filePath);
            if (info.IsValid)
            {
                CurrentMsiInfo = info;
                if (string.IsNullOrWhiteSpace(AppName)) AppName = info.ProductName;
                if (string.IsNullOrWhiteSpace(Manufacturer)) Manufacturer = info.Manufacturer;
                if (string.IsNullOrWhiteSpace(Version)) Version = info.ProductVersion;
            }
        }
        else if (ext == ".exe")
        {
            var (name, company, ver) = MetadataExtractor.ExtractExeMetadata(filePath);
            if (string.IsNullOrWhiteSpace(AppName) && !string.IsNullOrWhiteSpace(name)) AppName = name;
            if (string.IsNullOrWhiteSpace(Manufacturer) && !string.IsNullOrWhiteSpace(company)) Manufacturer = company;
            if (string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(ver)) Version = ver;
        }

        if (string.IsNullOrWhiteSpace(AppName))
            AppName = MetadataExtractor.ExtractNameFromFilename(filePath);

        ExtractedIconPath = IconExtractor.ExtractIconToTemp(filePath) ?? "";
    }

    public ApplicationInfo BuildApplicationInfo()
    {
        var installContext = UserInstall ? "User" : "System";
        var info = new ApplicationInfo
        {
            Name = AppName.Trim(),
            Manufacturer = string.IsNullOrWhiteSpace(Manufacturer) ? "Unknown" : Manufacturer.Trim(),
            Version = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim(),
            SourcesPath = SourcesPath.Trim(),
            InstallContext = installContext,
            Architecture = string.IsNullOrWhiteSpace(Architecture) ? "x64" : Architecture.Trim()
        };

        if (CurrentMsiInfo?.IsValid == true)
        {
            info.MsiProductCode = CurrentMsiInfo.ProductCode;
            info.MsiProductVersion = CurrentMsiInfo.ProductVersion;
            info.MsiUpgradeCode = CurrentMsiInfo.UpgradeCode;
        }

        return info;
    }

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(AppName))
        {
            StatusText = "Application Name is required.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Path of an existing package for this app and version, or null. Lets the caller
    /// offer to replace it rather than fail.
    /// </summary>
    public string? FindExistingPackage(AppSettings settings)
    {
        var outputPath = settings.NetworkPaths.IntuneApplications;
        var templatePath = settings.NetworkPaths.PSADTTemplate;
        if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(templatePath))
            return null;
        if (string.IsNullOrWhiteSpace(AppName))
            return null;

        var check = new PSADTGenerator(outputPath, templatePath).ValidatePackageCreation(BuildApplicationInfo());
        return check.PackageExists ? check.ExistingPath : null;
    }

    public async Task<string?> GenerateAsync(AppSettings settings, bool overwriteExisting = false)
    {
        if (!Validate()) return null;

        IsGenerating = true;
        StatusText = "Generating package…";

        try
        {
            var outputPath = settings.NetworkPaths.IntuneApplications;
            var templatePath = settings.NetworkPaths.PSADTTemplate;

            if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(templatePath))
            {
                StatusText = "Configure IntuneApplications and PSADTTemplate paths in Settings first.";
                return null;
            }

            var appInfo = BuildApplicationInfo();

            var generator = new PSADTGenerator(outputPath, templatePath);
            var result = await generator.CreatePackageAsync(appInfo, overwriteExisting);
            var packagePath = result.PackagePath;

            if (!string.IsNullOrEmpty(ExtractedIconPath))
                IconExtractor.CopyIconToPackage(ExtractedIconPath, packagePath, appInfo.Name);

            CurrentPackagePath = packagePath;
            StatusText = result.Warnings.Count == 0
                ? $"Package created · {DateTime.Now:HH:mm:ss}"
                : $"Package created · {DateTime.Now:HH:mm:ss} · check the script: {string.Join(" ", result.Warnings)}";
            Debug.WriteLine($"Package created: {packagePath}");
            return packagePath;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Debug.WriteLine($"Package generation failed: {ex}");
            return null;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    public void Reset()
    {
        SourcesPath = "";
        AppName = "";
        Manufacturer = "";
        Version = "";
        ExtractedIconPath = "";
        UserInstall = false;
        Architecture = "x64";
        CurrentMsiInfo = null;
        CurrentPackagePath = "";
        StatusText = "";
        PredecessorAppId = "";
    }
}
