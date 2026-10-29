using Packman.Helpers;

namespace Packman.Models;

/// <summary>The buckets the Devices section filters on. Unknown rows only show under All.</summary>
public enum DeviceInstallState { Installed, Failed, Pending, NotInstalled, NotApplicable, Unknown }

/// <summary>One device's row in the Intune device install status report for an app.</summary>
public sealed class DeviceInstallStatus
{
    public string DeviceName { get; init; } = "";
    public string UserPrincipalName { get; init; } = "";
    public DeviceInstallState State { get; init; }

    /// <summary>The report's own text for the install state detail, e.g. "Detection failed after install".</summary>
    public string StateDetail { get; init; } = "";

    /// <summary>Signed 32-bit code as Intune reports it; 0 when there is none.</summary>
    public int ErrorCode { get; init; }

    public DateTime? LastModified { get; init; }
    public string OsVersion { get; init; } = "";

    public string HexErrorCode => ErrorCode == 0 ? "" : $"0x{unchecked((uint)ErrorCode):X8}";

    /// <summary>"0x87D1041C (-2016345060)"; empty when there is no error.</summary>
    public string ErrorCodeText => ErrorCode == 0 ? "" : $"{HexErrorCode} ({ErrorCode})";

    /// <summary>Documented meaning of the code, or empty: an unknown code is shown by its value only.</summary>
    public string ErrorDescription => ErrorCode == 0 ? "" : InstallErrorText.Describe(ErrorCode) ?? "";

    /// <summary>The detail line: the report's text first, then the code and its documented meaning.</summary>
    public string DetailText => string.Join(" · ",
        new[] { StateDetail, ErrorCodeText, ErrorDescription }.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } text
        ? text : "No detail reported";

    public string LastModifiedText => LastModified is { } t ? t.ToLocalTime().ToString("g") : "—";
    public string OsVersionText => string.IsNullOrWhiteSpace(OsVersion) ? "—" : OsVersion;

    // StatusBadgeTemplate binds StatusLabel + StatusKind.
    public string StatusLabel => Label(State);
    public string StatusKind => State switch
    {
        DeviceInstallState.Installed => "ok",
        DeviceInstallState.Failed => "bad",
        DeviceInstallState.Pending => "warn",
        _ => "mut",
    };

    public static string Label(DeviceInstallState state) => state switch
    {
        DeviceInstallState.Installed => "Installed",
        DeviceInstallState.Failed => "Failed",
        DeviceInstallState.Pending => "Pending",
        DeviceInstallState.NotInstalled => "Not installed",
        DeviceInstallState.NotApplicable => "Not applicable",
        _ => "Unknown",
    };
}
