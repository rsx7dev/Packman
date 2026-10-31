#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Packman.Helpers;

// First-run setup checks without a window: the package folder layout helpers, the rules for an
// extra folder name, and the path checks behind the Paths step (applications root, PSADT template,
// IntuneWinAppUtil). Uses the production sources; nothing touches the network or a real settings
// file. The read-only root is made with UnixFileMode on Linux and a deny-write ACL on Windows.
//   dotnet run --file tests/SetupUnitTests.cs -c Release
// Exits non-zero when a check fails.
internal static class SetupUnitTests
{
    private const string Root = @"C:\Packages\Reader";

    private static int _passed;
    private static readonly List<string> Failures = new();

    public static int Main()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("Folder names are the fixed PSADT v4 names", FolderNames),
            ("Application, Files, Intune and Icon paths are built under the package root", LayoutPaths),
            ("ScriptPath points at the deployment script inside Application", ScriptPathInApplication),
            ("OptionalFolders lists Icon first when on, then the extras in order", OptionalFoldersOrder),
            ("OptionalFolders with Icon off and no extras is empty", OptionalFoldersEmpty),
            ("A valid extra folder name has no problem", ValidNameAccepted),
            ("An empty extra folder name is rejected", EmptyNameRejected),
            ("A name with a path separator is rejected", SeparatorRejected),
            ("A name with a Windows-invalid character is rejected", InvalidCharRejected),
            ("A name with a control character is rejected", ControlCharRejected),
            ("A name ending with a dot is rejected", TrailingDotRejected),
            ("A reserved device name is rejected, with or without an extension", ReservedNameRejected),
            ("A name that only starts like a reserved name is accepted", ReservedPrefixAccepted),
            ("Application, Intune and Icon are rejected in any case", FixedFolderRejected),
            ("A duplicate name is rejected case-insensitively", DuplicateRejected),
            ("A new name that is not in the list is accepted", NewNameAccepted),
            ("A blank applications root is bad", BlankRootBad),
            ("A missing applications root is bad", MissingRootBad),
            ("A writable applications root is ok and leaves no test file", WritableRootOk),
            ("A read-only applications root is warn and leaves no test file", ReadOnlyRootWarns),
            ("A template folder with the script and toolkit folder is ok", TemplateFolderOk),
            ("A template given as its Invoke-AppDeployToolkit.ps1 is ok", TemplateScriptOk),
            ("A template without the script is bad", TemplateWithoutScriptBad),
            ("A template without the PSAppDeployToolkit folder is bad", TemplateWithoutToolkitBad),
            ("A blank template path is bad", BlankTemplateBad),
            ("An IntuneWinAppUtil.exe in any case is ok", ToolAnyCaseOk),
            ("A tool path to a different file name is bad", ToolWrongNameBad),
            ("A tool path to a missing file is bad", ToolMissingBad),
            ("A tool path to a folder is bad", ToolFolderBad),
        };

        foreach (var (name, check) in checks)
        {
            try
            {
                check();
                _passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (SkipException ex)
            {
                _passed++;
                Console.WriteLine($"SKIP  {name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {checks.Length} checks failed.");
        return Failures.Count == 0 ? 0 : 1;
    }

    // PsadtLayout

    private static void FolderNames()
    {
        Equal("Application", PsadtLayout.ApplicationFolderName);
        Equal("Files", PsadtLayout.FilesFolderName);
        Equal("Intune", PsadtLayout.IntuneFolderName);
        Equal("Icon", PsadtLayout.IconFolderName);
        Equal("PSAppDeployToolkit", PsadtLayout.ToolkitFolderName);
    }

    private static void LayoutPaths()
    {
        Equal(Path.Combine(Root, "Application"), PsadtLayout.ApplicationFolder(Root));
        Equal(Path.Combine(Root, "Application", "Files"), PsadtLayout.FilesFolder(Root));
        Equal(Path.Combine(Root, "Intune"), PsadtLayout.IntuneFolder(Root));
        Equal(Path.Combine(Root, "Icon"), PsadtLayout.IconFolder(Root));
    }

    private static void ScriptPathInApplication()
    {
        Equal(Path.Combine(Root, "Application", "Invoke-AppDeployToolkit.ps1"), PsadtLayout.ScriptPath(Root));
    }

    private static void OptionalFoldersOrder()
    {
        SequenceEqual(new[] { "Icon" }, PsadtLayout.OptionalFolders(true, Array.Empty<string>()));
        SequenceEqual(new[] { "Icon", "Docs", "Tools" }, PsadtLayout.OptionalFolders(true, new[] { "Docs", "Tools" }));
        SequenceEqual(new[] { "Tools", "Docs" }, PsadtLayout.OptionalFolders(false, new[] { "Tools", "Docs" }));
    }

    private static void OptionalFoldersEmpty()
    {
        SequenceEqual(Array.Empty<string>(), PsadtLayout.OptionalFolders(false, Array.Empty<string>()));
    }

    // ExtraFolderProblem: null when valid, otherwise a one-line reason.

    private static void ValidNameAccepted()
    {
        IsNull(PsadtLayout.ExtraFolderProblem("Docs", Array.Empty<string>()));
        IsNull(PsadtLayout.ExtraFolderProblem("Vendor Notes", new[] { "Tools" }));
    }

    private static void EmptyNameRejected()
    {
        NotNull(PsadtLayout.ExtraFolderProblem("", Array.Empty<string>()));
    }

    private static void SeparatorRejected()
    {
        NotNull(PsadtLayout.ExtraFolderProblem(@"a\b", Array.Empty<string>()));
        NotNull(PsadtLayout.ExtraFolderProblem("a/b", Array.Empty<string>()));
    }

    private static void InvalidCharRejected()
    {
        foreach (var c in "<>:\"|?*")
            NotNull(PsadtLayout.ExtraFolderProblem($"a{c}b", Array.Empty<string>()));
    }

    private static void ControlCharRejected()
    {
        NotNull(PsadtLayout.ExtraFolderProblem("a\u0001b", Array.Empty<string>()));
        NotNull(PsadtLayout.ExtraFolderProblem("a\tb", Array.Empty<string>()));
    }

    private static void TrailingDotRejected()
    {
        NotNull(PsadtLayout.ExtraFolderProblem("Docs.", Array.Empty<string>()));
    }

    private static void ReservedNameRejected()
    {
        foreach (var name in new[] { "CON", "con", "PRN", "aux", "NUL", "COM0", "COM1", "com9", "LPT0", "LPT5", "lpt9", "CON.txt", "nul.log" })
            NotNull(PsadtLayout.ExtraFolderProblem(name, Array.Empty<string>()));
    }

    private static void ReservedPrefixAccepted()
    {
        IsNull(PsadtLayout.ExtraFolderProblem("CONSOLE", Array.Empty<string>()));
        IsNull(PsadtLayout.ExtraFolderProblem("COM10", Array.Empty<string>()));
    }

    private static void FixedFolderRejected()
    {
        foreach (var name in new[] { "Application", "intune", "ICON" })
            NotNull(PsadtLayout.ExtraFolderProblem(name, Array.Empty<string>()));
    }

    private static void DuplicateRejected()
    {
        NotNull(PsadtLayout.ExtraFolderProblem("docs", new[] { "Docs" }));
    }

    private static void NewNameAccepted()
    {
        IsNull(PsadtLayout.ExtraFolderProblem("Tools", new[] { "Docs", "Scripts" }));
    }

    // SetupChecks

    private static void BlankRootBad()
    {
        Kind("bad", SetupChecks.ApplicationsRoot(""));
        Kind("bad", SetupChecks.ApplicationsRoot("   "));
    }

    private static void MissingRootBad()
    {
        var missing = Path.Combine(TempDir(), "does-not-exist");
        Kind("bad", SetupChecks.ApplicationsRoot(missing));
    }

    private static void WritableRootOk()
    {
        var dir = TempDir();
        Kind("ok", SetupChecks.ApplicationsRoot(dir));
        Empty(Directory.GetFiles(dir));
    }

    private static void ReadOnlyRootWarns()
    {
        var dir = TempDir();
        SetReadOnly(dir, true);
        try
        {
            if (CanWriteInto(dir)) throw new SkipException("running with write access to a read-only folder (root?)");
            Kind("warn", SetupChecks.ApplicationsRoot(dir));
            Empty(Directory.GetFiles(dir));
        }
        finally
        {
            SetReadOnly(dir, false);
        }
    }

    private static void TemplateFolderOk()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Invoke-AppDeployToolkit.ps1"), "");
        Directory.CreateDirectory(Path.Combine(dir, "PSAppDeployToolkit"));
        Kind("ok", SetupChecks.Template(dir));
    }

    private static void TemplateScriptOk()
    {
        var dir = TempDir();
        var script = Path.Combine(dir, "Invoke-AppDeployToolkit.ps1");
        File.WriteAllText(script, "");
        Directory.CreateDirectory(Path.Combine(dir, "PSAppDeployToolkit"));
        Kind("ok", SetupChecks.Template(script));
    }

    private static void TemplateWithoutScriptBad()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "PSAppDeployToolkit"));
        Kind("bad", SetupChecks.Template(dir));
    }

    private static void TemplateWithoutToolkitBad()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Invoke-AppDeployToolkit.ps1"), "");
        Kind("bad", SetupChecks.Template(dir));
    }

    private static void BlankTemplateBad()
    {
        Kind("bad", SetupChecks.Template(""));
    }

    private static void ToolAnyCaseOk()
    {
        var dir = TempDir();
        var exe = Path.Combine(dir, "intunewinapputil.EXE");
        File.WriteAllText(exe, "");
        Kind("ok", SetupChecks.IntuneWinAppUtil(exe));
    }

    private static void ToolWrongNameBad()
    {
        var dir = TempDir();
        var exe = Path.Combine(dir, "other.exe");
        File.WriteAllText(exe, "");
        Kind("bad", SetupChecks.IntuneWinAppUtil(exe));
    }

    private static void ToolMissingBad()
    {
        Kind("bad", SetupChecks.IntuneWinAppUtil(Path.Combine(TempDir(), "IntuneWinAppUtil.exe")));
    }

    private static void ToolFolderBad()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "IntuneWinAppUtil.exe"));
        Kind("bad", SetupChecks.IntuneWinAppUtil(Path.Combine(dir, "IntuneWinAppUtil.exe")));
    }

    // Helpers

    private sealed class SkipException(string message) : Exception(message);

    private static string TempDir() => Directory.CreateTempSubdirectory("packman-setup-unit-").FullName;

    private static bool CanWriteInto(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $"probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Read-only on Linux by mode bits; on Windows a deny-write ACL for Everyone.
    private static void SetReadOnly(string dir, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            SetDenyWrite(dir, readOnly);
            return;
        }
        File.SetUnixFileMode(dir, readOnly
            ? UnixFileMode.UserRead | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void SetDenyWrite(string dir, bool deny)
    {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(everyone,
            FileSystemRights.CreateFiles | FileSystemRights.AppendData, AccessControlType.Deny);
        if (deny) security.AddAccessRule(rule);
        else security.RemoveAccessRule(rule);
        info.SetAccessControl(security);
    }

    private static void Kind(string expected, SetupCheck check)
    {
        Equal(expected, check.Kind);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected '{expected}', got '{actual}'");
    }

    private static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new Exception($"expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }

    private static void IsNull(string? value)
    {
        if (value != null) throw new Exception($"expected no problem, got '{value}'");
    }

    private static void NotNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new Exception("expected a problem reason, got none");
    }

    private static void Empty(IReadOnlyCollection<string> items)
    {
        if (items.Count != 0) throw new Exception($"expected no leftover files, found {items.Count}");
    }
}
