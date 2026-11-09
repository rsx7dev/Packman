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
    private string _installArguments = "";
    private string _uninstallArguments = "";
    private InstallerDetection? _detection;
    private string _installerError = "";
    private bool _isReadingInstaller;
    private string _msiSearch = "";
    private int _readSequence;

    // Fields the last installer filled in. The next installer replaces these; what the
    // user typed (anything through the public setters) is kept.
    private readonly HashSet<string> _autoFilled = new();

    public string SourcesPath
    {
        get => _sourcesPath;
        set
        {
            if (!Set(ref _sourcesPath, value)) return;
            // A different source must not inherit the previous file's insight or switches.
            _readSequence++;
            _detection = null;
            InstallerError = "";
            IsReadingInstaller = false;
            MsiSearch = "";
            InstallArguments = "";
            UninstallArguments = "";
            CurrentMsiInfo = null;
            RaiseAll(nameof(InstallerType), nameof(PackageType), nameof(IsExeInstaller), nameof(HasSourceFile));
            RaiseInstallerState();
        }
    }

    public string AppName
    {
        get => _appName;
        set { if (Set(ref _appName, value, [nameof(PreviewTitle)])) _autoFilled.Remove(nameof(AppName)); }
    }

    public string Manufacturer
    {
        get => _manufacturer;
        set { if (Set(ref _manufacturer, value)) _autoFilled.Remove(nameof(Manufacturer)); }
    }

    public string Version
    {
        get => _version;
        set { if (Set(ref _version, value, [nameof(PreviewMeta)])) _autoFilled.Remove(nameof(Version)); }
    }

    /// <summary>Loader write: fills a blank field from the installer and remembers it was not typed.</summary>
    private void Fill(ref string field, string? value, string name, params string[] dependents)
    {
        if (!string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value)) return;
        Set(ref field, value, dependents, name);
        _autoFilled.Add(name);
    }

    /// <summary>Blanks what the previous installer filled in, so the new one can fill it again.</summary>
    private void ClearAutoFilled()
    {
        if (_autoFilled.Contains(nameof(AppName))) Set(ref _appName, "", [nameof(PreviewTitle)], nameof(AppName));
        if (_autoFilled.Contains(nameof(Manufacturer))) Set(ref _manufacturer, "", nameof(Manufacturer));
        if (_autoFilled.Contains(nameof(Version))) Set(ref _version, "", [nameof(PreviewMeta)], nameof(Version));
        _autoFilled.Clear();
    }

    public bool UserInstall
    {
        get => _userInstall;
        set { if (Set(ref _userInstall, value)) RaiseAll(nameof(InstallContextHelp), nameof(PreviewMeta), nameof(MsiWarnings), nameof(HasMsiWarnings)); }
    }

    /// <summary>One line under the SYSTEM / USER toggle.</summary>
    public string InstallContextHelp => _userInstall
        ? "Installs only for the current user, without requiring elevation."
        : "Installs for all users with elevated privileges (most common for managed apps).";

    /// <summary>Written to the script's AppArch field.</summary>
    public string Architecture
    {
        get => _architecture;
        set => Set(ref _architecture, value, [nameof(PreviewMeta), nameof(MsiWarnings), nameof(HasMsiWarnings)]);
    }

    public MsiInfoService.MsiInfo? CurrentMsiInfo
    {
        get => _currentMsiInfo;
        set { if (Set(ref _currentMsiInfo, value, [nameof(ProductCode), nameof(HasProductCode)])) RaiseInstallerState(); }
    }

    public string CurrentPackagePath
    {
        get => _currentPackagePath;
        set { if (Set(ref _currentPackagePath, value)) RaiseAddCanExecute(); }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        set
        {
            if (!Set(ref _isGenerating, value)) return;
            CancelCommand.RaiseCanExecuteChanged();
            RaiseAddCanExecute();
        }
    }

    private CancellationTokenSource? _cts;

    /// <summary>The last generate was stopped by the user, so it is not an error to pop up.</summary>
    public bool WasCancelled { get; private set; }

    /// <summary>Stops the copy; the generator removes the half-built folder.</summary>
    public RelayCommand CancelCommand { get; }

    public CreatePackageViewModel()
    {
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsGenerating);
        AddInstallSwitchCommand = new RelayCommand<string>(text => AddSwitch(text, uninstall: false), _ => CanAddArguments);
        AddUninstallSwitchCommand = new RelayCommand<string>(text => AddSwitch(text, uninstall: true), _ => CanAddArguments);
        AddMsiPropertyCommand = new RelayCommand<string>(
            argument => InstallArguments = ArgumentLine.Append(InstallArguments, argument), _ => CanAddArguments);
        AddMsiFeatureCommand = new RelayCommand<string>(
            feature => InstallArguments = AddFeature(InstallArguments, feature), _ => CanAddArguments);
    }

    public string ExtractedIconPath
    {
        get => _extractedIconPath;
        set => Set(ref _extractedIconPath, value);
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

    // ── Installer insight (the Installer block of the rail) ─────────────────
    /// <summary>Silent switches written into the install command; empty keeps the script's placeholder.</summary>
    public string InstallArguments
    {
        get => _installArguments;
        set => Set(ref _installArguments, value);
    }

    public string UninstallArguments
    {
        get => _uninstallArguments;
        set => Set(ref _uninstallArguments, value);
    }

    public bool IsReadingInstaller
    {
        get => _isReadingInstaller;
        private set { if (Set(ref _isReadingInstaller, value)) RaiseInstallerState(); }
    }

    /// <summary>Why the installer could not be read; empty when it could.</summary>
    public string InstallerError
    {
        get => _installerError;
        private set { if (Set(ref _installerError, value)) RaiseInstallerState(); }
    }

    public bool HasInstallerError => _installerError.Length > 0;

    /// <summary>What the rail's Installer block shows for an EXE: the detection, or Unknown when the read failed.</summary>
    private InstallerKind? ExeKind => !IsExeInstaller || _isReadingInstaller ? null
        : _detection?.Kind ?? (HasInstallerError ? InstallerCatalog.Unknown : null);

    public bool HasExeInsight => ExeKind != null;
    public bool HasMsiInsight => !_isReadingInstaller && _currentMsiInfo != null;

    /// <summary>Anything to show in the rail's Installer block besides the reading line.</summary>
    public bool HasInstallerInsight => HasExeInsight || HasMsiInsight || (HasInstallerError && !IsExeInstaller);

    public string InstallerKindName => ExeKind?.Name ?? "";
    public bool IsUnknownInstaller => ExeKind == InstallerCatalog.Unknown;
    public bool HasDetectedInstaller => HasExeInsight && !IsUnknownInstaller;

    /// <summary>High, Medium or Low; empty for Unknown.</summary>
    public string InstallerConfidence => _detection?.Confidence?.ToString() ?? "";
    public string InstallerEvidence => _detection?.Evidence ?? "";

    public IReadOnlyList<InstallerSwitch> InstallSwitches => Switches(uninstall: false);
    public IReadOnlyList<InstallerSwitch> UninstallSwitches => Switches(uninstall: true);

    private IReadOnlyList<InstallerSwitch> Switches(bool uninstall) =>
        ExeKind?.Switches.Where(s => s.Uninstall == uninstall)
            .Select(s => InstallerCatalog.WithLogFolder(s, _psadtLogFolder)).ToList() ?? [];

    private string _psadtLogFolder = PsadtConfig.DefaultLogFolder;

    /// <summary>The template's Toolkit.LogPath, so suggested /log switches write beside PSADT's own logs.</summary>
    public string PsadtLogFolder
    {
        get => _psadtLogFolder;
        set => Set(ref _psadtLogFolder, value, [nameof(InstallSwitches), nameof(UninstallSwitches)]);
    }

    /// <summary>What the EXE's own headers say: the setup program's architecture and whether it is signed.</summary>
    public IReadOnlyList<InstallerFact> ExeFacts
    {
        get
        {
            if (ExeKind == null || _detection == null) return [];
            var facts = new List<InstallerFact>();
            if (_detection.Architecture.Length > 0) facts.Add(new("Setup program", _detection.Architecture));
            facts.Add(new("Signature", _detection.HasSignature ? "Signed (not verified)" : "Not signed"));
            return facts;
        }
    }

    public string InstallerLocation => ExeKind?.UninstallerLocation ?? "";
    public bool HasInstallerLocation => InstallerLocation.Length > 0;
    public string InstallerNote => ExeKind?.Note ?? "";
    public bool HasInstallerNote => InstallerNote.Length > 0;
    /// <summary>No documented switches: a type without a silent mode, or a type Packman doesn't recognise.</summary>
    public bool HasNoSwitches => HasExeInsight && ExeKind!.Switches.Length == 0;
    public bool HasNoKnownSwitches => HasDetectedInstaller && ExeKind!.Switches.Length == 0;

    // The identity block above the rail already shows these.
    private static readonly HashSet<string> HeaderProperties =
        ["ProductName", "ProductVersion", "Manufacturer", "ProductCode", "UpgradeCode"];

    /// <summary>Key properties not already shown in the package header.</summary>
    public IReadOnlyList<MsiInfoService.MsiProperty> MsiKeyProperties =>
        _currentMsiInfo?.KeyProperties.Where(p => !HeaderProperties.Contains(p.Name)).ToList() ?? [];
    public bool HasMsiKeyProperties => MsiKeyProperties.Count > 0;

    /// <summary>Where the MSI disagrees with the chosen architecture or install context.</summary>
    public IReadOnlyList<string> MsiWarnings => _currentMsiInfo?.Warnings(_userInstall, _architecture) ?? [];
    public bool HasMsiWarnings => HasMsiInsight && MsiWarnings.Count > 0;

    public string UpgradeCode => _currentMsiInfo?.IsValid == true ? _currentMsiInfo.UpgradeCode : "";
    public bool HasUpgradeCode => UpgradeCode.Length > 0;

    /// <summary>"PSADT v4 · Win32 · MSI"; the installer kind is left off until one is picked.</summary>
    public string PackageType => string.IsNullOrWhiteSpace(_sourcesPath) ? "PSADT v4 · Win32" : $"PSADT v4 · Win32 · {InstallerType}";
    public IReadOnlyList<MsiInfoService.MsiFeature> MsiFeatures => _currentMsiInfo?.Features ?? [];
    public bool HasMsiFeatures => MsiFeatures.Count > 0;
    public IReadOnlyList<MsiInfoService.MsiOption> MsiOptions => _currentMsiInfo?.Options ?? [];
    public bool HasMsiOptions => MsiOptions.Count > 0;
    public IReadOnlyList<string> MsiTransforms => _currentMsiInfo?.Transforms ?? [];
    public bool HasMsiTransforms => MsiTransforms.Count > 0;
    public string MsiTransformNames => string.Join(Environment.NewLine, MsiTransforms);

    public string MsiSearch
    {
        get => _msiSearch;
        set { if (Set(ref _msiSearch, value)) RaiseInstallerState(); }
    }

    /// <summary>The full Property table, narrowed by the search text.</summary>
    public IReadOnlyList<MsiInfoService.MsiProperty> MsiProperties => _currentMsiInfo?.Search(_msiSearch) ?? [];

    public bool HasMsiMatches => MsiProperties.Count > 0;
    public bool HasNoMsiMatches => HasMsiInsight && !HasMsiMatches;
    public string MsiCountText
    {
        get
        {
            var total = _currentMsiInfo?.Properties.Count ?? 0;
            return MsiProperties.Count == total ? $"{total} properties" : $"{MsiProperties.Count} of {total} properties";
        }
    }
    public string MsiNoMatchText => $"No properties match \"{_msiSearch.Trim()}\".";

    public RelayCommand<string> AddInstallSwitchCommand { get; }
    public RelayCommand<string> AddUninstallSwitchCommand { get; }
    public RelayCommand<string> AddMsiPropertyCommand { get; }
    /// <summary>Adds the feature to ADDLOCAL, creating ADDLOCAL when the line has none.</summary>
    public RelayCommand<string> AddMsiFeatureCommand { get; }

    /// <summary><paramref name="line"/> with <paramref name="feature"/> in its ADDLOCAL list.</summary>
    public static string AddFeature(string line, string feature)
    {
        var pieces = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var at = pieces.FindIndex(p => p.StartsWith("ADDLOCAL=", StringComparison.OrdinalIgnoreCase));
        if (at < 0) return ArgumentLine.Append(line, $"ADDLOCAL={feature}");
        var names = pieces[at]["ADDLOCAL=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (names.Contains(feature, StringComparer.Ordinal)) return line.Trim();
        names.Add(feature);
        pieces[at] = "ADDLOCAL=" + string.Join(',', names);
        return string.Join(' ', pieces);
    }

    private bool CanAddArguments => _currentPackagePath == "" && !_isGenerating;

    private void RaiseAddCanExecute()
    {
        AddInstallSwitchCommand.RaiseCanExecuteChanged();
        AddUninstallSwitchCommand.RaiseCanExecuteChanged();
        AddMsiPropertyCommand.RaiseCanExecuteChanged();
        AddMsiFeatureCommand.RaiseCanExecuteChanged();
    }

    private void AddSwitch(string text, bool uninstall)
    {
        var last = ExeKind?.Switches.FirstOrDefault(s => s.MustBeLast && s.Uninstall == uninstall)?.Text;
        if (uninstall) UninstallArguments = ArgumentLine.Append(UninstallArguments, text, last);
        else InstallArguments = ArgumentLine.Append(InstallArguments, text, last);
    }

    private void RaiseInstallerState() => RaiseAll(
        nameof(HasInstallerError), nameof(HasExeInsight), nameof(HasMsiInsight), nameof(HasInstallerInsight),
        nameof(InstallerKindName), nameof(IsUnknownInstaller), nameof(HasDetectedInstaller),
        nameof(InstallerConfidence), nameof(InstallerEvidence), nameof(InstallSwitches), nameof(UninstallSwitches),
        nameof(InstallerLocation), nameof(HasInstallerLocation), nameof(InstallerNote), nameof(HasInstallerNote),
        nameof(HasNoSwitches), nameof(HasNoKnownSwitches), nameof(ExeFacts), nameof(MsiWarnings), nameof(HasMsiWarnings), nameof(MsiFeatures),
        nameof(HasMsiFeatures), nameof(MsiOptions), nameof(HasMsiOptions), nameof(MsiKeyProperties), nameof(HasMsiKeyProperties),
        nameof(UpgradeCode), nameof(HasUpgradeCode),nameof(MsiTransforms), nameof(HasMsiTransforms),
        nameof(MsiTransformNames), nameof(MsiProperties), nameof(HasMsiMatches), nameof(HasNoMsiMatches),
        nameof(MsiCountText), nameof(MsiNoMatchText));

    public void LoadFromFile(string filePath)
    {
        SourcesPath = filePath;
        ClearAutoFilled();
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".exe")
        {
            var (name, company, ver) = MetadataExtractor.ExtractExeMetadata(filePath);
            Fill(ref _appName, name, nameof(AppName), nameof(PreviewTitle));
            Fill(ref _manufacturer, company, nameof(Manufacturer));
            Fill(ref _version, ver, nameof(Version), nameof(PreviewMeta));
        }

        if (ext is ".msi" or ".exe")
            InstallerRead = ReadInstallerAsync(filePath, msi: ext == ".msi");
        else
            InstallerRead = Task.CompletedTask;

        if (ext != ".msi")
            Fill(ref _appName, MetadataExtractor.ExtractNameFromFilename(filePath), nameof(AppName), nameof(PreviewTitle));

        ExtractedIconPath = IconExtractor.ExtractIconToTemp(filePath) ?? "";
    }

    /// <summary>Completes once the last picked file's installer insight is applied; completed when idle.</summary>
    public Task InstallerRead { get; private set; } = Task.CompletedTask;

    private async Task ReadInstallerAsync(string path, bool msi)
    {
        var sequence = _readSequence;
        IsReadingInstaller = true;
        try
        {
            if (msi)
            {
                var info = await MsiInfoService.ExtractMsiInfoAsync(path);
                if (sequence != _readSequence) return;
                if (info.IsValid)
                {
                    CurrentMsiInfo = info;
                    Fill(ref _appName, info.ProductName, nameof(AppName), nameof(PreviewTitle));
                    Fill(ref _manufacturer, info.Manufacturer, nameof(Manufacturer));
                    Fill(ref _version, info.ProductVersion, nameof(Version), nameof(PreviewMeta));
                }
                else
                {
                    InstallerError = "Couldn't read this MSI's Property table. It may be damaged or locked.";
                }
                Fill(ref _appName, MetadataExtractor.ExtractNameFromFilename(path), nameof(AppName), nameof(PreviewTitle));
            }
            else
            {
                var detection = await Task.Run(() => InstallerDetector.Detect(path));
                if (sequence != _readSequence) return;
                _detection = detection;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (sequence != _readSequence) return;
            InstallerError = $"Couldn't read this file as a Windows installer: {ex.Message}";
        }
        finally
        {
            if (sequence == _readSequence) IsReadingInstaller = false;
        }
    }

    public ApplicationInfo BuildApplicationInfo()
    {
        var installContext = UserInstall ? "User" : "System";
        var info = new ApplicationInfo
        {
            Name = AppName.Trim(),
            Manufacturer = string.IsNullOrWhiteSpace(Manufacturer) ? "Unknown" : Manufacturer.Trim(),
            Version = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim(),
            // Explorer's "Copy as path" wraps the path in quotes.
            SourcesPath = SourcesPath.Trim().Trim('"').Trim(),
            InstallContext = installContext,
            Architecture = string.IsNullOrWhiteSpace(Architecture) ? "x64" : Architecture.Trim(),
            InstallArguments = InstallArguments.Trim(),
            UninstallArguments = UninstallArguments.Trim()
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
        var templatePath = settings.NetworkPaths.PSADTTemplateInUse;
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;
        if (string.IsNullOrWhiteSpace(AppName))
            return null;

        var check = new PSADTGenerator(outputPath, templatePath, settings.PackageLayout).ValidatePackageCreation(BuildApplicationInfo());
        return check.PackageExists ? check.ExistingPath : null;
    }

    public async Task<string?> GenerateAsync(AppSettings settings, bool overwriteExisting = false)
    {
        WasCancelled = false;
        if (!Validate()) return null;

        IsGenerating = true;
        StatusText = "Copying the PSADT template and source files…";
        ActivityLog.Shared.Section($"Creating package {AppName} {Version}");
        _cts = new CancellationTokenSource();

        try
        {
            var outputPath = settings.NetworkPaths.IntuneApplications;
            var templatePath = settings.NetworkPaths.PSADTTemplateInUse;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                StatusText = "Set the Intune Applications Path in Settings first.";
                ActivityLog.Shared.Error(StatusText);
                return null;
            }

            var appInfo = BuildApplicationInfo();

            var generator = new PSADTGenerator(outputPath, templatePath, settings.PackageLayout);
            var result = await generator.CreatePackageAsync(appInfo, overwriteExisting, _cts.Token);
            var packagePath = result.PackagePath;

            if (!string.IsNullOrEmpty(ExtractedIconPath))
                IconExtractor.CopyIconToPackage(ExtractedIconPath, packagePath, appInfo.Name);

            CurrentPackagePath = packagePath;
            StatusText = result.Warnings.Count == 0
                ? $"Package created · {DateTime.Now:HH:mm:ss}"
                : $"Package created · {DateTime.Now:HH:mm:ss} · check the script: {string.Join(" ", result.Warnings)}";
            ActivityLog.Shared.Success($"Package created: {packagePath}");
            Debug.WriteLine($"Package created: {packagePath}");
            return packagePath;
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            StatusText = "Generation cancelled. No half-built package was left on the share.";
            ActivityLog.Shared.Warning(StatusText);
            return null;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            ActivityLog.Shared.Error(StatusText);
            Debug.WriteLine($"Package generation failed: {ex}");
            return null;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
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
        InstallArguments = "";
        UninstallArguments = "";
        _autoFilled.Clear();
    }
}
