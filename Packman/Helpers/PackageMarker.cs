using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Packman.Helpers;

/// <summary>
/// Intune\packman.id in a package folder: links the folder to the Intune app Packman uploaded
/// from it, next to the .intunewin it was built into.
/// </summary>
public static class PackageMarker
{
    public const string FileName = "packman.id";

    /// <summary>What builds before packman.id wrote in the package root. Read only, never written.</summary>
    private const string LegacyFileName = ".intune-appid.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed class Link
    {
        public string AppId { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string UploadedAt { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DisplayVersion { get; set; } = "";
        public string IntunewinFileName { get; set; } = "";
        public long UnencryptedSize { get; set; }
    }

    private sealed class LegacyMarker
    {
        public string IntuneAppId { get; set; } = "";
    }

    /// <summary>Path of the link file for a package folder.</summary>
    public static string PathFor(string packagePath) => Path.Combine(packagePath, "Intune", FileName);

    /// <summary>Writes or overwrites the link. Throws when the folder is not writable.</summary>
    public static void Save(string packagePath, Link link)
    {
        Directory.CreateDirectory(Path.Combine(packagePath, "Intune"));
        File.WriteAllText(PathFor(packagePath), JsonSerializer.Serialize(link, JsonOptions));
    }

    /// <summary>
    /// The package's link, or null when it has none or it cannot be read. Without packman.id an
    /// older .intune-appid.json is used; that link has an empty TenantId, so callers must check
    /// the app exists in the connected tenant before trusting it.
    /// </summary>
    public static Link? Read(string packagePath)
    {
        try
        {
            var path = PathFor(packagePath);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Link>(File.ReadAllText(path), JsonOptions);

            var legacy = Path.Combine(packagePath, LegacyFileName);
            var appId = File.Exists(legacy) ? JsonSerializer.Deserialize<LegacyMarker>(File.ReadAllText(legacy), JsonOptions)?.IntuneAppId : null;
            return string.IsNullOrEmpty(appId) ? null : new Link { AppId = appId };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading {FileName}: {ex.Message}");
            return null;
        }
    }
}
