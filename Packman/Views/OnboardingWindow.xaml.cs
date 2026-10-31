using Packman.Services;
using Packman.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace Packman.Views;

/// <summary>
/// First-run setup, also opened from Settings as Run setup again. The caller decides what
/// Completed does: open the main window on first run, set DialogResult from Settings.
/// </summary>
public partial class OnboardingWindow : FluentWindow
{
    public OnboardingViewModel ViewModel { get; }

    public OnboardingWindow() : this(new OnboardingViewModel(AppServices.Settings, AppServices.Auth)) { }

    public OnboardingWindow(OnboardingViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;

        SourceInitialized += (_, _) => vm.WindowHandle = new WindowInteropHelper(this).Handle;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Each step opens on its first field, so the keyboard can carry on from Next.
        if (e.PropertyName == nameof(OnboardingViewModel.CurrentStepIndex))
            Dispatcher.BeginInvoke(FocusCurrentStep, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FocusCurrentStep()
    {
        FrameworkElement[] panels = [WelcomePanel, ConnectionPanel, PathsPanel, LayoutPanel, SummaryPanel];
        panels[ViewModel.CurrentStepIndex].MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
}
