using System.Windows;
using Wpf.Ui.Controls;

namespace Packman.Views.Controls;

/// <summary>The console popped out of the rails. One at a time; closing it docks the console back.</summary>
public partial class ConsoleWindow : FluentWindow
{
    private static ConsoleWindow? _open;

    public ConsoleWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _open = null;
            ConsoleState.Shared.IsPoppedOut = false;
        };
    }

    /// <summary>Shows the console window, or brings the open one forward.</summary>
    public static void PopOut()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }

        // Not owned: it must be free to sit on another screen and stay open while the main
        // window is minimised. It closes with the app (see App's shutdown).
        var main = Application.Current?.MainWindow;
        _open = new ConsoleWindow();
        if (main != null && main != _open)
        {
            _open.WindowStartupLocation = WindowStartupLocation.Manual;
            _open.Left = main.Left + Math.Max(0, main.ActualWidth - _open.Width - 40);
            _open.Top = main.Top + 80;
        }
        ConsoleState.Shared.IsPoppedOut = true;
        _open.Show();
    }

    /// <summary>Closes the window; the rails show the console again.</summary>
    public static void Dock() => _open?.Close();
}
