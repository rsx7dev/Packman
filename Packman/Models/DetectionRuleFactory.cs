namespace Packman.Models;

/// <summary>Builds the detection rules Packman's pickers can express, one way, in one place.</summary>
public static class DetectionRuleFactory
{
    public const string DefaultVersionOperator = "greaterThanOrEqual";

    public static DetectionRule FileExists(string path, string fileOrFolderName) => new()
    {
        Type = DetectionRuleType.File,
        Path = path.Trim(),
        FileOrFolderName = fileOrFolderName.Trim(),
        DetectionType = "exists",
        Check32BitOn64System = true,
    };

    public static DetectionRule FileVersion(string path, string fileName, string version, string? op = null) => new()
    {
        Type = DetectionRuleType.File,
        Path = path.Trim(),
        FileOrFolderName = fileName.Trim(),
        DetectionType = "version",
        CheckVersion = true,
        Operator = string.IsNullOrWhiteSpace(op) ? DefaultVersionOperator : op,
        DetectionValue = version.Trim(),
        Check32BitOn64System = true,
    };

    public static DetectionRule RegistryKeyExists(string hive, string keyPath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hive);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        return new()
        {
            Type = DetectionRuleType.Registry,
            Path = RegistryHiveNames.Combine(hive, keyPath),
            FileOrFolderName = valueName.Trim(),
            DetectionType = "exists",
        };
    }

    public static DetectionRule Msi(string productCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);

        return new()
        {
            Type = DetectionRuleType.MSI,
            Path = productCode.Trim(),
        };
    }

    /// <summary>
    /// The rule for one of the four <see cref="DetectionMethod"/> choices, from the fields
    /// the pickers collect. Null when the method is unknown.
    /// </summary>
    public static DetectionRule? FromMethod(string method,
        string path, string name, string version, string? versionOperator,
        string hive, string keyPath, string valueName,
        string productCode) => method switch
    {
        DetectionMethod.FileExists => FileExists(path, name),
        DetectionMethod.FileVersion => FileVersion(path, name, version, versionOperator),
        DetectionMethod.RegistryKey => RegistryKeyExists(hive, keyPath, valueName),
        DetectionMethod.MsiProductCode => Msi(productCode),
        _ => null,
    };
}
