using Packman.Helpers;
using Packman.Models;

namespace Packman.Services;

public partial class IntuneUploadService
{
    /// <summary>
    /// Assigns the published app to the groups picked for this upload plus the ones set on
    /// the Settings page. Follow-up failures are reported without rolling back committed content.
    /// </summary>
    private async Task AssignGroupsAsync(string appId, ApplicationInfo appInfo, AppSettings.GroupAssignmentConfig config,
                                         IEnumerable<AssignedGroup>? pickedGroups, UploadLogger log, CancellationToken ct)
    {
        foreach (var picked in pickedGroups ?? Enumerable.Empty<AssignedGroup>())
        {
            if (string.IsNullOrWhiteSpace(picked.GroupId)) continue;
            await CreateGroupAssignmentAsync(appId, picked.GroupId, ParseIntent(picked.AssignmentType), picked.GroupName, log, ct);
        }

        foreach (var existing in config.ExistingGroups)
        {
            if (string.IsNullOrWhiteSpace(existing.GroupName)) continue;
            var groupId = await ResolveGroupIdAsync(existing.GroupName, log, ct);
            if (groupId == null)
            {
                RecordFollowUpWarning($"Group '{existing.GroupName}' not found in Entra ID - skipped", log);
                continue;
            }
            await CreateGroupAssignmentAsync(appId, groupId, existing.Intent, existing.GroupName, log, ct);
        }

        if (config.CreateGroupPerPackage)
            await AssignPerPackageGroupAsync(appId, appInfo, config.GroupNameTemplate, config.NewGroupIntent, log, ct);

        if (config.CreateUninstallGroupPerPackage)
            await AssignPerPackageGroupAsync(appId, appInfo, config.UninstallGroupNameTemplate, AssignmentIntent.Uninstall, log, ct);
    }

    /// <summary>Resolves or creates the per-package group and assigns it.</summary>
    private async Task AssignPerPackageGroupAsync(string appId, ApplicationInfo appInfo, string template, AssignmentIntent intent, UploadLogger log, CancellationToken ct)
    {
        var name = GroupAssignmentNamer.Build(template, appInfo.Manufacturer, appInfo.Name, appInfo.Version);
        if (string.IsNullOrWhiteSpace(name))
        {
            RecordFollowUpWarning($"Per-package group name for the {intent} assignment resolved to empty - skipped", log);
            return;
        }
        // Reuse an existing group with this name before creating one.
        var groupId = await ResolveGroupIdAsync(name, log, ct) ?? await CreateSecurityGroupAsync(name, log, ct);
        if (groupId != null)
            await CreateGroupAssignmentAsync(appId, groupId, intent, name, log, ct);
    }

    private async Task<string?> ResolveGroupIdAsync(string displayName, UploadLogger log, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"displayName eq '{OData.Literal(displayName.Trim())}'");
        var response = await _graph.GetAsync($"{GraphClient.Groups}?$filter={filter}&$select=id&$top=2", "Group lookup", ct);
        var page = response.Json;
        if (page.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !page.TryGetProperty("value", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidOperationException($"Entra returned an invalid lookup result for group '{displayName}'. Check the group before retrying.");
        if (!string.IsNullOrEmpty(page.GetSafeString("@odata.nextLink")))
            throw new InvalidOperationException($"The lookup for group '{displayName}' was incomplete. Select the intended group by ID before assigning.");
        if (arr.GetArrayLength() > 1)
            throw new InvalidOperationException($"Multiple Entra groups are named '{displayName}'. Select the intended group by ID before assigning.");
        if (arr.GetArrayLength() == 1)
        {
            var id = arr[0].GetSafeString("id");
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException($"Group '{displayName}' was returned without an ID.");
            return id;
        }
        return null;
    }

    private async Task<string?> CreateSecurityGroupAsync(string displayName, UploadLogger log, CancellationToken ct)
    {
        var payload = new
        {
            displayName,
            mailEnabled = false,
            mailNickname = SanitizeMailNickname(displayName),
            securityEnabled = true,
            groupTypes = Array.Empty<string>(),
        };
        var response = await _graph.PostAsync(GraphClient.Groups, payload, "Create group", ct, throwOnError: false);
        if (!response.IsSuccess)
        {
            var hint = response.StatusCode == 403 ? " (needs Group.ReadWrite.All; grant it on the Advanced screen)" : "";
            RecordFollowUpWarning($"Could not create group '{displayName}' (HTTP {response.StatusCode}){hint}: {GraphException.ExtractMessage(response.Body)}", log);
            return null;
        }

        var id = response.Json.GetSafeString("id");
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException($"Entra accepted the group '{displayName}' but did not return an ID. Check the group before retrying.");
        log.Success($"Created group '{displayName}'");
        return id;
    }

    private async Task CreateGroupAssignmentAsync(string appId, string groupId, AssignmentIntent intent, string groupName, UploadLogger log, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.mobileAppAssignment",
            ["intent"] = intent switch
            {
                AssignmentIntent.Required => "required",
                AssignmentIntent.Uninstall => "uninstall",
                _ => "available",
            },
            ["target"] = new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.groupAssignmentTarget",
                ["groupId"] = groupId,
            },
        };
        var response = await _graph.PostAsync($"{GraphClient.MobileApps}/{appId}/assignments", payload, "Assign group", ct, throwOnError: false);
        if (response.IsSuccess)
            log.Success($"Assigned '{groupName}' ({intent})");
        else
            RecordFollowUpWarning($"Could not assign '{groupName}' (HTTP {response.StatusCode}): {GraphException.ExtractMessage(response.Body)}", log);
    }

    private static AssignmentIntent ParseIntent(string intent) => intent.ToLowerInvariant() switch
    {
        "uninstall" => AssignmentIntent.Uninstall,
        "available" => AssignmentIntent.Available,
        _ => AssignmentIntent.Required,
    };

    // Graph caps mailNickname at 64 characters and allows letters, digits, '_' and '-'.
    private static string SanitizeMailNickname(string displayName)
    {
        var nickname = new string(displayName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (nickname.Length > 64) nickname = nickname[..64];
        return string.IsNullOrEmpty(nickname) ? "group" : nickname;
    }
}
