using Packman.Helpers;
using Packman.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Packman.Views;

public partial class StepGenerate : UserControl
{
    public StepGenerate()
    {
        InitializeComponent();
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Application Source File",
            Filter = "Supported Files (*.msi;*.exe)|*.msi;*.exe|MSI Files (*.msi)|*.msi|EXE Files (*.exe)|*.exe|All Files (*.*)|*.*"
        };

        var browsePath = VM?.CreatePackage.SourcesPath;
        if (!string.IsNullOrEmpty(browsePath) && Directory.Exists(Path.GetDirectoryName(browsePath)))
            dialog.InitialDirectory = Path.GetDirectoryName(browsePath);

        if (dialog.ShowDialog() == true)
            VM?.CreatePackage.LoadFromFile(dialog.FileName);
    }

    private void SourcesPath_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SourcesPath_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0)
                VM?.CreatePackage.LoadFromFile(files[0]);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
        => Services.AppNavigation.Go(Services.AppNavigation.SettingsPaths);

    private void UploadExisting_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateToUploadIntune();

    private void BrowseUpgradePackage_Click(object sender, RoutedEventArgs e)
    {
        var folder = PackageFolderDialog.Show("Select existing PSADT package folder");
        if (folder != null) VM?.Upgrade.LoadPackage(folder);
    }

    private void BrowseUpgradeSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select New Source File",
            Filter = "Supported Files (*.msi;*.exe)|*.msi;*.exe|MSI Files (*.msi)|*.msi|EXE Files (*.exe)|*.exe|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
            VM?.Upgrade.SetNewSource(dialog.FileName);
    }
}
