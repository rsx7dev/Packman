using Packman.Helpers;
using Packman.Models;
using System.Text.Json;

namespace Packman.Services;

/// <summary>
/// The one mapping between <see cref="DetectionRule"/> and Graph's win32LobApp
/// detectionRules. Create and edit both go through here, so what the Upload step sends
/// is exactly what the detail page would send.
/// </summary>
public static class DetectionRuleGraph
{
    public static Dictionary<string, object?> Serialize(DetectionRule r) => r.Type switch
    {
        DetectionRuleType.MSI => new()
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppProductCodeDetection",
            ["productCode"] = r.Path.Trim(),
            ["productVersion"] = r.CheckVersion ? r.FileOrFolderName.Trim() : null,
            ["productVersionOperator"] = r.CheckVersion ? DefaultOperator(r.Operator) : "notConfigured",
        },
        DetectionRuleType.File => new()
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemDetection",
            ["path"] = r.Path.Trim(),
            ["fileOrFolderName"] = r.FileOrFolderName.Trim(),
            ["check32BitOn64System"] = r.Check32BitOn64System,
            ["detectionType"] = DetectionType(r),
            ["operator"] = OperatorNeedsValue(DetectionType(r)) ? DefaultOperator(r.Operator) : "notConfigured",
            ["detectionValue"] = OperatorNeedsValue(DetectionType(r)) ? r.DetectionValue.Trim() : null,
        },
        DetectionRuleType.Registry => new()
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryDetection",
            ["keyPath"] = r.Path.Trim(),
            // No value name checks the key itself; Graph takes that as null.
            ["valueName"] = string.IsNullOrWhiteSpace(r.FileOrFolderName) ? null : r.FileOrFolderName.Trim(),
            ["check32BitOn64System"] = r.Check32BitOn64System,
            ["detectionType"] = DetectionType(r),
            ["operator"] = OperatorNeedsValue(DetectionType(r)) ? DefaultOperator(r.Operator) : "notConfigured",
            ["detectionValue"] = OperatorNeedsValue(DetectionType(r)) ? r.DetectionValue.Trim() : null,
        },
        DetectionRuleType.Script => new()
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptDetection",
            ["scriptContent"] = r.ScriptContent,
            ["enforceSignatureCheck"] = r.EnforceSignatureCheck,
            ["runAs32Bit"] = r.RunAs32Bit,
        },
        _ => throw new NotSupportedException($"Unknown detection rule type: {r.Type}"),
    };

    /// <summary>Reads the detectionRules array off a win32LobApp document.</summary>
    public static List<DetectionRule> Parse(JsonElement app)
    {
        var rules = new List<DetectionRule>();
        if (app.ValueKind != JsonValueKind.Object || !app.TryGetProperty("detectionRules", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return rules;

        foreach (var r in arr.EnumerateArray())
        {
            switch (r.GetSafeString("@odata.type"))
            {
                case "#microsoft.graph.win32LobAppProductCodeDetection":
                    var productVersion = r.GetSafeString("productVersion");
                    rules.Add(new DetectionRule
                    {
                        Type = DetectionRuleType.MSI,
                        Path = r.GetSafeString("productCode"),
                        CheckVersion = !string.IsNullOrEmpty(productVersion),
                        Operator = r.GetSafeString("productVersionOperator"),
                        FileOrFolderName = productVersion,
                    });
                    break;
                case "#microsoft.graph.win32LobAppFileSystemDetection":
                    rules.Add(new DetectionRule
                    {
                        Type = DetectionRuleType.File,
                        Path = r.GetSafeString("path"),
                        FileOrFolderName = r.GetSafeString("fileOrFolderName"),
                        DetectionType = r.GetSafeString("detectionType"),
                        Operator = r.GetSafeString("operator"),
                        DetectionValue = r.GetSafeString("detectionValue"),
                        CheckVersion = r.GetSafeString("detectionType") == "version",
                        Check32BitOn64System = r.GetSafeBool("check32BitOn64System"),
                    });
                    break;
                case "#microsoft.graph.win32LobAppRegistryDetection":
                    rules.Add(new DetectionRule
                    {
                        Type = DetectionRuleType.Registry,
                        Path = r.GetSafeString("keyPath"),
                        FileOrFolderName = r.GetSafeString("valueName"),
                        DetectionType = r.GetSafeString("detectionType"),
                        Operator = r.GetSafeString("operator"),
                        DetectionValue = r.GetSafeString("detectionValue"),
                        Check32BitOn64System = r.GetSafeBool("check32BitOn64System"),
                    });
                    break;
                case "#microsoft.graph.win32LobAppPowerShellScriptDetection":
                    rules.Add(new DetectionRule
                    {
                        Type = DetectionRuleType.Script,
                        ScriptContent = r.GetSafeString("scriptContent"),
                        EnforceSignatureCheck = r.GetSafeBool("enforceSignatureCheck"),
                        RunAs32Bit = r.GetSafeBool("runAs32Bit"),
                    });
                    break;
            }
        }
        return rules;
    }

    // A rule built with CheckVersion but no explicit type is a version rule.
    private static string DetectionType(DetectionRule r)
    {
        if (!string.IsNullOrEmpty(r.DetectionType) && r.DetectionType != "exists") return r.DetectionType;
        return r.CheckVersion ? "version" : "exists";
    }

    private static bool OperatorNeedsValue(string detectionType) =>
        detectionType is "version" or "string" or "integer" or "sizeInMB" or "modifiedDate" or "createdDate";

    private static string DefaultOperator(string op) =>
        string.IsNullOrEmpty(op) || op == "notConfigured" ? "equal" : op;
}
