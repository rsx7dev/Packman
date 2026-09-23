using System.Buffers.Text;
using System.Text.Json;

namespace Packman.Services;

public sealed class ConnectionCheck
{
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class ConnectionTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    /// <summary>One line for the Settings rail: "Read and write verified", "Read only: publishing will fail"…</summary>
    public string PermissionSummary { get; init; } = "Not tested";

    public IReadOnlyList<ConnectionCheck> Checks { get; init; } = Array.Empty<ConnectionCheck>();
}

public partial class IntuneService
{
    /// <summary>
    /// Probes read access to each service, then checks write permission without writing
    /// anything: the access token lists what was granted (<c>scp</c> for a signed-in user,
    /// <c>roles</c> for an app registration). A user's Entra role can still narrow it.
    /// </summary>
    /// <param name="checkGroupCreate">Also require permission to create groups (group per package is on).</param>
    public async Task<ConnectionTestResult> TestConnectionAsync(bool checkGroupCreate, CancellationToken ct = default)
    {
        string token;
        try
        {
            token = await _graph.GetTokenAsync();
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = $"Could not acquire a Microsoft Graph token: {ex.Message}",
            };
        }

        var checks = new List<ConnectionCheck>
        {
            await ProbeAsync("Intune apps · read access", $"{Base}?$top=1&$select=id", ct),
            await ProbeAsync("Entra groups · read access", $"{GraphClient.Groups}?$top=1&$select=id", ct),
            await ProbeAsync("Entra devices · read access", $"{GraphClient.Devices}?$top=1&$select=id", ct),
            await ProbeAsync("Entra users · read access", $"{GraphClient.Users}?$top=1&$select=id", ct),
        };
        var readOk = checks.All(c => c.Ok);

        var granted = GrantedPermissions(token);
        // Creating and updating Win32 apps, their content and assignments.
        var canPublish = granted.Contains("DeviceManagementApps.ReadWrite.All");
        checks.Add(PermissionCheck("Intune apps · write permission", canPublish, "DeviceManagementApps.ReadWrite.All"));

        // POST /groups: Group.ReadWrite.All (Group.Create also covers it for an app registration).
        var canCreateGroups = !checkGroupCreate
            || granted.Contains("Group.ReadWrite.All") || granted.Contains("Group.Create") || granted.Contains("Directory.ReadWrite.All");
        if (checkGroupCreate)
            checks.Add(PermissionCheck("Entra groups · create permission", canCreateGroups, "Group.ReadWrite.All"));

        var ok = readOk && canPublish && canCreateGroups;
        return new ConnectionTestResult
        {
            Success = ok,
            Message = !readOk ? "One or more Microsoft Graph read checks failed. Check the results below."
                : !canPublish ? "Read only: the sign-in lacks DeviceManagementApps.ReadWrite.All, so publishing will fail."
                : !canCreateGroups ? "Publishing is permitted, but creating a group per package needs Group.ReadWrite.All."
                : "Read access and write permissions verified. For a signed-in user, the Entra role can still limit changes.",
            PermissionSummary = !readOk ? "Read checks failed"
                : !canPublish ? "Read only: publishing will fail"
                : !canCreateGroups ? "Group creation not permitted"
                : "Read and write verified",
            Checks = checks,
        };
    }

    private static ConnectionCheck PermissionCheck(string name, bool ok, string permission) =>
        new() { Name = name, Ok = ok, Detail = ok ? "Granted" : $"{permission} missing" };

    /// <summary>Delegated scopes (<c>scp</c>) and application roles (<c>roles</c>) from the JWT payload.</summary>
    private static HashSet<string> GrantedPermissions(string token)
    {
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(token.Split('.')[1]));
        if (payload.RootElement.TryGetProperty("scp", out var scp))
            granted.UnionWith((scp.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (payload.RootElement.TryGetProperty("roles", out var roles))
            foreach (var role in roles.EnumerateArray())
                granted.Add(role.GetString() ?? "");
        return granted;
    }

    private async Task<ConnectionCheck> ProbeAsync(string name, string url, CancellationToken ct)
    {
        try
        {
            var response = await _graph.GetAsync(url, name, ct, throwOnError: false);
            if (response.IsSuccess)
                return new ConnectionCheck { Name = name, Ok = true, Detail = "OK" };

            var detail = response.StatusCode is 401 or 403
                ? "Read access denied"
                : $"HTTP {response.StatusCode}";
            return new ConnectionCheck { Name = name, Ok = false, Detail = detail };
        }
        catch (Exception ex)
        {
            return new ConnectionCheck { Name = name, Ok = false, Detail = ex.Message };
        }
    }
}
