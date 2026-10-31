using Packman.ViewModels;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Packman.Views.Onboarding;

public partial class PathsStep : UserControl
{
    public PathsStep() => InitializeComponent();

    private OnboardingViewModel Vm => (OnboardingViewModel)DataContext;

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the applications share root" };
        if (dialog.ShowDialog() == true) Vm.ApplicationsRoot = dialog.FolderName;
    }

    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the PSADT v4 template folder" };
        if (dialog.ShowDialog() == true) Vm.TemplatePath = dialog.FolderName;
    }

    private void BrowseTool_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose IntuneWinAppUtil.exe",
            Filter = "IntuneWinAppUtil.exe|IntuneWinAppUtil.exe|Programs (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() == true) Vm.IntuneWinAppUtilPath = dialog.FileName;
    }

    private void PsadtLink_Click(object sender, RoutedEventArgs e) => Open(OnboardingViewModel.PsadtReleasesUrl);

    private void ToolLink_Click(object sender, RoutedEventArgs e) => Open(OnboardingViewModel.IntuneWinAppUtilUrl);

    // Opens the page in the browser; nothing is downloaded by Packman.
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
