using Packman.Services;
using Packman.ViewModels;
using System.Windows;
using Wpf.Ui.Controls;

namespace Packman.Views;

/// <summary>First-run setup. The caller decides what Completed does: open the main window.</summary>
public partial class OnboardingWindow : FluentWindow
{
    public OnboardingViewModel ViewModel { get; }

    public OnboardingWindow() : this(new OnboardingViewModel(AppServices.Settings)) { }

    public OnboardingWindow(OnboardingViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        Loaded += (_, _) => RootBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the packages folder" };
        if (dialog.ShowDialog() == true) ViewModel.ApplicationsRoot = dialog.FolderName;
    }
}
