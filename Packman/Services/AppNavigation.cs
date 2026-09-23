using Packman.ViewModels;

namespace Packman.Services;

/// <summary>
/// Lets any page or rail ask the main window to switch pages, without holding a
/// reference to it. The "Sign in" links in the title bar and the rails use
/// <see cref="OpenSettings"/>.
/// </summary>
public static class AppNavigation
{
    public const string Settings = "settings";
    public const string SettingsPaths = "settings-paths";
    public const string SettingsDefaults = "settings-defaults";
    public const string Upload = "upload";
    public const string Applications = "applications";

    /// <summary>Raised with the page key; MainWindow switches to it.</summary>
    public static event Action<string>? Requested;

    public static void Go(string page) => Requested?.Invoke(page);

    /// <summary>Opens Settings on the Authentication section.</summary>
    public static RelayCommand OpenSettings { get; } = new(() => Go(Settings));
}
