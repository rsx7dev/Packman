using System.IO;
using System.Management.Automation.Language;
using Packman.Helpers;

namespace Packman.Services;

/// <summary>Checks the staged PSADT package without running its deployment script.</summary>
public static class PackagePreflight
{
    public static IReadOnlyList<string> Check(string packageRoot)
    {
        var issues = new List<string>();
        var application = PsadtLayout.ApplicationFolder(packageRoot);
        var scriptPath = PsadtLayout.ScriptPath(packageRoot);
        var required = new[]
        {
            PsadtLayout.ScriptName,
            PsadtLayout.SetupFileName,
            Path.Combine(PsadtLayout.ToolkitFolderName, "PSAppDeployToolkit.psd1")
        };
        foreach (var file in required)
            if (!File.Exists(Path.Combine(application, file)))
                issues.Add($"Missing {PsadtLayout.ApplicationFolderName}/{file}. Use a complete PSADT v4 template in Settings → Network paths.");

        if (!File.Exists(scriptPath)) return issues;
        try
        {
            var ast = Parser.ParseInput(File.ReadAllText(scriptPath), out _, out var errors);
            issues.AddRange(errors.Take(3).Select(e => $"Script line {e.Extent.StartLineNumber}: {e.Message}"));

            // Inspect string expressions, not raw text: comments and help examples should
            // not block a package. Match the exact placeholders emitted by our generator.
            var placeholders = ast.FindAll(node => node is StringConstantExpressionAst literal &&
                (literal.Value.Equals("<silent flags>", StringComparison.OrdinalIgnoreCase) ||
                 literal.Value.Equals("<uninstall flags>", StringComparison.OrdinalIgnoreCase)), searchNestedScriptBlocks: true);
            if (placeholders.Any())
                issues.Add("Replace the EXE install/uninstall flag placeholders in Edit script with the vendor's silent switches, then save the script.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add($"Cannot read the deployment script: {ex.Message}");
        }
        return issues;
    }

    public static void EnsureReady(string packageRoot)
    {
        var issues = Check(packageRoot);
        if (issues.Count != 0)
            throw new InvalidOperationException("Package needs attention before publishing:\n" + string.Join("\n", issues));
    }
}
