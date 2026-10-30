using System.IO;

namespace Packman.Services;

public partial class IntuneUploadService
{
    /// <summary>
    /// Rebuilds the .intunewin from the package folder and publishes it as a new content
    /// version of an app that already exists in Intune. Nothing else on the app is touched:
    /// name, description, install commands, detection rules, requirements, return codes,
    /// icon and assignments stay exactly as the tenant has them.
    /// </summary>
    /// <returns>The committed content version id.</returns>
    public async Task<string> UpdatePackageContentAsync(
        string appId,
        string appName,
        string packagePath,
        string? version = null,
        IUploadProgress? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("No Intune app id to update.", nameof(appId));
        if (!Directory.Exists(packagePath))
            throw new DirectoryNotFoundException($"Package folder not found: {packagePath}");

        using var log = new UploadLogger(string.IsNullOrWhiteSpace(appName) ? appId : appName, "Update");
        LinkWarning = null;

        try
        {
            log.Section("UPDATE METADATA");
            log.LogMetadata("Application Name", appName);
            log.LogMetadata("Version", version ?? "");
            log.LogMetadata("Intune App Id", appId);
            log.LogMetadata("Package Path", packagePath);
            log.Info("Only the package content is replaced; app metadata is left untouched.");
            log.Info($"Update log file: {log.LogFilePath}");

            log.Section("UPDATE PROCESS");

            Report(progress, log, 5, "Authenticating with Microsoft Graph...");
            _ = await _graph.GetTokenAsync();
            log.Success("Authentication successful");

            var intuneWin = await BuildIntuneWinAsync(packagePath, progress, log, ct);

            ct.ThrowIfCancellationRequested();

            var contentVersionId = await PublishContentAsync(appId, intuneWin, progress, log, ct);

            Report(progress, log, 100, "Update complete!");
            log.Section("UPDATE COMPLETE");
            log.Success($"Package content for '{appName}' updated. App ID: {appId}");

            WriteLink(packagePath, appId, log);

            return contentVersionId;
        }
        catch (OperationCanceledException)
        {
            // The old content version stays committed, so a cancelled update is a no-op
            // for devices; the orphaned version is harmless and ages out with the app.
            log.Section("UPDATE CANCELLED");
            throw;
        }
        catch (Exception ex)
        {
            log.Section("UPDATE FAILED");
            log.Error("Update process failed", ex);
            throw;
        }
    }
}
