#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:project ../Packman/Packman.csproj

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Packman;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;

// Windows-only developer utility. It renders the whole window, page by page, with sample
// state, without showing a window, authenticating, generating a package or executing a
// deployment. The script editor (WebView2) is not rendered: it is an HWND and would be blank.
internal static class UiPreviews
{
    private static string _outputDirectory = "";

    private static readonly string[] Pages =
        ["CreatePackagePage", "UploadIntunePage", "RemoteTestPage", "ApplicationsPage", "AppDetailPage", "AdvancedPage", "SettingsPage"];

    [STAThread]
    public static void Main(string[] args)
    {
        _outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/ui-previews");
        Directory.CreateDirectory(_outputDirectory);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();

        var vm = new MainViewModel();
        vm.CreatePackage.AppName = "Contoso Reader";
        vm.CreatePackage.Manufacturer = "Contoso";
        vm.CreatePackage.Version = "4.2.0";
        vm.CreatePackage.SourcesPath = @"C:\Sources\ContosoReader-4.2.0-x64.msi";
        vm.CreatePackage.CurrentMsiInfo = new MsiInfoService.MsiInfo
        {
            ProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}", ProductVersion = "4.2.0",
            UpgradeCode = "{8B1E4D2A-6C3F-4E5B-9A7D-1F2E3C4B5A69}",
            Properties =
            [
                new("ProductName", "Contoso Reader"), new("ProductVersion", "4.2.0"), new("Manufacturer", "Contoso"),
                new("ProductCode", "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}"),
                new("UpgradeCode", "{8B1E4D2A-6C3F-4E5B-9A7D-1F2E3C4B5A69}"),
                new("ALLUSERS", "1"), new("REBOOT", "ReallySuppress"), new("INSTALLFOLDER", @"C:\Program Files\Contoso\Reader"),
                new("ARPNOREPAIR", "1"), new("SecureCustomProperties", "WIX_UPGRADE_DETECTED"),
            ],
            Transforms = ["Contoso.mst"],
        };

        var detail = new ApplicationDetailViewModel(new IntuneApplication
        {
            DisplayName = "Contoso Reader", Publisher = "Contoso", Version = "4.2.0",
            PublishingState = "published", Id = "sample-app", LastModified = DateTime.UtcNow
        });
        detail.Detail.InstallCommand = "Invoke-AppDeployToolkit.exe Install -DeployMode Silent";
        detail.Detail.UninstallCommand = "Invoke-AppDeployToolkit.exe Uninstall -DeployMode Silent";
        detail.Detail.RestartBehavior = "basedOnReturnCode";
        detail.Detail.MaxRunTimeMinutes = 60;
        detail.Detail.MinDiskSpaceMB = 500;
        detail.Detail.MinimumOperatingSystem = "Windows 11 22H2";
        detail.Detail.Size = 84 * 1024 * 1024;
        detail.Detail.Description = "PDF reader for managed Windows devices. Sample application for layout verification.";
        detail.Detail.Statistics = new InstallationStatistics
        {
            TotalDevices = 120, SuccessfulInstalls = 104, PendingInstalls = 8, FailedInstalls = 3,
            NotInstalled = 3, NotApplicable = 2
        };
        // One product code across every sample screen.
        var rule = DetectionRuleFactory.FromMethod(DetectionMethod.MsiProductCode, "", "", "", null,
            RegistryHiveNames.LocalMachine, "", "", "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}")!;
        detail.Detail.DetectionRules.Add(rule);
        detail.DetectionDisplays.Add(DetectionRuleDisplay.From(rule));
        var assignment = new AssignedGroup
        {
            GroupId = "sample-group", GroupName = "Workplace engineering: Windows application pilot devices",
            AssignmentType = "required"
        };
        detail.Detail.AssignedGroups.Add(assignment);

        foreach (var theme in new[] { "Dark", "Light" })
        {
            // The app's own switch, so the Fluent controls follow the palette as they do live.
            ThemeService.Apply(theme == "Light" ? AppTheme.Light : AppTheme.Dark);

            var shell = new MainWindow();
            // Layout only: no window is shown, no sign-in or Graph operation is started.
            var root = (FrameworkElement)shell.Content;
            shell.Content = null;
            root.DataContext = vm;
            void Show(string page)
            {
                foreach (var name in Pages)
                    ((UIElement)shell.FindName(name)).Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
            }

            // ── Create wizard ──
            Show("CreatePackagePage");
            vm.CreatePackage.CurrentPackagePath = "";
            vm.CurrentStepIndex = 0;
            RenderShell(root, $"{theme}-01-package");
            RenderShell(root, $"{theme}-01-package-compact", 1180, 780);

            vm.IsUpgradeMode = true;
            vm.Upgrade.ExistingPackagePath = @"C:\Packages\Contoso_Reader\4.2.0";
            vm.Upgrade.NewSourcePath = @"C:\Sources\ContosoReader-4.3.0-x64.msi";
            vm.Upgrade.NewVersion = "4.3.0";
            RenderShell(root, $"{theme}-01-upgrade");
            vm.IsUpgradeMode = false;
            vm.Upgrade.Reset();

            // A non-existent local sample path is sufficient for summaries; no generation or upload runs.
            vm.CreatePackage.CurrentPackagePath = Path.Combine(Path.GetTempPath(), "Packman-UI-sample", "Contoso_Reader", "4.2.0");
            RenderShell(root, $"{theme}-02-package-generated");

            vm.CurrentStepIndex = 1;
            vm.Upload.SelectedDeployMode = "Silent";
            vm.Upload.GroupPicker.SelectedGroups.Clear();
            vm.Upload.GroupPicker.SelectedGroups.Add(assignment);
            RenderShell(root, $"{theme}-03-configure", 1440, 1500);

            vm.CurrentStepIndex = 2;
            RenderShell(root, $"{theme}-04-review", 1440, 1500);
            vm.CurrentStepIndex = 0;

            // ── Other pages ──
            Show("UploadIntunePage");
            RenderShell(root, $"{theme}-05-upload", 1440, 1300);

            Show("RemoteTestPage");
            var remote = (RemoteTestView)shell.FindName("RemoteTestPage");
            remote.ViewModel.TargetComputer = "TEST-PC-01";
            remote.ViewModel.Lines.Clear();
            remote.ViewModel.Lines.Add(new RemoteTestLine { Time = "09:00", Text = "Sample output. No deployment executed in this preview.", Kind = LineKind.Dim });
            RenderShell(root, $"{theme}-06-remote-test");

            Show("ApplicationsPage");
            RenderShell(root, $"{theme}-07-applications");

            Show("AppDetailPage");
            var detailPage = (ApplicationDetailView)shell.FindName("AppDetailPage");
            detailPage.DataContext = detail;
            foreach (var tab in new[] { "overview", "package", "deployment" })
            {
                detail.Tab = tab;
                RenderShell(root, $"{theme}-08-detail-{tab}", 1440, 1200);
            }
            detail.DetectionDisplays[0].BeginEdit();
            RenderShell(root, $"{theme}-08-detail-edit-detection", 1440, 1200);
            detail.DetectionDisplays[0].CancelEdit();

            Show("AdvancedPage");
            RenderShell(root, $"{theme}-09-advanced");

            Show("SettingsPage");
            var settings = (SettingsView)shell.FindName("SettingsPage");
            var settingsVm = (SettingsViewModel)settings.DataContext;
            foreach (var name in new[] { "TabAuth", "TabNetworkPaths", "TabPackageLayout", "TabIntuneDefaults", "TabGroupAssignment", "TabCodeSign", "TabAppearance" })
            {
                ((RadioButton)settings.FindName(name)).IsChecked = true;
                ((ScrollViewer)settings.FindName("SectionScroll")).ScrollToTop();
                RenderShell(root, $"{theme}-10-settings-{name}");
            }
            settingsVm.CodeSigningEnabled = true;
            settingsVm.IsAppRegistration = true;
            settingsVm.CreateGroupPerPackage = true;
            settingsVm.CreateUninstallGroupPerPackage = true;
            foreach (var name in new[] { "TabAuth", "TabCodeSign", "TabGroupAssignment" })
            {
                ((RadioButton)settings.FindName(name)).IsChecked = true;
                ((ScrollViewer)settings.FindName("SectionScroll")).ScrollToTop();
                RenderShell(root, $"{theme}-10-settings-{name}-expanded", 1440, 1100);
            }
            settingsVm.CodeSigningEnabled = false;
            settingsVm.IsAppRegistration = false;
            settingsVm.CreateGroupPerPackage = false;
            settingsVm.CreateUninstallGroupPerPackage = false;

            RenderExtraStates(theme, root, shell, vm, Show);

            shell.Close();

            // First-run setup, on settings in a temp folder so none of the user's paths show.
            var setup = new OnboardingWindow(new OnboardingViewModel(
                new SettingsService(Path.Combine(Path.GetTempPath(), "packman-preview-settings.json"))));
            var setupRoot = (FrameworkElement)setup.Content;
            setup.Content = null;
            RenderShell(setupRoot, $"{theme}-00-setup", 760, 440);
            setup.Close();

            var combo = new ComboBox { IsEditable = true, Text = "TEST-PC-01", Width = 240 };
            Render(combo, $"{theme}-editable-dropdown", 320, 100);
        }
        app.Shutdown();
        Console.WriteLine($"Native UI previews written to {_outputDirectory}");
    }

    /// <summary>
    /// States the page-by-page pass does not reach: wide and narrow windows, Settings tabs scrolled
    /// to the bottom, a deeper package folder format, and the console with lines, filtered and
    /// collapsed. Dark gets the full set; Light a subset, since layout does not depend on the theme.
    /// </summary>
    private static void RenderExtraStates(string theme, FrameworkElement root, MainWindow shell, MainViewModel vm, Action<string> show)
    {
        var full = theme == "Dark";
        var settings = (SettingsView)shell.FindName("SettingsPage");
        var settingsVm = (SettingsViewModel)settings.DataContext;
        var sectionScroll = (ScrollViewer)settings.FindName("SectionScroll");
        void Tab(string name)
        {
            ((RadioButton)settings.FindName(name)).IsChecked = true;
            sectionScroll.ScrollToTop();
        }

        // ── Wide windows: the 860px column stays centred and the rail grows to 480px ──
        (int Width, int Height)[] wideSizes = full ? [(1920, 1080), (2560, 1300)] : [(1920, 1080)];
        foreach (var (width, height) in wideSizes)
        {
            var tag = $"{theme}-20-wide{width}";
            show("CreatePackagePage");
            vm.CurrentStepIndex = 0;
            RenderShell(root, $"{tag}-package", width, height);
            if (!full) continue;
            vm.CurrentStepIndex = 1;
            RenderShell(root, $"{tag}-configure", width, height);
            vm.CurrentStepIndex = 0;
            show("UploadIntunePage");
            RenderShell(root, $"{tag}-upload", width, height);
            show("RemoteTestPage");
            RenderShell(root, $"{tag}-remote-test", width, height);
            show("SettingsPage");
            foreach (var name in new[] { "TabAuth", "TabNetworkPaths", "TabPackageLayout" })
            {
                Tab(name);
                RenderShell(root, $"{tag}-settings-{name}", width, height);
            }
        }

        // ── Narrow windows. The rail folds at a 1000px page frame, which is a window of about
        //    1228px with the navigation open; 1080px is the window's minimum width. ──
        show("CreatePackagePage");
        vm.CurrentStepIndex = 0;
        RenderShell(root, $"{theme}-21-narrow1240-package", 1240, 780);
        RenderShell(root, $"{theme}-21-narrow1216-package", 1216, 780);
        if (full)
        {
            RenderShell(root, $"{theme}-21-narrow1080-package", 1080, 780);
            // Every frame, shown or not: IsVisible is false for all of them without a window.
            foreach (var frame in Descendants<Packman.Views.Controls.PageFrame>(root)) frame.IsRailOpen = true;
            RenderShell(root, $"{theme}-21-narrow1080-package-details-open", 1080, 780);
            foreach (var frame in Descendants<Packman.Views.Controls.PageFrame>(root)) frame.IsRailOpen = false;
            vm.CurrentStepIndex = 1;
            RenderShell(root, $"{theme}-21-narrow1080-configure", 1080, 780);
            vm.CurrentStepIndex = 0;
            show("UploadIntunePage");
            RenderShell(root, $"{theme}-21-narrow1080-upload", 1080, 780);
            show("RemoteTestPage");
            RenderShell(root, $"{theme}-21-narrow1240-remote-test", 1240, 780);
            RenderShell(root, $"{theme}-21-narrow1080-remote-test", 1080, 780);
            show("SettingsPage");
            Tab("TabPackageLayout");
            RenderShell(root, $"{theme}-21-narrow1240-settings-TabPackageLayout", 1240, 780);
            RenderShell(root, $"{theme}-21-narrow1080-settings-TabPackageLayout", 1080, 780);
            Tab("TabAuth");
            RenderShell(root, $"{theme}-21-narrow1080-settings-TabAuth", 1080, 780);
        }

        // ── Settings: bottom of Paths and Package layout, and a format one folder deeper ──
        show("SettingsPage");
        Tab("TabNetworkPaths");
        RenderShell(root, $"{theme}-22-settings-TabNetworkPaths-bottom", 1440, 860, sectionScroll.ScrollToBottom);
        var layout = settingsVm.Layout;
        var savedTemplate = layout.FolderNameTemplate;
        var savedFolders = layout.ExtraFolders.ToList();
        layout.FolderNameTemplate = @"%vendor%\%appName%";
        foreach (var folder in new[] { "Documentation", "Source" })
            if (!layout.ExtraFolders.Contains(folder)) layout.ExtraFolders.Add(folder);
        Tab("TabPackageLayout");
        RenderShell(root, $"{theme}-22-settings-TabPackageLayout-vendor-folder", 1440, 860);
        RenderShell(root, $"{theme}-22-settings-TabPackageLayout-vendor-folder-bottom", 1440, 860, sectionScroll.ScrollToBottom);
        if (full)
        {
            sectionScroll.ScrollToTop();
            RenderShell(root, $"{theme}-22-settings-TabPackageLayout-vendor-folder-tall", 1440, 1300);
            RenderShell(root, $"{theme}-22-settings-TabPackageLayout-vendor-folder-narrow1080", 1080, 1300);
            layout.FolderNameTemplate = "%vendor%_%appName%_%version%";
            RenderShell(root, $"{theme}-22-settings-TabPackageLayout-format-problem", 1440, 1300);
        }
        layout.FolderNameTemplate = savedTemplate;
        layout.ExtraFolders.Clear();
        foreach (var folder in savedFolders) layout.ExtraFolders.Add(folder);
        sectionScroll.ScrollToTop();
        Tab("TabAuth");

        // ── Console: lines with a warning and an error, issues only, collapsed ──
        show("CreatePackagePage");
        vm.CurrentStepIndex = 0;
        var log = ActivityLog.Shared;
        var console = Packman.Views.Controls.ConsoleState.Shared;
        log.Section("Generate package");
        log.Info(@"Copying the PSADT template to C:\Packages\Contoso_Contoso_Reader\4.2.0\Application");
        log.Info("Reading ContosoReader-4.2.0-x64.msi");
        log.Warning("The installer has no icon. A placeholder icon is used for the upload.");
        log.Success("Invoke-AppDeployToolkit.ps1 written");
        log.Error(@"Signing failed: no certificate with thumbprint 9F3A52C1D07B4E6688A1B2C3D4E5F60718293A4B in CurrentUser\My.");
        log.Info("Sample lines. Nothing ran in this preview.");
        RenderShell(root, $"{theme}-23-console-lines", 1440, 860, RefreshConsoles);
        console.IsCollapsed = true;
        RenderShell(root, $"{theme}-23-console-collapsed", 1440, 860, RefreshConsoles);
        console.IsCollapsed = false;
        if (full)
        {
            console.IssuesOnly = true;
            RenderShell(root, $"{theme}-23-console-issues-only", 1440, 860, RefreshConsoles);
            console.IssuesOnly = false;
            RenderShell(root, $"{theme}-23-console-lines-wide2560", 2560, 1300, RefreshConsoles);
            show("RemoteTestPage");
            RenderShell(root, $"{theme}-23-console-lines-remote-test", 1440, 860, RefreshConsoles);
            show("CreatePackagePage");
        }
        log.Clear();
        RenderShell(root, $"{theme}-23-console-cleared", 1440, 860, RefreshConsoles);

        // The console subscribes to its state on Loaded, which a view that is never shown may not get.
        void RefreshConsoles()
        {
            foreach (var view in Descendants<Packman.Views.Controls.ActivityConsole>(root))
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>The whole window content on the shell background, at a window size.</summary>
    private static void RenderShell(FrameworkElement root, string name, int width = 1440, int height = 860, Action? afterLayout = null)
    {
        var surface = new Border { Background = (Brush)Application.Current.FindResource("ShellBrush"), Child = root };
        // The content is lifted out of its window, so give it the text colour the window passes down.
        System.Windows.Documents.TextElement.SetForeground(surface, (Brush)Application.Current.FindResource("TextBrush"));
        if (afterLayout != null)
        {
            Layout(surface, width, height);
            afterLayout();
        }
        Save(surface, name, width, height);
        surface.Child = null;
    }

    private static void Render(FrameworkElement view, string name, int width, int height)
    {
        var surface = new Border
        {
            Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            Padding = new Thickness(24), Child = view
        };
        Save(surface, name, width, height);
        surface.Child = null;
    }

    private static void Layout(Border surface, int width, int height)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        // Do not pump the dispatcher here: the Application posts its Startup from its constructor,
        // and running that shows the real main window and connects a saved app registration.
    }

    private static void Save(Border surface, string name, int width, int height)
    {
        Layout(surface, width, height);
        if (surface.Child is FrameworkElement { ActualWidth: <= 0 })
            throw new InvalidOperationException($"{name} did not produce a layout.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_outputDirectory, name + ".png"));
        png.Save(stream);
    }
}
