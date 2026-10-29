namespace Packman.Helpers;

/// <summary>
/// Microsoft's documented text for install error codes a Win32 app reports. Only codes
/// with a published description are listed; anything else returns null and is shown by
/// its value.
/// </summary>
public static class InstallErrorText
{
    // https://learn.microsoft.com/windows/win32/msi/error-codes
    private static readonly Dictionary<int, string> WindowsInstaller = new()
    {
        [1601] = "The Windows Installer service couldn't be accessed. Contact your support personnel to verify that the Windows Installer service is properly registered.",
        [1602] = "The user canceled installation.",
        [1603] = "A fatal error occurred during installation.",
        [1605] = "This action is only valid for products that are currently installed.",
        [1612] = "The installation source for this product isn't available. Verify that the source exists and that you can access it.",
        [1618] = "Another installation is already in progress. Complete that installation before proceeding with this install.",
        [1619] = "This installation package couldn't be opened. Verify that the package exists and is accessible, or contact the application vendor to verify that this is a valid Windows Installer package.",
        [1620] = "This installation package couldn't be opened. Contact the application vendor to verify that this is a valid Windows Installer package.",
        [1624] = "There was an error applying transforms. Verify that the specified transform paths are valid.",
        [1625] = "This installation is forbidden by system policy. Contact your system administrator.",
        [1632] = "The Temp folder is either full or inaccessible. Verify that the Temp folder exists and that you can write to it.",
        [1633] = "This installation package isn't supported on this platform. Contact your application vendor.",
        [1638] = "Another version of this product is already installed. Installation of this version can't continue.",
        [1639] = "Invalid command line argument. Consult the Windows Installer SDK for detailed command-line help.",
        [1641] = "The installer has initiated a restart. This message indicates success.",
        [1643] = "The patch package isn't permitted by system policy.",
        [3010] = "A restart is required to complete the install. This message indicates success.",
    };

    // https://learn.microsoft.com/windows/win32/debug/system-error-codes--0-499- and --1000-1299-
    private static readonly Dictionary<int, string> Win32 = new()
    {
        [2] = "The system cannot find the file specified.",
        [3] = "The system cannot find the path specified.",
        [5] = "Access is denied.",
        [13] = "The data is invalid.",
        [32] = "The process cannot access the file because it is being used by another process.",
        [87] = "The parameter is incorrect.",
        [112] = "There is not enough space on the disk.",
        [1223] = "The operation was canceled by the user.",
    };

    // https://learn.microsoft.com/troubleshoot/mem/intune/app-management/app-install-error-codes
    private static readonly Dictionary<uint, string> Intune = new()
    {
        [0x87D1041C] = "Application installation succeeded but application is not detected.",
    };

    public static string? Describe(int code)
    {
        var unsigned = unchecked((uint)code);
        if (Intune.TryGetValue(unsigned, out var intune)) return intune;

        // HRESULT_FROM_WIN32 wraps a Win32 or Windows Installer code as 0x8007xxxx.
        var win32 = (unsigned & 0xFFFF0000) == 0x80070000 ? (int)(unsigned & 0xFFFF) : code;
        if (WindowsInstaller.TryGetValue(win32, out var msi)) return msi;
        return Win32.TryGetValue(win32, out var text) ? text : null;
    }
}
