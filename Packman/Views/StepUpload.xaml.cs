using System.Windows;
using System.Windows.Controls;
using Packman.Services;

namespace Packman.Views;

public partial class StepUpload : UserControl
{
    public StepUpload()
    {
        InitializeComponent();
    }

    private void OpenIntuneDefaults_Click(object sender, RoutedEventArgs e)
        => AppNavigation.Go(AppNavigation.SettingsDefaults);
}
