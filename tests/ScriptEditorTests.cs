#:property TargetFramework=net10.0
#:property PublishAot=false

using Packman.Helpers;

// ModuleVersion read from a package's PSAppDeployToolkit.psd1, using the production source.
// The file is written to a temporary package folder for each check; nothing else is touched.
//   dotnet run --file tests/ScriptEditorTests.cs -c Release
// Exits non-zero when a check fails.
internal static class ScriptEditorTests
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static readonly (string Name, Action Check)[] Checks =
    [
        ("A quoted ModuleVersion is read", PresentVersionRead),
        ("Single and double quotes both work, with or without spaces around =", QuoteStylesRead),
        ("A commented-out ModuleVersion is skipped", CommentedVersionSkipped),
        ("The live ModuleVersion wins over a commented one above it", LiveVersionWinsOverComment),
        ("A missing psd1 returns null", MissingFileNull),
        ("A missing PSAppDeployToolkit folder returns null", MissingFolderNull),
        ("A psd1 without ModuleVersion returns null", NoVersionNull),
        ("A malformed version returns null", MalformedVersionNull),
        ("An unquoted version returns null", UnquotedVersionNull),
        ("An unreadable psd1 returns null instead of throwing", UnreadableNull),
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

    // ── Checks ──

    private static void PresentVersionRead() =>
        Equal("4.1.7", ReadWith("ModuleVersion = '4.1.7'"));

    private static void QuoteStylesRead()
    {
        Equal("4.1.7", ReadWith("ModuleVersion=\"4.1.7\""));
        Equal("4.1.8", ReadWith("    ModuleVersion   =   '4.1.8'"));
    }

    private static void CommentedVersionSkipped() =>
        Equal(null, ReadWith("# ModuleVersion = '4.1.7'"));

    private static void LiveVersionWinsOverComment() =>
        Equal("4.1.7", ReadWith("# ModuleVersion = '4.2.0'\nModuleVersion = '4.1.7'"));

    private static void MissingFileNull()
    {
        var package = NewPackage();
        Equal(null, PsadtManifest.ReadModuleVersion(package));
    }

    private static void MissingFolderNull()
    {
        var package = Path.Combine(Path.GetTempPath(), "packman-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        try
        {
            Equal(null, PsadtManifest.ReadModuleVersion(package));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    private static void NoVersionNull() =>
        Equal(null, ReadWith("RootModule = 'PSAppDeployToolkit.psm1'\nGUID = '1234'"));

    private static void MalformedVersionNull() =>
        Equal(null, ReadWith("ModuleVersion = 'four.one'"));

    private static void UnquotedVersionNull() =>
        Equal(null, ReadWith("ModuleVersion = 4.1.7"));

    private static void UnreadableNull()
    {
        // A directory where the psd1 should be: reading it throws, and the reader must not.
        var package = NewPackage();
        var manifest = Path.Combine(package, "PSAppDeployToolkit", "PSAppDeployToolkit.psd1");
        Directory.CreateDirectory(manifest);
        try
        {
            Equal(null, PsadtManifest.ReadModuleVersion(package));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    // ── Helpers ──

    /// <summary>Writes the psd1 with the given text into a fresh package folder and reads its version.</summary>
    private static string? ReadWith(string manifestText)
    {
        var package = NewPackage();
        try
        {
            File.WriteAllText(Path.Combine(package, "PSAppDeployToolkit", "PSAppDeployToolkit.psd1"), manifestText);
            return PsadtManifest.ReadModuleVersion(package);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    /// <summary>A package folder with an empty PSAppDeployToolkit subfolder, ready for a psd1.</summary>
    private static string NewPackage()
    {
        var package = Path.Combine(Path.GetTempPath(), "packman-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(package, "PSAppDeployToolkit"));
        return package;
    }

    // ── Assertions ──

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected <{expected}>, got <{actual}>");
    }
}
