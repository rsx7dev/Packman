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
            ProductCode = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}", ProductVersion = "4.2.0"
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
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Packman;component/Themes/{theme}Theme.xaml")
            });

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
            foreach (var name in new[] { "TabAuth", "TabNetworkPaths", "TabIntuneDefaults", "TabGroupAssignment", "TabCodeSign", "TabAppearance" })
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

            shell.Close();

            var combo = new ComboBox { IsEditable = true, Text = "TEST-PC-01", Width = 240 };
            Render(combo, $"{theme}-editable-dropdown", 320, 100);
        }
        app.Shutdown();
        Console.WriteLine($"Native UI previews written to {_outputDirectory}");
    }

    /// <summary>The whole window content on the shell background, at a window size.</summary>
    private static void RenderShell(FrameworkElement root, string name, int width = 1440, int height = 860)
    {
        var surface = new Border { Background = (Brush)Application.Current.FindResource("ShellBrush"), Child = root };
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

    private static void Save(Border surface, string name, int width, int height)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
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
