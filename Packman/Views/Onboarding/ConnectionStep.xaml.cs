using Packman.Helpers;
using Packman.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Packman.Views.Onboarding;

public partial class ConnectionStep : UserControl
{
    public ConnectionStep() => InitializeComponent();

    private void CopyPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm)
            ClipboardCopy.Copy((Button)sender, vm.RequiredPermissionsText);
    }
}
