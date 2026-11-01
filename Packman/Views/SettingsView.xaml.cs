using Packman.Services;
using Packman.ViewModels;
using System.Windows.Controls;

namespace Packman.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(AppServices.Settings, AppServices.Auth);
    }

    /// <summary>Opens the Authentication section; the "Sign in" links land here.</summary>
    public void ShowAuthentication() => TabAuth.IsChecked = true;

    /// <summary>Opens Network paths; "Change in Settings" beside the output share lands here.</summary>
    public void ShowNetworkPaths() => TabNetworkPaths.IsChecked = true;

    /// <summary>Opens Intune defaults, where the install and uninstall command lines are set.</summary>
    public void ShowIntuneDefaults() => TabIntuneDefaults.IsChecked = true;
}
