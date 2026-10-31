#:property TargetFramework=net10.0
#:property PublishAot=false

using Packman.Services;

// Settings never keep the bundled PSADT folder as the template path: it is cleared on load and
// on save, a reopened Settings page reads it back empty, and other template paths are untouched.
// Uses the production SettingsService against a temp settings file.
//   dotnet run --file tests/SettingsTemplateTests.cs -c Release
// Exits non-zero when a check fails.
internal static class SettingsTemplateTests
{
    private static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "PSADT");
    private const string NetworkTemplate = @"\\server\share\PSADTTemplate";

    private static int _passed;
    private static readonly List<string> Failures = new();

    private static readonly (string Name, Action Check)[] Checks =
    [
        ("A saved bundled template path is empty after load", LoadClearsBundled),
        ("A path inside the bundled folder, or with a trailing separator, is cleared", LoadClearsInsideBundled),
        ("Saving a bundled template path writes it empty and a reopen reads it empty", SaveAndReopenStayEmpty),
        ("Another template path survives load, save and reopen", OtherPathKept),
        ("An empty template path stays empty", EmptyStaysEmpty),
    ];

    public static int Main()
    {
        foreach (var (name, check) in Checks)
        {
            try
            {
                check();
                _passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void LoadClearsBundled()
    {
        using var file = new SettingsFile(Bundled);
        Equal("", file.Open().Settings.NetworkPaths.PSADTTemplate);
    }

    private static void LoadClearsInsideBundled()
    {
        foreach (var path in new[] { Path.Combine(Bundled, "Application"), Bundled + Path.DirectorySeparatorChar, Path.Combine(Bundled, "Application", "Invoke-AppDeployToolkit.ps1") })
        {
            using var file = new SettingsFile(path);
            Equal("", file.Open().Settings.NetworkPaths.PSADTTemplate);
        }
    }

    private static void SaveAndReopenStayEmpty()
    {
        using var file = new SettingsFile(NetworkTemplate);
        var service = file.Open();
        service.Settings.NetworkPaths.PSADTTemplate = Bundled;
        service.Save();

        Equal("", file.Open().Settings.NetworkPaths.PSADTTemplate);
    }

    private static void OtherPathKept()
    {
        using var file = new SettingsFile(NetworkTemplate);
        var service = file.Open();
        Equal(NetworkTemplate, service.Settings.NetworkPaths.PSADTTemplate);
        service.Save();
        Equal(NetworkTemplate, file.Open().Settings.NetworkPaths.PSADTTemplate);
    }

    private static void EmptyStaysEmpty()
    {
        using var file = new SettingsFile("");
        var service = file.Open();
        service.Save();
        Equal("", file.Open().Settings.NetworkPaths.PSADTTemplate);
    }

    // A temp settings file holding the given template path.
    private sealed class SettingsFile : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("packman-settings-").FullName;

        public SettingsFile(string template)
        {
            File.WriteAllText(Path, System.Text.Json.JsonSerializer.Serialize(
                new { NetworkPaths = new { PSADTTemplate = template } }));
        }

        public string Path => System.IO.Path.Combine(_directory, "appsettings.json");

        public SettingsService Open() => new(Path);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private static void Equal(string expected, string actual)
    {
        if (expected != actual) throw new Exception($"expected <{expected}>, got <{actual}>");
    }
}
