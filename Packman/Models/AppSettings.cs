using Packman.Helpers;
using System.Text.Json.Serialization;

namespace Packman.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthMode { Interactive, AppRegistration }

/// <summary>How Remote test reaches the computer the package runs on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteTestMethod
{
    /// <summary>PowerShell remoting; this computer is tested through an elevated helper instead.</summary>
    WinRM,
    /// <summary>Sysinternals PsExec over the admin shares, for targets without WinRM.</summary>
    PsExec,
    /// <summary>A disposable Windows Sandbox on this computer.</summary>
    Sandbox,
}

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
    // "Got it" on the Create page's welcome card; it still returns while a prerequisite is missing.
    public bool WelcomeCardDismissed { get; set; } = false;
    public AuthMode AuthMode { get; set; } = AuthMode.Interactive;
    public AuthConfig Authentication { get; set; } = new();
    public CodeSigningConfig CodeSigning { get; set; } = new();
    public NetworkPathsConfig NetworkPaths { get; set; } = new();
    public PackageLayoutConfig PackageLayout { get; set; } = new();
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

        /// <summary>A template of your own; empty uses the one bundled with Packman.</summary>
        public string PSADTTemplate { get; set; } = "";

        /// <summary>An IntuneWinAppUtil.exe of your own; empty uses the pinned build Packman downloads from Microsoft.</summary>
        public string IntuneWinAppUtil { get; set; } = "";

        /// <summary>Parent folder on Remote test targets; Packman stages in its Packman subfolder. Empty uses C:\ProgramData.</summary>
        public string RemoteStagingFolder { get; set; } = "";

        /// <summary>PsExec64.exe or PsExec.exe for Remote test over PsExec; empty looks on PATH. Not shipped with Packman.</summary>
        public string PsExec { get; set; } = "";

        /// <summary>Where upload and update logs go, e.g. a share for an audit trail. Empty uses %LocalAppData%\Packman\Logs.</summary>
        public string UploadLogsFolder { get; set; } = "";

        [JsonIgnore]
        public string PSADTTemplateInUse => string.IsNullOrWhiteSpace(PSADTTemplate) ? BundledTools.PsadtTemplate : PSADTTemplate;

        [JsonIgnore]
        public string IntuneWinAppUtilInUse => string.IsNullOrWhiteSpace(IntuneWinAppUtil) ? BundledTools.IntuneWinAppUtil : IntuneWinAppUtil;

        /// <summary>True once the packages folder is set; until then, first-run setup is shown.</summary>
        public bool IsComplete => !string.IsNullOrWhiteSpace(IntuneApplications);
    }

    /// <summary>Folders a new package gets besides Application and Intune. Existing packages are never changed.</summary>
    public class PackageLayoutConfig
    {
        public bool IconFolder { get; set; } = true;
        // Created empty in every new package; plain folder names.
        public List<string> ExtraFolders { get; set; } = new();

        /// <summary>
        /// How a new package's folder under the Intune Applications Path is named. Tokens:
        /// %vendor% %appName%; one '\' allowed for a vendor folder. The version is always a subfolder.
        /// </summary>
        // Same text as PackagePaths.DefaultFolderTemplate; a literal so this model compiles on its own.
        public string FolderNameTemplate { get; set; } = "%vendor%_%appName%";
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

        // How long a publish waits for Intune to report the new app as published.
        public const int DefaultPublishWaitMinutes = 10;
        public const int MaxPublishWaitMinutes = 120;

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

        /// <summary>
        /// Minutes a publish waits for Intune to finish processing the content before it gives
        /// up on supersedence and assignments, which Intune only accepts on a published app.
        /// </summary>
        public int PublishWaitMinutes { get; set; } = DefaultPublishWaitMinutes;

        /// <summary><see cref="PublishWaitMinutes"/> within its limits; a settings file can hold anything.</summary>
        [JsonIgnore]
        public TimeSpan PublishWait => TimeSpan.FromMinutes(Math.Clamp(PublishWaitMinutes, 1, MaxPublishWaitMinutes));

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
        public RemoteTestMethod Method { get; set; } = RemoteTestMethod.WinRM;
        /// <summary>Whether a Windows Sandbox test can reach the network; installers that download need it.</summary>
        public bool SandboxNetworking { get; set; } = true;
    }

    public class ExistingGroupAssignment
    {
        public string GroupName { get; set; } = "";
        public AssignmentIntent Intent { get; set; } = AssignmentIntent.Required;
    }

    public sealed record RestartBehaviorOption(string Value, string Label);
}
