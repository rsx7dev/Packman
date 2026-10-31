namespace Packman.Models;

public class ApplicationInfo
{
    public string Manufacturer { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallContext { get; set; } = "System";
    public string SourcesPath { get; set; } = "";
    public string Architecture { get; set; } = "x64";
    public string Author { get; set; } = "";
    public string MsiProductCode { get; set; } = "";
    public string MsiProductVersion { get; set; } = "";
    public string MsiUpgradeCode { get; set; } = "";

    /// <summary>Silent switches (EXE) or MSI properties (MSI) written into the install command; empty keeps the placeholder.</summary>
    public string InstallArguments { get; set; } = "";
    public string UninstallArguments { get; set; } = "";

    private string _displayName = "";

    /// <summary>Title shown in Intune. Defaults to "Vendor Name Version" until overridden.</summary>
    public string DisplayName
    {
        get => string.IsNullOrWhiteSpace(_displayName) ? $"{Manufacturer} {Name} {Version}".Trim() : _displayName.Trim();
        set => _displayName = value ?? "";
    }

    public bool IsMsiPackage => !string.IsNullOrEmpty(MsiProductCode);

    public string PackageType
    {
        get
        {
            if (IsMsiPackage) return "MSI";
            if (!string.IsNullOrEmpty(SourcesPath))
            {
                var ext = System.IO.Path.GetExtension(SourcesPath).ToLower();
                if (ext == ".exe") return "EXE";
            }
            return "Unknown";
        }
    }
}
