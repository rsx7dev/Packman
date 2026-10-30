using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Packman.Helpers;

/// <summary>
/// packman.id in the package folder root: links the folder to the Intune app Packman uploaded
/// from it. The .intunewin build never packs it.
/// </summary>
public static class PackageMarker
{
    public const string FileName = "packman.id";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed class Link
    {
        public string AppId { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string LinkedAt { get; set; } = "";
    }

    /// <summary>Writes or overwrites the link. Throws when the folder is missing or not writable.</summary>
    public static void Save(string folderPath, string appId, string tenantId)
    {
        var link = new Link { AppId = appId, TenantId = tenantId, LinkedAt = DateTime.UtcNow.ToString("o") };
        File.WriteAllText(Path.Combine(folderPath, FileName), JsonSerializer.Serialize(link, JsonOptions));
    }

    /// <summary>The folder's link, or null when it has none or it cannot be read.</summary>
    public static Link? Read(string folderPath)
    {
        try
        {
            var path = Path.Combine(folderPath, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<Link>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading {FileName}: {ex.Message}");
            return null;
        }
    }
}
