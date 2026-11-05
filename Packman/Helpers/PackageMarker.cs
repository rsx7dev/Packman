using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>
        /// Read from a legacy .intune-appid.json, which has no tenant: only trust it once the app
        /// is known to exist in the connected tenant.
        /// </summary>
        [JsonIgnore] public bool IsLegacy { get; init; }
    }

    private sealed class LegacyMarker
    {
        public string IntuneAppId { get; set; } = "";
    }

    /// <summary>Path of the link file for a package folder.</summary>
    public static string PathFor(string packagePath) => Path.Combine(PsadtLayout.IntuneFolder(packagePath), FileName);

    /// <summary>
    /// Writes or overwrites the link. Returns null, or the warning to show when it could not be
    /// written; a failed write never fails the upload.
    /// </summary>
    public static string? Save(string packagePath, Link link)
    {
        try
        {
            Directory.CreateDirectory(PsadtLayout.IntuneFolder(packagePath));
            File.WriteAllText(PathFor(packagePath), JsonSerializer.Serialize(link, JsonOptions));
            return null;
        }
        catch (Exception ex)
        {
            return $"The upload succeeded, but {PathFor(packagePath)} could not be written: {ex.Message} " +
                "Until it is, Packman will not offer Republish content or Edit script for this app.";
        }
    }

    /// <summary>
    /// The package's link, or null when it has none or it cannot be read. A packman.id needs both
    /// ids. Only without one is an older .intune-appid.json used, as an <see cref="Link.IsLegacy"/> link.
    /// </summary>
    public static Link? Read(string packagePath)
    {
        try
        {
            var path = PathFor(packagePath);
            if (File.Exists(path))
            {
                var link = JsonSerializer.Deserialize<Link>(File.ReadAllText(path), JsonOptions);
                return link is { AppId.Length: > 0, TenantId.Length: > 0 } ? link : null;
            }

            var legacy = Path.Combine(packagePath, LegacyFileName);
            var appId = File.Exists(legacy) ? JsonSerializer.Deserialize<LegacyMarker>(File.ReadAllText(legacy), JsonOptions)?.IntuneAppId : null;
            return string.IsNullOrEmpty(appId) ? null : new Link { AppId = appId, IsLegacy = true };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading {FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The app an upgrade of this package supersedes, or "". A legacy link counts only when
    /// <paramref name="appExists"/> finds the app in the connected tenant.
    /// </summary>
    public static async Task<string> PredecessorAppIdAsync(string packagePath, Func<string, Task<bool>> appExists)
    {
        var link = Read(packagePath);
        if (link is { IsLegacy: true } && !await appExists(link.AppId))
            return "";
        return link?.AppId ?? "";
    }
}
