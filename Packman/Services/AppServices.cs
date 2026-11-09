namespace Packman.Services;

/// <summary>
/// App-wide singletons. No DI container; the sign-in done on the Settings page has to
/// be reachable from the upload flow.
/// </summary>
public static class AppServices
{
    static AppServices()
    {
        Auth.StateChanged += Apps.InvalidateApplicationCache;

        // Upload logs follow Settings ▸ Paths ▸ Upload logs folder.
        UploadLogger.ConfiguredFolder = Settings.Settings.NetworkPaths.UploadLogsFolder;
        Settings.Saved += () => UploadLogger.ConfiguredFolder = Settings.Settings.NetworkPaths.UploadLogsFolder;
    }

    public static SettingsService Settings { get; } = new();
    public static IntuneAuthService Auth { get; } = new();
    public static IntuneService Apps { get; } = new(() => Auth.GetAccessTokenAsync());
    public static IDialogService Dialogs { get; set; } = new MessageBoxDialogService();
}
