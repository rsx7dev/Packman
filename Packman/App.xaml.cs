using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Packman.Services;
using Packman.ViewModels;

namespace Packman;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Without these, a throw off the UI thread takes the process down with no trace.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        ErrorReporter.Reported += ex => Report(ex, "Something went wrong.");

        ThemeService.Apply(AppServices.Settings.Settings.Theme);

        if (AppServices.Settings.Settings.NetworkPaths.IsComplete)
        {
            ShowMainWindow();
            ConnectSavedAppRegistration();
        }
        else
        {
            ShowSetup();
        }
    }

    // Set as MainWindow explicitly: dialogs and the Settings sign-in take their owner from it.
    private void ShowMainWindow()
    {
        var main = new Packman.MainWindow();
        MainWindow = main;
        main.Show();
    }

    /// <summary>First run: setup before anything else. Closing it unfinished ends the app.</summary>
    private void ShowSetup()
    {
        var setup = new Views.OnboardingWindow();
        MainWindow = setup;
        setup.ViewModel.Completed += () =>
        {
            ShowMainWindow();
            setup.Close();
        };
        setup.Show();
    }

    /// <summary>
    /// App-only auth needs no user interaction, so a configured app registration is
    /// connected at launch. Without it the app sits at "Not connected" pointing the user
    /// at a browser sign-in that app registration mode does not use.
    /// </summary>
    private static void ConnectSavedAppRegistration()
    {
        var settings = AppServices.Settings.Settings;
        if (settings.AuthMode != Models.AuthMode.AppRegistration) return;

        var cfg = settings.Authentication;
        if (string.IsNullOrWhiteSpace(cfg.TenantId) ||
            string.IsNullOrWhiteSpace(cfg.ClientId) ||
            string.IsNullOrWhiteSpace(cfg.CertificateThumbprint)) return;

        // Not awaited: startup carries on while the token is fetched. Left on the UI thread
        // so the sign-in notification lands there, like every other sign-in does.
        _ = SignInSilentlyAsync(cfg);
    }

    private static async Task SignInSilentlyAsync(Models.AppSettings.AuthConfig cfg)
    {
        try
        {
            await AppServices.Auth.SignInAsync(Models.AuthMode.AppRegistration, cfg, nint.Zero);
        }
        catch (Exception ex)
        {
            // A missing certificate or withdrawn consent must not block startup. The user
            // sees "Not connected" and the Settings connection test explains why.
            Debug.WriteLine($"App registration sign-in at startup failed: {ex.Message}");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report(e.Exception, "Packman hit an unexpected error.");
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Terminating either way; at least leave a log behind.
        if (e.ExceptionObject is Exception ex) WriteCrashLog(ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        WriteCrashLog(e.Exception);
    }

    /// <summary>Logs the failure and tells the user.</summary>
    private static void Report(Exception ex, string headline)
    {
        var logPath = WriteCrashLog(ex);
        var detail = logPath == null ? "" : $"\n\nDetails were written to:\n{logPath}";

        MessageBox.Show(
            $"{headline}\n\n{ex.Message}{detail}",
            "Packman", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string? WriteCrashLog(Exception ex)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packman", "Logs", "Errors");
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, $"error-{DateTime.Now:yyyy-MM-dd}.log");
            var entry = new StringBuilder()
                .AppendLine(new string('=', 80))
                .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine(ex.ToString())
                .AppendLine();

            File.AppendAllText(path, entry.ToString(), Encoding.UTF8);
            return path;
        }
        catch (Exception logFailure)
        {
            Debug.WriteLine($"Could not write the crash log: {logFailure.Message}");
            return null;
        }
    }
}
