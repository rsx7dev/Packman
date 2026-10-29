using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Packman.Services;
using Packman.ViewModels;
using Wpf.Ui.Controls;

namespace Packman;

public partial class MainWindow : FluentWindow
{
    private bool _closeConfirmed;
    private bool _closeRequested;

    public MainWindow()
    {
        DataContext = new MainViewModel();
        InitializeComponent();

        ApplicationsPage.AppOpened += app =>
        {
            AppDetailPage.Show(app);
            TrackRepublish(AppDetailPage.ViewModel);
            ShowOnly(AppDetailPage, "Applications / Detail");
        };
        ApplicationsPage.ConnectRequested += () => SettingsNavBtn.IsChecked = true;

        // The standalone Upload page publishes on its own; the wizard stepper waits for it too.
        UploadIntunePage.ViewModel.Publish.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PublishRunViewModel.IsRunning) && DataContext is MainViewModel main)
                main.IsUploadPageRunning = UploadIntunePage.ViewModel.Publish.IsRunning;
        };

        AppDetailPage.BackRequested += () => ShowOnly(ApplicationsPage, "Applications");
        AppDetailPage.Deleted += () =>
        {
            ShowOnly(ApplicationsPage, "Applications");
            ErrorReporter.FireAndForget(() => ApplicationsPage.ViewModel.LoadAsync(force: true));
        };
        // The detail page stays open on the refreshed app; the list behind it is now stale.
        AppDetailPage.Updated += () =>
            ErrorReporter.FireAndForget(() => ApplicationsPage.ViewModel.LoadAsync(force: true));

        AdvancedPage.ConnectRequested += () => SettingsNavBtn.IsChecked = true;

        AppNavigation.Requested += Navigate;
        Closed += (_, _) => AppNavigation.Requested -= Navigate;

        // The title bar template builds the caption buttons without names.
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => NameCaptionButtons(TitleBarControl)));
    }

    private static void NameCaptionButtons(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TitleBarButton button)
            {
                var label = System.Windows.Automation.AutomationProperties.GetAutomationId(button) switch
                {
                    "TitleBarMinimizeButton" => "Minimize",
                    "TitleBarMaximizeButton" => "Maximize or restore",
                    "TitleBarCloseButton" => "Close",
                    _ => null,
                };
                if (label != null) System.Windows.Automation.AutomationProperties.SetName(button, label);
            }
            NameCaptionButtons(child);
        }
    }

    /// <summary>A republish on the detail page locks the wizard stepper like any other running job.</summary>
    private void TrackRepublish(ApplicationDetailViewModel? detail)
    {
        if (detail == null || DataContext is not MainViewModel main) return;
        detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ApplicationDetailViewModel.IsUpdating)) main.IsRepublishing = detail.IsUpdating;
        };
    }

    /// <summary>Every generate, publish or republish in flight, with how to stop it. Empty when idle.</summary>
    private List<(string Name, ICommand Cancel)> RunningJobs(MainViewModel vm)
    {
        var jobs = new List<(string, ICommand)>();
        if (vm.CreatePackage.IsGenerating) jobs.Add(("package generation", vm.CreatePackage.CancelCommand));
        if (vm.Upload.Publish.IsRunning) jobs.Add(("publish", vm.Upload.Publish.CancelCommand));
        if (UploadIntunePage.ViewModel.Publish.IsRunning) jobs.Add(("upload", UploadIntunePage.ViewModel.Publish.CancelCommand));
        if (AppDetailPage.ViewModel is { IsUpdating: true } detail) jobs.Add(("republish", detail.CancelUpdateCommand));
        return jobs;
    }

    /// <summary>Page switches asked for from a rail, the title bar or another page.</summary>
    private void Navigate(string page)
    {
        switch (page)
        {
            case AppNavigation.Settings:
                SettingsNavBtn.IsChecked = true;
                SettingsPage.ShowAuthentication();
                break;
            case AppNavigation.SettingsPaths:
                SettingsNavBtn.IsChecked = true;
                SettingsPage.ShowNetworkPaths();
                break;
            case AppNavigation.SettingsDefaults:
                SettingsNavBtn.IsChecked = true;
                SettingsPage.ShowIntuneDefaults();
                break;
            case AppNavigation.Upload:
                UploadIntuneNavBtn.IsChecked = true;
                break;
            case AppNavigation.Applications:
                ApplicationsNavBtn.IsChecked = true;
                break;
        }
    }

    /// <summary>
    /// Asks before abandoning a running job, then lets the script editor save before the app
    /// closes. The waits are awaited, so the first close is cancelled and the second runs
    /// straight through. A failure here is reported and the close still proceeds, rather
    /// than trapping the user.
    /// </summary>
    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_closeConfirmed || DataContext is not MainViewModel vm) return;

        var jobs = RunningJobs(vm);
        if (jobs.Count == 0 && !vm.Editor.HasUnsavedChanges) return;

        e.Cancel = true;
        if (_closeRequested) return;   // already stopping a job or asking about the editor

        if (jobs.Count > 0)
        {
            var names = string.Join(" and ", jobs.Select(j => j.Name));
            if (!AppServices.Dialogs.Confirm(
                    $"The {names} {(jobs.Count == 1 ? "is" : "are")} still running. Closing now cancels {(jobs.Count == 1 ? "it" : "them")}.",
                    "Packman is busy", $"Cancel {names} and close", "Keep Packman open"))
                return;
            foreach (var job in jobs) job.Cancel.Execute(null);
        }

        _closeRequested = true;
        ErrorReporter.FireAndForget(async () =>
        {
            bool proceed;
            try
            {
                await Task.Yield();   // Close() below must not run inside this Closing event
                // Let the job clean up (a cancelled publish removes the half-built app) first.
                while (RunningJobs(vm).Count > 0) await Task.Delay(250);
                proceed = await vm.Editor.PromptSaveAllAsync();
            }
            catch (Exception ex)
            {
                ErrorReporter.Report(ex);
                proceed = true;
            }

            _closeRequested = false;
            if (!proceed) return;
            _closeConfirmed = true;
            Close();
        });
    }

    /// <summary>Shows one page and collapses the rest. Null-safe during load.</summary>
    private void ShowOnly(UIElement? page, string? screenTitle = null)
    {
        foreach (var p in new UIElement?[] { CreatePackagePage, RemoteTestPage, SettingsPage, UploadIntunePage, ApplicationsPage, AppDetailPage, AdvancedPage })
            if (p != null) p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;

        if (screenTitle != null && DataContext is MainViewModel vm) vm.ScreenTitle = screenTitle;
    }

    /// <summary>Switches to the Upload to Intune page, for the wizard's cross-link.</summary>
    public void NavigateToUploadIntune() => UploadIntuneNavBtn.IsChecked = true;

    /// <summary>A tool covers the wizard, so returning here closes it.</summary>
    private void CreatePackageNavBtn_Checked(object sender, RoutedEventArgs e)
    {
        ShowOnly(CreatePackagePage, "Create package");
        (DataContext as MainViewModel)?.CloseToolCommand.Execute(null);
    }

    private void UploadIntuneNavBtn_Checked(object sender, RoutedEventArgs e)
    {
        ShowOnly(UploadIntunePage, "Upload to Intune");
        UploadIntunePage.Refresh();
    }

    private void ApplicationsNavBtn_Checked(object sender, RoutedEventArgs e)
    {
        ShowOnly(ApplicationsPage, "Applications");
        ApplicationsPage.Load();
    }

    private void AdvancedNavBtn_Checked(object sender, RoutedEventArgs e)
    {
        ShowOnly(AdvancedPage, "Advanced");
        AdvancedPage.Refresh();
    }

    private void SettingsNavBtn_Checked(object sender, RoutedEventArgs e) => ShowOnly(SettingsPage, "Settings");

    /// <summary>
    /// Separate from the wizard's tool of the same name: this one starts with no package,
    /// so the user picks one built earlier.
    /// </summary>
    private void RemoteTestNavBtn_Checked(object sender, RoutedEventArgs e)
    {
        ShowOnly(RemoteTestPage, "Remote test");
        RemoteTestPage.Refresh();
    }
}
