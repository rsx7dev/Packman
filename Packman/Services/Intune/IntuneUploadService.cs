using Packman.Helpers;
using Packman.Models;
using System.IO;

namespace Packman.Services;

/// <summary>Intune reported a terminal upload state. Never retried.</summary>
public sealed class UploadStateException : Exception
{
    public string State { get; }
    public UploadStateException(string stage, string state)
        : base($"Intune reported '{state}' while waiting for {stage}. Check the app in the Intune admin center.")
        => State = state;
}

/// <summary>
/// Uploads a Win32 (PSADT) package to Intune via Graph: sign, build the .intunewin,
/// register the app, push the encrypted payload to Azure Storage, commit, publish, assign.
/// The same build and publish steps also replace the content of an app that already exists.
/// </summary>
public partial class IntuneUploadService
{
    private readonly GraphClient _graph;
    private readonly string _tenantId;
    private readonly IFileSigner? _signer;
    private readonly string _converterPath;

    /// <summary>Whether the half-built app was removed after a failure or cancel. Null when nothing had been created.</summary>
    public bool? RollbackSucceeded { get; private set; }
    private readonly List<string> _followUpWarnings = [];

    /// <summary>Why packman.id could not be written after a successful upload; null when it was.</summary>
    public string? LinkWarning { get; private set; }

    public IntuneUploadService(Func<Task<string>> tokenProvider, string tenantId, IFileSigner? signer, string converterPath)
    {
        _graph = new GraphClient(tokenProvider);
        _tenantId = tenantId;
        _signer = signer;
        _converterPath = converterPath;
    }

    public async Task<string> UploadWin32ApplicationAsync(
        ApplicationInfo appInfo,
        string packagePath,
        List<DetectionRule> detectionRules,
        string installCommand,
        string uninstallCommand,
        string description,
        string installContext,
        string? iconPath = null,
        IUploadProgress? progress = null,
        string? predecessorAppId = null,
        AppSettings.GroupAssignmentConfig? groupAssignment = null,
        RequirementInfo? requirements = null,
        List<ReturnCodeInfo>? returnCodes = null,
        string? privacyUrl = null,
        string? informationUrl = null,
        IEnumerable<AssignedGroup>? pickedGroups = null,
        string? restartBehavior = null,
        int? maxRunTimeMinutes = null,
        CancellationToken ct = default)
    {
        using var log = new UploadLogger(appInfo.Name, console: progress);
        string? createdAppId = null;
        string? committedAppId = null;
        RollbackSucceeded = null;
        LinkWarning = null;
        _followUpWarnings.Clear();

        try
        {
            log.Section("UPLOAD METADATA");
            log.LogMetadata("Application Name", appInfo.Name);
            log.LogMetadata("Manufacturer", appInfo.Manufacturer);
            log.LogMetadata("Version", appInfo.Version);
            log.LogMetadata("Package Path", packagePath);
            log.LogMetadata("Install Command", installCommand);
            log.LogMetadata("Uninstall Command", uninstallCommand);
            log.LogMetadata("Install Context", installContext);
            log.LogMetadata("Architectures", AllowedArchitectures(appInfo.Architecture));
            log.LogMetadata("Restart Behavior", RestartBehavior(restartBehavior));
            log.LogMetadata("Max Run Time", $"{maxRunTimeMinutes ?? AppSettings.IntuneDefaultsConfig.DefaultMaxRunTimeMinutes} min");
            log.LogMetadata("Detection Rules", $"{detectionRules.Count} rule(s)");
            log.Info($"Upload log file: {log.LogFilePath}");

            log.Section("UPLOAD PROCESS");

            Report(progress, log, 5, "Authenticating with Microsoft Graph...");
            _ = await _graph.GetTokenAsync();
            log.Success("Authentication successful");

            var intuneWin = await BuildIntuneWinAsync(packagePath, progress, log, ct);

            ct.ThrowIfCancellationRequested();

            Report(progress, log, 35, "Registering application in Intune...");
            var appId = await CreateWin32LobAppAsync(appInfo, installCommand, uninstallCommand, description, detectionRules,
                installContext, intuneWin, iconPath, requirements, returnCodes, privacyUrl, informationUrl,
                restartBehavior, maxRunTimeMinutes, ct);
            createdAppId = appId;
            log.Success($"Application registered with ID: {appId}");

            await PublishContentAsync(appId, intuneWin, progress, log, ct);

            // Published: a later supersedence or assignment failure must not roll it back.
            createdAppId = null;
            committedAppId = appId;

            WriteLink(packagePath, appId, appInfo.DisplayName, appInfo.Version, intuneWin, log);
            Report(progress, log, 97, "Waiting for Intune to finish publishing...");
            await WaitForApplicationPublishedAsync(appId, ct);
            log.Success("Intune reports the app as published");

            if (!string.IsNullOrEmpty(predecessorAppId))
                await WriteSupersedenceAsync(appId, predecessorAppId, log, ct);

            var picked = pickedGroups?.Where(g => !string.IsNullOrWhiteSpace(g.GroupId)).ToList() ?? new List<AssignedGroup>();
            if (picked.Count > 0 || (groupAssignment?.HasAnyAssignment() ?? false))
            {
                Report(progress, log, 98, "Assigning groups...");
                log.Section("GROUP ASSIGNMENT");
                await AssignGroupsAsync(appId, appInfo, groupAssignment ?? new AppSettings.GroupAssignmentConfig(), picked, log, ct);
            }

            if (_followUpWarnings.Count > 0)
                throw new IntuneFollowUpException(appId, string.Join(Environment.NewLine, _followUpWarnings));

            Report(progress, log, 100, "Upload complete!");
            log.Section("UPLOAD COMPLETE");
            log.Success($"Application '{appInfo.Name}' uploaded successfully! ID: {appId}");
            return appId;
        }
        catch (IntuneFollowUpException) { throw; }
        catch (Exception ex) when (committedAppId != null)
        {
            var message = ex is OperationCanceledException
                ? "Follow-up was cancelled after Intune accepted the content. Publishing, assignments or supersedence may still need attention."
                : $"Intune accepted the content, but follow-up did not finish: {ex.Message}";
            log.Warning(message);
            throw new IntuneFollowUpException(committedAppId, message, ex);
        }
        catch (OperationCanceledException)
        {
            log.Section("UPLOAD CANCELLED");
            await RollbackCreatedAppAsync(createdAppId, log);
            throw;
        }
        catch (Exception ex)
        {
            log.Section("UPLOAD FAILED");
            log.Error("Upload process failed", ex);
            await RollbackCreatedAppAsync(createdAppId, log);
            throw;
        }
    }

    private async Task WaitForApplicationPublishedAsync(string appId, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(10);
        var deadline = DateTime.UtcNow + timeout;
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                deadlineCts.Token.ThrowIfCancellationRequested();
                var response = await _graph.GetAsync($"{GraphClient.MobileApps}/{appId}?$select=publishingState", "Check publishing state", deadlineCts.Token);
                if (response.Json.GetSafeString("publishingState") == "published") return;
                await Task.Delay(TimeSpan.FromSeconds(5), deadlineCts.Token);
            }
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !ct.IsCancellationRequested) { }
        throw new TimeoutException("Intune has not confirmed the app is published. Check its publishing state before assigning it.");
    }

    /// <summary>Links the package folder to the app through Intune\packman.id. A failed write never fails the upload.</summary>
    private void WriteLink(string packagePath, string appId, string displayName, string displayVersion, IntuneWinInfo intuneWin, UploadLogger log)
    {
        log.Info(@"Writing Intune\packman.id...");
        LinkWarning = PackageMarker.Save(packagePath, new PackageMarker.Link
        {
            AppId = appId,
            TenantId = _tenantId,
            UploadedAt = DateTime.UtcNow.ToString("o"),
            DisplayName = displayName,
            DisplayVersion = displayVersion,
            IntunewinFileName = Path.GetFileName(intuneWin.IntuneWinPath),
            UnencryptedSize = intuneWin.UnencryptedContentSize,
        });
        if (LinkWarning == null)
            log.Success($@"Wrote Intune\packman.id: the package folder is linked to app {appId}");
        else
            log.Warning(LinkWarning);
    }

    private void RecordFollowUpWarning(string message, UploadLogger log)
    {
        _followUpWarnings.Add(message);
        log.Warning(message);
    }

    private static void Report(IUploadProgress? progress, UploadLogger log, int pct, string message)
    {
        progress?.UpdateProgress(pct, message);
        log.Progress(pct, message);
    }

    /// <summary>Signs the script, runs IntuneWinAppUtil and reads the result. Progress 10-30.</summary>
    private async Task<IntuneWinInfo> BuildIntuneWinAsync(string packagePath, IUploadProgress? progress, UploadLogger log, CancellationToken ct)
    {
        Report(progress, log, 5, "Checking PSADT runtime and deployment script...");
        PackagePreflight.EnsureReady(packagePath);
        Report(progress, log, 10, "Signing application files...");
        await SignApplicationFilesAsync(packagePath, progress, log, ct);

        Report(progress, log, 20, "Packaging application files...");
        var intuneWinFile = await CreateIntuneWinFileAsync(packagePath, log, ct);
        log.Success($"Package created: {Path.GetFileName(intuneWinFile)}");

        Report(progress, log, 30, "Reading package metadata...");
        var intuneWin = ExtractIntuneWinInfo(intuneWinFile);
        log.Success($"Package metadata extracted - Size: {intuneWin.UnencryptedContentSize:N0} bytes");
        return intuneWin;
    }

    /// <summary>
    /// Pushes a built .intunewin into an app as a new content version and switches the app
    /// to it: content version, file entry, Azure upload, commit, publish. Nothing else on
    /// the app is touched. Progress 45-95. Returns the committed content version id.
    /// </summary>
    private async Task<string> PublishContentAsync(string appId, IntuneWinInfo intuneWin, IUploadProgress? progress, UploadLogger log, CancellationToken ct)
    {
        Report(progress, log, 45, "Preparing content storage...");
        var contentVersionId = await CreateContentVersionAsync(appId, ct);
        log.Success($"Content version created: {contentVersionId}");

        Report(progress, log, 50, "Initializing file upload...");
        var fileId = await CreateFileEntryAsync(appId, contentVersionId, intuneWin, ct);
        var fileUrl = FileUrl(appId, contentVersionId, fileId);
        log.Success($"File entry created: {fileId}");

        Report(progress, log, 55, "Requesting Azure upload URL...");
        var storage = await WaitForUploadStateAsync(fileUrl, "azureStorageUriRequest", TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(10), ct);
        var sasUri = storage.GetSafeString("azureStorageUri");
        if (string.IsNullOrEmpty(sasUri))
            throw new UploadStateException("the Azure Storage URI", "azureStorageUriRequestSuccess without a URI");
        log.Success($"Azure Storage upload URL obtained: {new Uri(sasUri).GetLeftPart(UriPartial.Path)}");

        await UploadToAzureStorageAsync(sasUri, fileUrl, intuneWin, progress, log, ct);
        log.Success("Package uploaded to Azure Storage successfully");

        Report(progress, log, 85, "Finalizing package upload...");
        await CommitFileAsync(fileUrl, intuneWin.EncryptionInfo, ct);
        log.Success("File committed successfully");

        Report(progress, log, 90, "Processing uploaded package...");
        await WaitForUploadStateAsync(fileUrl, "commitFile", TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5), ct);
        log.Success("File processing completed");

        // Switching the committed version is the only PATCH on the app: it carries just
        // committedContentVersion, so every other property keeps its tenant value.
        Report(progress, log, 95, "Publishing application...");
        await CommitAppAsync(appId, contentVersionId, ct);
        log.Success($"App now serves content version {contentVersionId}");
        return contentVersionId;
    }

    /// <summary>
    /// Drops the half-built app so a failed upload leaves no shell in the tenant. Best
    /// effort, and not cancellable: it runs after a cancel.
    /// </summary>
    private async Task RollbackCreatedAppAsync(string? appId, UploadLogger log)
    {
        if (string.IsNullOrEmpty(appId)) return;

        try
        {
            var response = await _graph.DeleteAsync($"{GraphClient.MobileApps}/{appId}", "Remove incomplete app", CancellationToken.None, throwOnError: false);
            RollbackSucceeded = response.IsSuccess;
            if (response.IsSuccess)
                log.Info($"Removed the incomplete app {appId} from Intune");
            else
                log.Warning($"Could not remove the incomplete app {appId} (HTTP {response.StatusCode}) - delete it in the Intune admin center");
        }
        catch (Exception ex)
        {
            RollbackSucceeded = false;
            log.Warning($"Could not remove the incomplete app {appId}: {ex.Message}");
        }
    }

    private async Task WriteSupersedenceAsync(string newAppId, string predecessorAppId, UploadLogger log, CancellationToken ct)
    {
        log.Info($"Writing supersedence: this app replaces {predecessorAppId}");
        var payload = new
        {
            relationships = new object[]
            {
                new Dictionary<string, string>
                {
                    ["@odata.type"] = "#microsoft.graph.mobileAppSupersedence",
                    ["targetId"] = predecessorAppId,
                    ["targetType"] = "child",
                    ["supersedenceType"] = "update",
                },
            },
        };

        try
        {
            var response = await _graph.PostAsync($"{GraphClient.MobileApps}/{newAppId}/updateRelationships", payload, "Write supersedence", ct, throwOnError: false);
            if (response.IsSuccess)
                log.Success($"Marked previous app {predecessorAppId} as superseded");
            else
                RecordFollowUpWarning($"Could not write supersedence (HTTP {response.StatusCode}): {GraphException.ExtractMessage(response.Body)}", log);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RecordFollowUpWarning($"Could not write supersedence: {ex.Message}", log);
        }
    }
}

/// <summary>What the upload needs to know about a built .intunewin.</summary>
public sealed class IntuneWinInfo
{
    public string IntuneWinPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long UnencryptedContentSize { get; init; }
    /// <summary>Name of the encrypted payload entry inside the archive.</summary>
    public string ContentEntryName { get; init; } = "";
    public long EncryptedContentSize { get; init; }
    public EncryptionInfo EncryptionInfo { get; init; } = new();
}

public sealed class EncryptionInfo
{
    public string EncryptionKey { get; init; } = "";
    public string MacKey { get; init; } = "";
    public string InitializationVector { get; init; } = "";
    public string Mac { get; init; } = "";
    public string ProfileIdentifier { get; init; } = "ProfileVersion1";
    public string FileDigest { get; init; } = "";
    public string FileDigestAlgorithm { get; init; } = "SHA256";
}
