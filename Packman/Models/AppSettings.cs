using System.Text.Json.Serialization;

namespace Packman.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthMode { Interactive, AppRegistration }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssignmentIntent { Available, Required, Uninstall }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme { System, Dark, Light }

public class AppSettings
{
    // Dark by default: upgrades keep their existing look.
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    // The left navigation is shown by default; hiding it swaps in the bottom menu bar.
    public bool NavHidden { get; set; } = false;
    public AuthMode AuthMode { get; set; } = AuthMode.Interactive;
    public AuthConfig Authentication { get; set; } = new();
    public CodeSigningConfig CodeSigning { get; set; } = new();
    public NetworkPathsConfig NetworkPaths { get; set; } = new();
    public GroupAssignmentConfig GroupAssignment { get; set; } = new();
    public IntuneDefaultsConfig IntuneDefaults { get; set; } = new();
    public RemoteTestConfig RemoteTest { get; set; } = new();

    public class AuthConfig
    {
        public string TenantId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string CertificateThumbprint { get; set; } = "";
    }

    public class CodeSigningConfig
    {
        public bool Enabled { get; set; } = false;
        public string CertificateThumbprint { get; set; } = "";
        public string CertificateName { get; set; } = "";
        public string CertificateSubject { get; set; } = "";
        public string TimestampServer { get; set; } = "http://timestamp.digicert.com";
    }

    public class NetworkPathsConfig
    {
        public string IntuneApplications { get; set; } = "";
        public string PSADTTemplate { get; set; } = "";
        public string IntuneWinAppUtil { get; set; } = "";
    }

    public class GroupAssignmentConfig
    {
        // New install security group per uploaded package.
        public bool CreateGroupPerPackage { get; set; } = false;
        // Tokens: %vendor% %appName% %appVersion%.
        public string GroupNameTemplate { get; set; } = "%vendor%_%appName%_%appVersion%_Install";
        public AssignmentIntent NewGroupIntent { get; set; } = AssignmentIntent.Required;

        // Matching uninstall group per uploaded package.
        public bool CreateUninstallGroupPerPackage { get; set; } = false;
        public string UninstallGroupNameTemplate { get; set; } = "%vendor%_%appName%_%appVersion%_Uninstall";

        // Groups assigned to every upload.
        public List<ExistingGroupAssignment> ExistingGroups { get; set; } = new();

        /// <summary>True when this config produces at least one assignment.</summary>
        public bool HasAnyAssignment() =>
            CreateGroupPerPackage || CreateUninstallGroupPerPackage || ExistingGroups.Count > 0;

        /// <summary>Snapshot for one upload, isolated from later Settings edits.</summary>
        public GroupAssignmentConfig Clone() => new()
        {
            CreateGroupPerPackage = CreateGroupPerPackage,
            GroupNameTemplate = GroupNameTemplate,
            NewGroupIntent = NewGroupIntent,
            CreateUninstallGroupPerPackage = CreateUninstallGroupPerPackage,
            UninstallGroupNameTemplate = UninstallGroupNameTemplate,
            ExistingGroups = ExistingGroups.Select(g => new ExistingGroupAssignment
            {
                GroupName = g.GroupName,
                Intent = g.Intent,
            }).ToList(),
        };
    }

    public class IntuneDefaultsConfig
    {
        public const string DefaultInstallCommand = "Invoke-AppDeployToolkit.exe Install";
        public const string DefaultUninstallCommand = "Invoke-AppDeployToolkit.exe Uninstall";
        public const string DefaultDisplayNameTemplate = "%vendor% %appName% %appVersion%";

        // What the Intune admin center pre-selects for a new Win32 app.
        public const string DefaultRestartBehavior = "allow";
        public const int DefaultMaxRunTimeMinutes = 60;

        /// <summary>Graph deviceRestartBehavior values with the admin center's wording.</summary>
        public static readonly RestartBehaviorOption[] RestartBehaviors =
        [
            new("basedOnReturnCode", "Determine behavior based on return codes"),
            new("allow", "App install may force a device restart"),
            new("suppress", "No specific action"),
            new("force", "Intune will force a mandatory device restart"),
        ];

        // Pre-filled on the upload step.
        public RequirementInfo Requirements { get; set; } = new();
        // Sent with every uploaded Win32 app.
        public List<ReturnCodeInfo> ReturnCodes { get; set; } = ReturnCodeInfo.Defaults();

        // Command lines Intune runs.
        public string InstallCommand { get; set; } = DefaultInstallCommand;
        public string UninstallCommand { get; set; } = DefaultUninstallCommand;

        // Install experience sent with every Win32 app: deviceRestartBehavior and maxRunTimeInMinutes.
        public string RestartBehavior { get; set; } = DefaultRestartBehavior;
        public int MaxRunTimeMinutes { get; set; } = DefaultMaxRunTimeMinutes;

        // Company Portal links; sent only when set.
        public string PrivacyUrl { get; set; } = "";
        public string InformationUrl { get; set; } = "";

        // Tokens: %vendor% %appName% %appVersion%.
        public string DisplayNameTemplate { get; set; } = DefaultDisplayNameTemplate;
    }

    public class RemoteTestConfig
    {
        // Most recent first.
        public List<string> RecentComputers { get; set; } = new();
        // Off by default so a re-run only copies what changed.
        public bool CleanupAfterRun { get; set; } = false;
    }

    public class ExistingGroupAssignment
    {
        public string GroupName { get; set; } = "";
        public AssignmentIntent Intent { get; set; } = AssignmentIntent.Required;
    }

    public sealed record RestartBehaviorOption(string Value, string Label);
}
