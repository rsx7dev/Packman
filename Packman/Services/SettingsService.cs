using Packman.Helpers;
using Packman.Models;
using System.IO;
using System.Text.Json;

namespace Packman.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // %LocalAppData%: the install directory is read-only for a machine-wide install.
    // The old path next to the exe is still read once so settings carry over.
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packman", "appsettings.json");

    private static readonly string LegacyPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private AppSettings? _settings;

    public AppSettings Settings => _settings ??= Load();

    /// <summary>Set when the last load hit an unreadable file. Shown on the Settings page.</summary>
    public string? LoadError { get; private set; }

    private AppSettings Load()
    {
        var source = File.Exists(_path) ? _path : File.Exists(LegacyPath) ? LegacyPath : null;
        if (source == null) return new AppSettings();

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), ReadOptions)
                           ?? new AppSettings();
            Migrate(settings);
            return settings;
        }
        catch (Exception ex)
        {
            // Keep the bad file rather than silently replacing a whole configuration.
            LoadError = QuarantineCorruptFile(source, ex);
            return new AppSettings();
        }
    }

    private static string QuarantineCorruptFile(string source, Exception cause)
    {
        try
        {
            var backup = $"{source}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(source, backup);
            return $"Settings could not be read ({cause.Message}). The previous file was kept as {backup} and defaults were loaded.";
        }
        catch
        {
            return $"Settings could not be read ({cause.Message}). Defaults were loaded.";
        }
    }

    /// <summary>Moves a per-package group with Uninstall intent onto the uninstall group.</summary>
    private static void Migrate(AppSettings settings)
    {
        var groups = settings.GroupAssignment;
        if (!groups.CreateGroupPerPackage || groups.NewGroupIntent != AssignmentIntent.Uninstall) return;

        groups.CreateUninstallGroupPerPackage = true;
        groups.UninstallGroupNameTemplate = groups.GroupNameTemplate;
        groups.CreateGroupPerPackage = false;
        groups.NewGroupIntent = AssignmentIntent.Required;
    }

    /// <summary>Where the settings live; shown on the Settings page.</summary>
    public string SettingsPath => _path;

    /// <summary>
    /// Writes the settings file through a temp file, so a crash mid-write cannot leave a
    /// truncated file that the next start quarantines. Throws when the file cannot be written.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        TextFileIO.Write(_path, JsonSerializer.Serialize(Settings, WriteOptions), new System.Text.UTF8Encoding(false));
        Saved?.Invoke();
    }

    /// <summary>Raised after a successful save, so pages summarising settings can refresh.</summary>
    public event Action? Saved;
}
