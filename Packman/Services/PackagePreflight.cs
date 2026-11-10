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
                issues.Add($"Missing {PsadtLayout.ApplicationFolderName}/{file}. Use a complete PSADT v4 template in Settings → Paths.");

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

            // PSADT 4.2+ phase blocks: an empty Install or Uninstall block deploys nothing and
            // still exits 0, so Intune would report success for an app that never installed.
            foreach (var phase in EmptyPhases(ast))
                issues.Add($"The {phase} phase in the script is empty. Add the {phase.ToLowerInvariant()} command in Edit script, then save the script.");

            // Written when the MSI's product code is not known; uninstall cannot work with it.
            var productCode = ast.FindAll(node => node is StringConstantExpressionAst literal &&
                literal.Value.Contains(PsadtScript.ProductCodePlaceholder, StringComparison.OrdinalIgnoreCase), searchNestedScriptBlocks: true);
            if (productCode.Any())
                issues.Add($"Replace {PsadtScript.ProductCodePlaceholder} in Edit script with the MSI's product code, then save the script.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add($"Cannot read the deployment script: {ex.Message}");
        }
        return issues;
    }

    /// <summary>"Install" and/or "Uninstall" when their New-Variable -Value { } block has no statements.</summary>
    internal static IEnumerable<string> EmptyPhases(Ast ast)
    {
        foreach (var command in ast.FindAll(n => n is CommandAst c && string.Equals(c.GetCommandName(), "New-Variable", StringComparison.OrdinalIgnoreCase), true).Cast<CommandAst>())
        {
            string? name = null;
            ScriptBlockAst? value = null;
            var elements = command.CommandElements;
            for (int i = 1; i < elements.Count - 1; i++)
            {
                if (elements[i] is not CommandParameterAst parameter) continue;
                if (parameter.ParameterName.Equals("Name", StringComparison.OrdinalIgnoreCase) && elements[i + 1] is StringConstantExpressionAst s)
                    name = s.Value;
                else if (parameter.ParameterName.Equals("Value", StringComparison.OrdinalIgnoreCase) && elements[i + 1] is ScriptBlockExpressionAst block)
                    value = block.ScriptBlock;
            }
            if (name is not ("Install" or "Uninstall") || value == null) continue;
            var empty = (value.EndBlock?.Statements.Count ?? 0) == 0 && (value.ProcessBlock?.Statements.Count ?? 0) == 0
                        && (value.BeginBlock?.Statements.Count ?? 0) == 0;
            if (empty) yield return name;
        }
    }

    public static void EnsureReady(string packageRoot)
    {
        var issues = Check(packageRoot);
        if (issues.Count != 0)
            throw new InvalidOperationException("Package needs attention before publishing:\n" + string.Join("\n", issues));
    }
}
