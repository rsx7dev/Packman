namespace Packman.Models;

/// <summary>Full application details from Graph, fetched when a row is opened.</summary>
public class ApplicationDetail : IntuneApplication
{
    public string InstallContext { get; set; } = "System";
    public string InstallCommand { get; set; } = "Invoke-AppDeployToolkit.exe Install";
    public string UninstallCommand { get; set; } = "Invoke-AppDeployToolkit.exe Uninstall";

    public string Owner { get; set; } = "";
    public string Developer { get; set; } = "";
    public string RestartBehavior { get; set; } = "";
    public int MaxRunTimeMinutes { get; set; }
    public int MinDiskSpaceMB { get; set; }
    public string MinimumOperatingSystem { get; set; } = "Not specified";
    public string Notes { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedDateTime { get; set; } = DateTime.MinValue;
    public DateTime LastModifiedDateTime { get; set; } = DateTime.MinValue;
    public string SetupFilePath { get; set; } = "";

    public List<DetectionRule> DetectionRules { get; set; } = new();
    public List<AssignedGroup> AssignedGroups { get; set; } = new();
    public InstallationStatistics? Statistics { get; set; }
    public ApplicationRelationships? Relationships { get; set; }

    // ── Display helpers ──
    public string SizeFormatted => Size switch
    {
        > 1024L * 1024 * 1024 => $"{Size / (1024.0 * 1024 * 1024):F1} GB",
        > 1024L * 1024 => $"{Size / (1024.0 * 1024):F1} MB",
        > 1024 => $"{Size / 1024.0:F1} KB",
        > 0 => $"{Size} B",
        _ => "—",
    };

    public string CreatedFormatted => CreatedDateTime != DateTime.MinValue ? CreatedDateTime.ToLocalTime().ToString("MMM d, yyyy") : "—";
    public string LastModifiedFormatted => LastModifiedDateTime != DateTime.MinValue ? LastModifiedDateTime.ToLocalTime().ToString("MMM d, yyyy") : "—";

    /// <summary>Header subtitle, e.g. "Mozilla · v124.0.1 · System context · Win32".</summary>
    public string SubLine => $"{Publisher} · v{Version} · {InstallContext} context · Win32";

    public string StatusLabel => PublishingState?.ToLowerInvariant() switch
    {
        "published" => "Published",
        "processing" => "Processing",
        "notpublished" => "Not published",
        _ => string.IsNullOrEmpty(PublishingState) ? "Unknown" : PublishingState,
    };

    public string StatusKind => PublishingState?.ToLowerInvariant() switch
    {
        "published" => "ok",
        "processing" => "warn",
        _ => "mut",
    };

    /// <summary>App id shortened for the sidebar; copy gives the full value.</summary>
    public string IdShort => Id.Length > 13 ? $"{Id[..8]}…{Id[^4..]}" : Id;

    // ── Requirements (Deployment tab tile strip) ──
    public string RestartBehaviorText => RestartBehavior switch
    {
        "basedOnReturnCode" => "By return code",
        "allow" => "App may restart",
        "suppress" => "Suppress restart",
        "force" => "Force restart",
        _ => "—",
    };
    public string MaxRunTimeText => MaxRunTimeMinutes > 0 ? $"{MaxRunTimeMinutes} min" : "—";
    public string MinDiskSpaceText => MinDiskSpaceMB > 0 ? $"{MinDiskSpaceMB} MB" : "Not specified";
}

public class ApplicationRelationships
{
    public List<IntuneApplication> Dependencies { get; } = new();
    public List<IntuneApplication> Supersedes { get; } = new();
    public List<IntuneApplication> SupersededBy { get; } = new();
    public string Error { get; set; } = "";
}

public class AssignedGroup
{
    public string AssignmentId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string AssignmentType { get; set; } = "";   // required | available | uninstall
    public bool IsExcluded { get; set; }

    // StatusBadgeTemplate binds StatusLabel + StatusKind.
    public string StatusLabel => IsExcluded ? $"Excluded · {IntentLabel}" : IntentLabel;
    private string IntentLabel => AssignmentType?.ToLowerInvariant() switch
    {
        "required" => "Required",
        "uninstall" => "Uninstall",
        "available" or "availablewithoutenrollment" => "Available",
        _ => string.IsNullOrEmpty(AssignmentType) ? "Unknown" : AssignmentType,
    };
    public string StatusKind => IsExcluded ? "mut" : AssignmentType?.ToLowerInvariant() switch
    {
        "required" => "ok",
        "uninstall" => "bad",
        _ => "mut",
    };
    public string ChipText => $"{GroupName} · {StatusLabel}";
}

/// <summary>Per-app device install rollup from the Intune reporting endpoint.</summary>
public class InstallationStatistics
{
    public int SuccessfulInstalls { get; set; }
    public int FailedInstalls { get; set; }
    public int PendingInstalls { get; set; }
    public int NotInstalled { get; set; }
    public int NotApplicable { get; set; }
    public int TotalDevices { get; set; }
}
