using Packman.Helpers;

namespace Packman.Services;

/// <summary>
/// Publishing creates a new Intune app every time. A package folder that is already linked
/// to an app in this tenant would end up as two apps, so the packager is asked first.
/// </summary>
public static class PublishGuard
{
    /// <summary>
    /// True to go ahead: the folder has no link to a live app in the connected tenant, or the
    /// packager chose to publish it as a second app.
    /// </summary>
    public static async Task<bool> ConfirmNewAppAsync(string packagePath, IntuneAuthService auth,
        Func<string, Task<bool>> appExists, IDialogService dialogs)
    {
        var link = PackageMarker.Read(packagePath);
        if (link == null || link.IsLegacy) return true;
        if (!string.Equals(link.TenantId, auth.TenantId, StringComparison.OrdinalIgnoreCase)) return true;
        if (!await appExists(link.AppId)) return true;

        var name = string.IsNullOrWhiteSpace(link.DisplayName) ? link.AppId : link.DisplayName;
        var when = DateTimeOffset.TryParse(link.UploadedAt, out var at) ? $" on {at.LocalDateTime:g}" : "";
        return dialogs.Confirm(
            $"This package was already published as \"{name}\"{when}.\n\n" +
            "Publishing again creates a second Intune app. To update the existing app, open it under " +
            "Applications and use Republish content.\n\nPublish a second app anyway?",
            "Package already published", "Publish a second app", "Cancel");
    }
}
