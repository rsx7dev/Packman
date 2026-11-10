namespace Packman.Models;

/// <summary>Builds the detection rules Packman's pickers can express, one way, in one place.</summary>
public static class DetectionRuleFactory
{
    public const string DefaultVersionOperator = "greaterThanOrEqual";

    // check32BitOn64System makes Intune resolve %ProgramFiles%, System32 and HKLM\SOFTWARE in the
    // 32-bit view. That is right for a 32-bit app and wrong for a 64-bit one, so it follows the
    // package's architecture and is off (the Intune portal's default) when that is not known.
    public static DetectionRule FileExists(string path, string fileOrFolderName, bool is32BitApp = false) => new()
    {
        Type = DetectionRuleType.File,
        Path = path.Trim(),
        FileOrFolderName = fileOrFolderName.Trim(),
        DetectionType = "exists",
        Check32BitOn64System = is32BitApp,
    };

    public static DetectionRule FileVersion(string path, string fileName, string version, string? op = null, bool is32BitApp = false) => new()
    {
        Type = DetectionRuleType.File,
        Path = path.Trim(),
        FileOrFolderName = fileName.Trim(),
        DetectionType = "version",
        CheckVersion = true,
        Operator = string.IsNullOrWhiteSpace(op) ? DefaultVersionOperator : op,
        DetectionValue = version.Trim(),
        Check32BitOn64System = is32BitApp,
    };

    public static DetectionRule RegistryKeyExists(string hive, string keyPath, string valueName, bool is32BitApp = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hive);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        // A blank value name is the documented "the key itself exists" check.
        ArgumentNullException.ThrowIfNull(valueName);

        return new()
        {
            Type = DetectionRuleType.Registry,
            Path = RegistryHiveNames.Combine(hive, keyPath),
            FileOrFolderName = valueName.Trim(),
            DetectionType = "exists",
            Check32BitOn64System = is32BitApp,
        };
    }

    public static DetectionRule Msi(string productCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);

        return new()
        {
            Type = DetectionRuleType.MSI,
            // As Windows Installer registers it; anything that is not a GUID is left for validation to refuse.
            Path = DetectionInput.NormalizeProductCode(productCode) ?? productCode.Trim(),
        };
    }

    /// <summary>
    /// The rule for one of the four <see cref="DetectionMethod"/> choices, from the fields
    /// the pickers collect. Null when the method is unknown.
    /// </summary>
    public static DetectionRule? FromMethod(string method,
        string path, string name, string version, string? versionOperator,
        string hive, string keyPath, string valueName,
        string productCode, bool is32BitApp = false) => method switch
    {
        DetectionMethod.FileExists => FileExists(path, name, is32BitApp),
        DetectionMethod.FileVersion => FileVersion(path, name, version, versionOperator, is32BitApp),
        DetectionMethod.RegistryKey => RegistryKeyExists(hive, keyPath, valueName, is32BitApp),
        DetectionMethod.MsiProductCode => Msi(productCode),
        _ => null,
    };
}
