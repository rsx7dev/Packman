using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace Packman.ViewModels;

/// <summary>
/// The Application detail screen: metadata, assignments, detection rules and the
/// install-status rollup for one app, plus its source folder on the share.
/// </summary>
public sealed class ApplicationDetailViewModel : ObservableObject
{
    private readonly IntuneService _apps = AppServices.Apps;

    public ApplicationDetailViewModel(IntuneApplication app)
    {
        _groupSearchBox = new IncrementalSearch<EntraGroup>(q => _apps.SearchGroupsAsync(q), () => OnPropertyChanged(nameof(HasGroupResults)));
        _memberSearchBox = new IncrementalSearch<GroupMember>(q => _apps.SearchDevicesAndUsersAsync(q), () => OnPropertyChanged(nameof(HasMemberResults)));
        // Seed from the list row so the header renders now; LoadAsync fills the rest.
        Detail = new ApplicationDetail
        {
            Id = app.Id,
            DisplayName = app.DisplayName,
            Version = app.Version,
            Publisher = app.Publisher,
            Category = app.Category,
            LastModified = app.LastModified,
            LastModifiedDateTime = app.LastModified,
            PublishingState = app.PublishingState,
        };
    }

    private ApplicationDetail _detail = null!;
    public ApplicationDetail Detail
    {
        get => _detail;
        private set
        {
            _detail = value;
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(HasInstall));
            OnPropertyChanged(nameof(HasUninstall));
            OnPropertyChanged(nameof(HeaderMeta));
            OnPropertyChanged(nameof(CategoryText));
            OnPropertyChanged(nameof(OwnerText));
            OnPropertyChanged(nameof(StatsEmptyText));
        }
    }

    /// <summary>Header subtitle, e.g. "Contoso · 4.2.0 · System context · Win32"; blank parts are left out.</summary>
    public string HeaderMeta => string.Join(" · ",
        new[] { Detail.Publisher, Detail.Version, $"{Detail.InstallContext} context", "Win32" }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public string CategoryText => string.IsNullOrWhiteSpace(Detail.Category) ? "None" : Detail.Category;
    public string OwnerText => string.IsNullOrWhiteSpace(Detail.Owner) ? "Not set" : Detail.Owner;

    // ── Connection: every write to Intune needs a session; signed out, the page is read-only ──
    public bool IsSignedIn => AppServices.Auth.IsSignedIn;

    /// <summary>Called by the view when the sign-in state changes.</summary>
    public void RaiseConnectionChanged() =>
        RaiseAll(nameof(IsSignedIn), nameof(CanRepublish), nameof(CanAddAssignment));

    private DateTime? _loadedAt;
    /// <summary>When the detail was last loaded from Intune; shown when the figures may be stale.</summary>
    public string LastSyncedText => _loadedAt is { } t ? $"Last synced {t:g}" : "";

    // ── Tabs: Overview / Package / Deployment ──
    private string _tab = "overview";
    public string Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsOverview));
            OnPropertyChanged(nameof(IsPackage));
            OnPropertyChanged(nameof(IsDeployment));
        }
    }
    public bool IsOverview => _tab == "overview";
    public bool IsPackage => _tab == "package";
    public bool IsDeployment => _tab == "deployment";

    public bool HasInstall => !string.IsNullOrWhiteSpace(Detail.InstallCommand);
    public bool HasUninstall => !string.IsNullOrWhiteSpace(Detail.UninstallCommand);

    public ObservableCollection<DetectionRuleDisplay> DetectionDisplays { get; } = new();
    public bool HasDetectionRules => Detail.DetectionRules.Count > 0;
    public bool HasAssignments => Detail.AssignedGroups.Count > 0;
    public string DetectionRulesHint => Detail.DetectionRules.Count switch
    {
        0 => "No rules",
        1 => "1 rule",
        var n => $"{n} rules · all must match",
    };

    // ── Package source (network share) ──
    private string? _sourcePath;
    public string? SourcePath
    {
        get => _sourcePath;
        private set
        {
            if (!Set(ref _sourcePath, value)) return;
            OnPropertyChanged(nameof(HasSource));
            OnPropertyChanged(nameof(SourcePathDisplay));
            OnPropertyChanged(nameof(SourceHintText));
            OnPropertyChanged(nameof(SourceHintOk));
            OnPropertyChanged(nameof(CanUpdatePackage));
            OnPropertyChanged(nameof(CanRepublish));
        }
    }
    public bool HasSource => !string.IsNullOrEmpty(_sourcePath);
    public string SourcePathDisplay => _sourcePath ?? "Package not found on the configured share";
    public string SourceHintText => HasSource
        ? "Package path validated — ready for updates"
        : "No matching folder on the share — check the Intune Applications path in Settings";
    public bool SourceHintOk => HasSource;

    /// <summary>Path to the PSADT script in the source package, when found.</summary>
    public string? SourceScriptPath { get; private set; }

    public ObservableCollection<SourceCheck> SourceChecks { get; } = new();

    // ── Deployment status (the bar's star columns are bound to these counts) ──
    public bool HasSummary => Detail.Statistics is { TotalDevices: > 0 };
    public string TargetedDevicesText => (Detail.Statistics?.TotalDevices ?? 0).ToString("N0");
    public int SumInstalled => Detail.Statistics?.SuccessfulInstalls ?? 0;
    public int SumPending => Detail.Statistics?.PendingInstalls ?? 0;
    public int SumFailed => Detail.Statistics?.FailedInstalls ?? 0;
    public int SumNotInstalled => Detail.Statistics?.NotInstalled ?? 0;
    public int SumNotApplicable => Detail.Statistics?.NotApplicable ?? 0;
    public int SumRemaining => SumNotInstalled + SumNotApplicable;

    /// <summary>Shown instead of the counts when there are none to show.</summary>
    public string StatsEmptyText =>
        IsLoading ? "Loading deployment status…"
        : Detail.Statistics != null ? "No devices have reported for this app yet."
        : "Deployment status has not been loaded.";

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value, [nameof(StatsEmptyText)]); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = "";
        try
        {
            Detail = await _apps.GetApplicationDetailAsync(Detail.Id);
            _loadedAt = DateTime.Now;
            OnPropertyChanged(nameof(LastSyncedText));
            RebuildDetection();
            RaiseDerived();
            await LocateSourceAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load full details: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> DeleteAsync()
    {
        try
        {
            await _apps.DeleteApplicationAsync(Detail.Id);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not retire app: {ex.Message}";
            return false;
        }
    }

    // ── Package content update (regenerate .intunewin, keep the app's metadata) ──

    /// <summary>The share folder has to be resolved before the package can be rebuilt.</summary>
    public bool CanUpdatePackage => HasSource && !IsUpdating;

    /// <summary>Republish also needs a session: the new content is uploaded to Intune.</summary>
    public bool CanRepublish => CanUpdatePackage && IsSignedIn;

    private CancellationTokenSource? _updateCts;

    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (!Set(ref _isUpdating, value)) return;
            OnPropertyChanged(nameof(CanUpdatePackage));
            OnPropertyChanged(nameof(CanRepublish));
        }
    }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    private int _updatePercent;
    public int UpdatePercent { get => _updatePercent; private set => Set(ref _updatePercent, value); }

    public void CancelUpdate() => _updateCts?.Cancel();

    /// <summary>
    /// Rebuilds the .intunewin from the package on the share and publishes it as a new
    /// content version of this app. Display name, description, install commands, detection
    /// rules, requirements, return codes, icon and assignments keep their tenant values —
    /// only the payload devices download changes.
    /// </summary>
    public async Task<bool> UpdatePackageContentAsync()
    {
        if (IsUpdating) return false;

        var packagePath = SourcePath;
        if (string.IsNullOrEmpty(packagePath))
        {
            StatusText = "No package folder on the share — check the Intune Applications path in Settings.";
            return false;
        }

        var settings = AppServices.Settings.Settings;
        NativeCodeSigner? signer = null;
        if (settings.CodeSigning.Enabled)
            signer = new NativeCodeSigner(settings.CodeSigning.CertificateThumbprint, settings.CodeSigning.TimestampServer);

        StatusText = "";
        UpdateStatus = "Starting…";
        UpdatePercent = 0;
        IsUpdating = true;

        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;

        try
        {
            var uploadService = new IntuneUploadService(
                AppServices.Auth.CreateSessionTokenProvider(), signer, settings.NetworkPaths.IntuneWinAppUtil);

            var progress = new DetailProgress(this);
            var appId = Detail.Id;
            var appName = Detail.DisplayName;
            var version = Detail.Version;

            await Task.Run(() => uploadService.UpdatePackageContentAsync(
                appId, appName, packagePath, version, progress, token), token);

            IsUpdating = false;

            // Size and last-modified come from Graph, and the checklist compares against them.
            await LoadAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Package update cancelled. Check the committed content version in Intune before retrying; the final request may already have completed.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not update the package: {ex.Message}";
            return false;
        }
        finally
        {
            IsUpdating = false;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    /// <summary>Marshals upload progress back onto the UI thread.</summary>
    private sealed class DetailProgress : IUploadProgress
    {
        private readonly ApplicationDetailViewModel _vm;
        public DetailProgress(ApplicationDetailViewModel vm) => _vm = vm;

        public void UpdateProgress(int percentage, string message)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Apply(percentage, message);
                return;
            }
            dispatcher.Invoke(() => Apply(percentage, message));
        }

        private void Apply(int percentage, string message)
        {
            _vm.UpdatePercent = percentage;
            _vm.UpdateStatus = message;
        }
    }

    /// <summary>Opens the app's blade in the Intune admin center. The Applications list uses it too.</summary>
    public static void OpenInIntune(string appId) =>
        Process.Start(new ProcessStartInfo($"https://intune.microsoft.com/#view/Microsoft_Intune_Apps/SettingsMenu/~/0/appId/{appId}") { UseShellExecute = true });

    public void OpenInIntune()
    {
        try { OpenInIntune(Detail.Id); }
        catch (Exception ex) { StatusText = $"Could not open browser: {ex.Message}"; }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasDetectionRules));
        OnPropertyChanged(nameof(HasAssignments));
        OnPropertyChanged(nameof(DetectionRulesHint));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(TargetedDevicesText));
        OnPropertyChanged(nameof(SumInstalled));
        OnPropertyChanged(nameof(SumPending));
        OnPropertyChanged(nameof(SumFailed));
        OnPropertyChanged(nameof(SumNotInstalled));
        OnPropertyChanged(nameof(SumNotApplicable));
    }

    private void RebuildDetection()
    {
        DetectionDisplays.Clear();
        foreach (var r in Detail.DetectionRules)
            DetectionDisplays.Add(DetectionRuleDisplay.From(r));
    }

    // ── Detection rule editing (the whole array is PATCHed on save) ──

    public DetectionRuleDisplay AddDetectionRule(DetectionRuleType type)
    {
        var rule = new DetectionRule { Type = type, DetectionType = "exists" };
        Detail.DetectionRules.Add(rule);
        var display = DetectionRuleDisplay.From(rule, isNew: true);
        DetectionDisplays.Add(display);
        display.BeginEdit();
        OnPropertyChanged(nameof(HasDetectionRules));
        OnPropertyChanged(nameof(DetectionRulesHint));
        return display;
    }

    /// <summary>Cancelling a never-saved rule removes it.</summary>
    public void DiscardNewRule(DetectionRuleDisplay display)
    {
        Detail.DetectionRules.Remove(display.Rule);
        DetectionDisplays.Remove(display);
        OnPropertyChanged(nameof(HasDetectionRules));
        OnPropertyChanged(nameof(DetectionRulesHint));
    }

    public async Task<bool> SaveDetectionRulesAsync()
    {
        try
        {
            await _apps.UpdateDetectionRulesAsync(Detail.Id, Detail.DetectionRules);
            StatusText = "";
            RebuildDetection();
            OnPropertyChanged(nameof(HasDetectionRules));
            OnPropertyChanged(nameof(DetectionRulesHint));
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save detection rules: {ex.Message}";
            return false;
        }
    }

    public async Task DeleteDetectionRuleAsync(DetectionRuleDisplay display)
    {
        Detail.DetectionRules.Remove(display.Rule);
        if (!await SaveDetectionRulesAsync())
        {
            // PATCH failed; put the rule back so the UI matches Intune.
            Detail.DetectionRules.Add(display.Rule);
            RebuildDetection();
        }
    }

    // ── Assignment editing ──

    public string[] IntentChoices { get; } = { "Required", "Available", "Uninstall" };

    private string _selectedIntent = "Required";
    public string SelectedIntent { get => _selectedIntent; set => Set(ref _selectedIntent, value); }

    private readonly IncrementalSearch<EntraGroup> _groupSearchBox;
    private readonly IncrementalSearch<GroupMember> _memberSearchBox;

    private string _groupSearch = "";
    public string GroupSearch
    {
        get => _groupSearch;
        set
        {
            if (!Set(ref _groupSearch, value)) return;
            _selectedGroup = null;
            OnPropertyChanged(nameof(CanAddAssignment));
            ErrorReporter.FireAndForget(() => _groupSearchBox.RunAsync(value));
        }
    }

    public ObservableCollection<EntraGroup> GroupResults => _groupSearchBox.Results;
    public bool HasGroupResults => _groupSearchBox.HasResults;

    private EntraGroup? _selectedGroup;
    public bool CanAddAssignment => _selectedGroup != null && IsSignedIn;

    public void SelectGroupResult(EntraGroup group)
    {
        _selectedGroup = group;
        _groupSearch = group.DisplayName;     // field write: don't retrigger the search
        OnPropertyChanged(nameof(GroupSearch));
        OnPropertyChanged(nameof(CanAddAssignment));
        _groupSearchBox.Clear();
    }

    public async Task AddAssignmentAsync()
    {
        if (_selectedGroup == null) return;
        try
        {
            await _apps.AddAssignmentAsync(Detail.Id, _selectedGroup.Id, SelectedIntent.ToLowerInvariant());
            _selectedGroup = null;
            _groupSearch = "";
            OnPropertyChanged(nameof(GroupSearch));
            OnPropertyChanged(nameof(CanAddAssignment));
            await RefreshAssignmentsAsync();
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not add assignment: {ex.Message}";
        }
    }

    public async Task RemoveAssignmentAsync(AssignedGroup group)
    {
        if (string.IsNullOrEmpty(group.AssignmentId))
        {
            StatusText = "This assignment has no id — refresh and try again.";
            return;
        }
        try
        {
            await _apps.RemoveAssignmentAsync(Detail.Id, group.AssignmentId);
            await RefreshAssignmentsAsync();
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not remove assignment: {ex.Message}";
        }
    }

    private async Task RefreshAssignmentsAsync()
    {
        Detail.AssignedGroups = await _apps.GetAssignedGroupsAsync(Detail.Id);
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasAssignments));
    }

    // ── Group members slide-over ──

    private AssignedGroup? _flyoutGroup;
    public AssignedGroup? FlyoutGroup { get => _flyoutGroup; private set { _flyoutGroup = value; OnPropertyChanged(nameof(FlyoutGroup)); } }

    private bool _isFlyoutOpen;
    public bool IsFlyoutOpen { get => _isFlyoutOpen; private set => Set(ref _isFlyoutOpen, value); }

    public ObservableCollection<GroupMember> Members { get; } = new();

    private string _membersStatus = "";
    public string MembersStatus { get => _membersStatus; private set => Set(ref _membersStatus, value); }

    /// <summary>True for real groups; All Devices/Users have no member list.</summary>
    public bool FlyoutHasGroup => !string.IsNullOrEmpty(_flyoutGroup?.GroupId);

    public async Task OpenMembersAsync(AssignedGroup group)
    {
        FlyoutGroup = group;
        OnPropertyChanged(nameof(FlyoutHasGroup));
        Members.Clear();
        _memberSearchBox.Clear();
        _memberSearch = "";
        OnPropertyChanged(nameof(MemberSearch));
        IsFlyoutOpen = true;

        if (!FlyoutHasGroup)
        {
            MembersStatus = "Built-in assignment target — membership is implicit.";
            return;
        }

        MembersStatus = "Loading members…";
        try
        {
            var members = await _apps.GetGroupMembersAsync(group.GroupId);
            if (!ReferenceEquals(_flyoutGroup, group)) return;   // flyout switched meanwhile
            Members.Clear();
            foreach (var m in members) Members.Add(m);
            MembersStatus = $"{members.Count} member{(members.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            MembersStatus = $"Could not load members: {ex.Message}";
        }
    }

    public void CloseFlyout() => IsFlyoutOpen = false;

    private string _memberSearch = "";
    public string MemberSearch
    {
        get => _memberSearch;
        set
        {
            if (!Set(ref _memberSearch, value)) return;
            ErrorReporter.FireAndForget(() => _memberSearchBox.RunAsync(value));
        }
    }

    public ObservableCollection<GroupMember> MemberSearchResults => _memberSearchBox.Results;
    public bool HasMemberResults => _memberSearchBox.HasResults;

    public async Task AddMemberAsync(GroupMember member)
    {
        var group = _flyoutGroup;
        if (group == null || string.IsNullOrEmpty(group.GroupId)) return;
        try
        {
            await _apps.AddGroupMemberAsync(group.GroupId, member.Id);
            _memberSearch = "";
            OnPropertyChanged(nameof(MemberSearch));
            _memberSearchBox.Clear();
            await OpenMembersAsync(group);
        }
        catch (Exception ex)
        {
            MembersStatus = ex is GraphException { IsForbidden: true }
                ? "No permission to change membership — the signed-in account needs GroupMember.ReadWrite.All."
                : $"Could not add member: {ex.Message}";
        }
    }

    public async Task RemoveMemberAsync(GroupMember member)
    {
        var group = _flyoutGroup;
        if (group == null || string.IsNullOrEmpty(group.GroupId)) return;
        try
        {
            await _apps.RemoveGroupMemberAsync(group.GroupId, member.Id);
            await OpenMembersAsync(group);
        }
        catch (Exception ex)
        {
            MembersStatus = ex is GraphException { IsForbidden: true }
                ? "No permission to change membership — the signed-in account needs GroupMember.ReadWrite.All."
                : $"Could not remove member: {ex.Message}";
        }
    }

    /// <summary>
    /// Finds the package's version folder on the share and runs the integrity checks.
    /// Enumeration runs off the UI thread; failure leaves "not found".
    /// </summary>
    private async Task LocateSourceAsync()
    {
        var root = AppServices.Settings.Settings.NetworkPaths.IntuneApplications;
        var d = Detail;

        var (path, script, checks) = await Task.Run(() =>
        {
            var p = PackageSourceLocator.Locate(root, d.Publisher, d.DisplayName, d.Version);
            return p == null
                ? ((string?)null, (string?)null, new List<SourceCheck>())
                : (p, FindScript(p), BuildChecks(p, d.Size, d.SizeFormatted));
        });

        SourceScriptPath = script;
        SourceChecks.Clear();
        foreach (var c in checks) SourceChecks.Add(c);
        SourcePath = path;
    }

    private static string? FindScript(string packagePath) =>
        FolderBrowserHelper.GetPSADTScriptPath(Path.Combine(packagePath, "Application"))
        ?? FolderBrowserHelper.GetPSADTScriptPath(packagePath);

    private static List<SourceCheck> BuildChecks(string packagePath, long intuneSize, string intuneSizeText)
    {
        var checks = new List<SourceCheck>();

        var script = FindScript(packagePath);
        checks.Add(script != null
            ? new SourceCheck(Path.GetFileName(script), "deployment script present", ok: true)
            : new SourceCheck("Invoke-AppDeployToolkit.ps1", "deployment script not found", ok: false));

        var intuneDir = Path.Combine(packagePath, "Intune");
        var intunewin = Directory.Exists(intuneDir) ? Directory.GetFiles(intuneDir, "*.intunewin").FirstOrDefault() : null;
        if (intunewin == null)
        {
            checks.Add(new SourceCheck("*.intunewin", "package file not found", ok: false));
        }
        else
        {
            var size = new FileInfo(intunewin).Length;
            // Graph reports the committed upload, so allow for encryption overhead.
            var matches = intuneSize <= 0 || Math.Abs(size - intuneSize) <= intuneSize * 0.1;
            checks.Add(new SourceCheck(Path.GetFileName(intunewin), matches
                ? $"{ByteSize.Format(size)} — matches the Intune upload"
                : $"{ByteSize.Format(size)} on share vs {intuneSizeText} in Intune — re-upload?", ok: matches));
        }

        checks.Add(File.Exists(Path.Combine(intuneDir, "detection.xml"))
            ? new SourceCheck("detection.xml", "detection definition present", ok: true)
            : new SourceCheck("detection.xml", "not found", ok: false));

        var iconDir = Path.Combine(packagePath, "Icon");
        var hasIcon = Directory.Exists(iconDir) && Directory.EnumerateFiles(iconDir).Any();
        checks.Add(new SourceCheck(hasIcon ? @"Icon\" + Path.GetFileName(Directory.EnumerateFiles(iconDir).First()) : @"Icon\",
            hasIcon ? "icon present" : "no icon file", ok: hasIcon));

        return checks;
    }


}

/// <summary>One row of the Package tab's integrity checklist.</summary>
public sealed class SourceCheck
{
    public SourceCheck(string file, string note, bool ok)
    {
        File = file;
        Note = note;
        IsOk = ok;
    }
    public string File { get; }
    public string Note { get; }
    public bool IsOk { get; }
}

/// <summary>One choice in a detection editor dropdown: display label + Graph value.</summary>
public sealed record Option(string Label, string Value);

/// <summary>
/// A detection rule as a "type tag + summary" row with inline edit state. ApplyEdit
/// writes back to the rule; the view model PATCHes the whole array afterwards.
/// </summary>
public sealed class DetectionRuleDisplay : ObservableObject
{
    private static readonly Option[] Operators =
    {
        new("=", "equal"), new("≠", "notEqual"), new(">", "greaterThan"),
        new("≥", "greaterThanOrEqual"), new("<", "lessThan"), new("≤", "lessThanOrEqual"),
    };
    private static readonly Option[] FileTypes =
    {
        new("Exists", "exists"), new("Does not exist", "doesNotExist"), new("Version", "version"),
        new("Size (MB)", "sizeInMB"), new("Modified date", "modifiedDate"), new("Created date", "createdDate"),
    };
    private static readonly Option[] RegistryTypes =
    {
        new("Exists", "exists"), new("Does not exist", "doesNotExist"), new("String", "string"),
        new("Integer", "integer"), new("Version", "version"),
    };

    public DetectionRule Rule { get; private init; } = null!;
    public string TypeTag { get; private init; } = "";
    public bool IsNew { get; private set; }

    public string Summary => Compose(Rule);

    public bool IsMsi => Rule.Type == DetectionRuleType.MSI;
    public bool IsFile => Rule.Type == DetectionRuleType.File;
    public bool IsRegistry => Rule.Type == DetectionRuleType.Registry;
    public bool IsScript => Rule.Type == DetectionRuleType.Script;
    public bool IsFileOrRegistry => IsFile || IsRegistry;
    /// <summary>Script rules carry base64 PowerShell, edited as a file rather than fields.</summary>
    public bool CanEdit => !IsScript;

    public IReadOnlyList<Option> OperatorChoices => Operators;
    public IReadOnlyList<Option> DetectionTypeChoices => IsRegistry ? RegistryTypes : FileTypes;

    private bool _isEditing;
    public bool IsEditing { get => _isEditing; private set => Set(ref _isEditing, value); }

    private string _editPath = "";
    public string EditPath { get => _editPath; set => Set(ref _editPath, value); }

    private string _editName = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    private bool _editCheckVersion;
    public bool EditCheckVersion
    {
        get => _editCheckVersion;
        set { if (Set(ref _editCheckVersion, value)) OnPropertyChanged(nameof(EditNeedsValue)); }
    }

    private string _editDetectionType = "exists";
    public string EditDetectionType
    {
        get => _editDetectionType;
        set { if (Set(ref _editDetectionType, value)) OnPropertyChanged(nameof(EditNeedsValue)); }
    }

    /// <summary>Whether the operator + value inputs apply to the current edit state.</summary>
    public bool EditNeedsValue => IsMsi
        ? EditCheckVersion
        : _editDetectionType is "version" or "string" or "integer" or "sizeInMB" or "modifiedDate" or "createdDate";

    private string _editOperator = "equal";
    public string EditOperator { get => _editOperator; set => Set(ref _editOperator, value); }

    private string _editValue = "";
    public string EditValue { get => _editValue; set => Set(ref _editValue, value); }

    public void BeginEdit()
    {
        EditPath = Rule.Path;
        EditName = IsMsi ? "" : Rule.FileOrFolderName;
        EditCheckVersion = Rule.CheckVersion;
        EditDetectionType = string.IsNullOrEmpty(Rule.DetectionType) ? "exists" : Rule.DetectionType;
        EditOperator = string.IsNullOrEmpty(Rule.Operator) || Rule.Operator == "notConfigured" ? "equal" : Rule.Operator;
        EditValue = IsMsi ? Rule.FileOrFolderName : Rule.DetectionValue;
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    public void ApplyEdit()
    {
        Rule.Path = EditPath.Trim();
        if (IsMsi)
        {
            Rule.CheckVersion = EditCheckVersion;
            Rule.FileOrFolderName = EditCheckVersion ? EditValue.Trim() : "";
            Rule.Operator = EditCheckVersion ? EditOperator : "";
        }
        else if (IsFile || IsRegistry)
        {
            Rule.FileOrFolderName = EditName.Trim();
            Rule.DetectionType = EditDetectionType;
            Rule.CheckVersion = IsFile && EditDetectionType == "version";
            Rule.Operator = EditNeedsValue ? EditOperator : "";
            Rule.DetectionValue = EditNeedsValue ? EditValue.Trim() : "";
        }
        IsEditing = false;
        IsNew = false;
        OnPropertyChanged(nameof(Summary));
    }

    public static DetectionRuleDisplay From(DetectionRule r, bool isNew = false) => new()
    {
        Rule = r,
        IsNew = isNew,
        TypeTag = r.Type switch
        {
            DetectionRuleType.MSI => "MSI",
            DetectionRuleType.File => "FILE",
            DetectionRuleType.Registry => "REG",
            DetectionRuleType.Script => "PS1",
            _ => r.Type.ToString().ToUpperInvariant(),
        },
    };

    private static string Compose(DetectionRule r) => r.Type switch
    {
        DetectionRuleType.MSI => r.CheckVersion
            ? $"{Dash(r.Path)} · version {OperatorSymbol(r.Operator)} {r.FileOrFolderName}"
            : $"{Dash(r.Path)} · product code present",
        DetectionRuleType.File => $@"{Dash(r.Path)}\{r.FileOrFolderName} · {DetectionSummary(r)}",
        DetectionRuleType.Registry => $"{Dash(r.Path)} · {Dash(r.FileOrFolderName)} · {DetectionSummary(r)}",
        DetectionRuleType.Script => "PowerShell detection script",
        _ => "",
    };

    private static string DetectionSummary(DetectionRule r) => r.DetectionType switch
    {
        "exists" => "exists",
        "doesNotExist" => "does not exist",
        "version" => $"version {OperatorSymbol(r.Operator)} {r.DetectionValue}",
        "string" => $"string {OperatorSymbol(r.Operator)} \"{r.DetectionValue}\"",
        "integer" => $"integer {OperatorSymbol(r.Operator)} {r.DetectionValue}",
        "sizeInMB" => $"size {OperatorSymbol(r.Operator)} {r.DetectionValue} MB",
        "modifiedDate" => $"modified {OperatorSymbol(r.Operator)} {r.DetectionValue}",
        "createdDate" => $"created {OperatorSymbol(r.Operator)} {r.DetectionValue}",
        _ => string.IsNullOrEmpty(r.DetectionType) ? "exists" : r.DetectionType,
    };

    private static string OperatorSymbol(string op) => op switch
    {
        "greaterThanOrEqual" => ">=",
        "greaterThan" => ">",
        "equal" => "=",
        "notEqual" => "!=",
        "lessThan" => "<",
        "lessThanOrEqual" => "<=",
        _ => string.IsNullOrEmpty(op) ? "=" : op,
    };

    private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
}
