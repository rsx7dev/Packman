using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Packman.Views;

public partial class ApplicationsView : UserControl
{
    public ApplicationsViewModel ViewModel { get; }

    /// <summary>Raised on row activation; the host swaps in the detail screen.</summary>
    public event Action<IntuneApplication>? AppOpened;

    /// <summary>Raised on "connect"; the host switches to Settings.</summary>
    public event Action? ConnectRequested;

    public ApplicationsView()
    {
        ViewModel = new ApplicationsViewModel();
        ViewModel.OpenRequested += a => AppOpened?.Invoke(a);
        ViewModel.ConnectRequested += () => ConnectRequested?.Invoke();
        DataContext = ViewModel;
        InitializeComponent();
    }

    /// <summary>Loads or refreshes the list. Called each time the screen is shown.</summary>
    public void Load() => ErrorReporter.FireAndForget(() => ViewModel.LoadAsync());

    // A single click only selects (the rail shows the app); double click or Enter opens it.
    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AppList, (DependencyObject)e.OriginalSource) is ListBoxItem item)
            ViewModel.OpenCommand.Execute(item.DataContext as IntuneApplication);
    }

    private void AppList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel.SelectedApp == null) return;
        ViewModel.OpenCommand.Execute(ViewModel.SelectedApp);
        e.Handled = true;
    }
}
