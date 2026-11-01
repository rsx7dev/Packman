#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:project ../Packman/Packman.csproj

using System.IO;
using System.Windows;
using System.Windows.Threading;
using Packman;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;

internal static class ShowSetup
{
    [STAThread]
    public static void Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var path = Path.Combine(Path.GetTempPath(), "packman-showsetup", "appsettings.json");
        var window = new OnboardingWindow(new OnboardingViewModel(new SettingsService(path)));
        window.Closed += (_, _) => Dispatcher.CurrentDispatcher.InvokeShutdown();
        window.Show();
        Dispatcher.Run();
    }
}
