using Packman.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Packman.Helpers;

public static class ClipboardCopy
{
    /// <summary>
    /// Puts the text on the clipboard and flashes "Copied" over the button. A locked
    /// clipboard is reported instead of swallowed.
    /// </summary>
    public static void Copy(Button button, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            ErrorReporter.Report(new InvalidOperationException($"Could not copy to the clipboard: {ex.Message}", ex));
            return;
        }

        var tip = new ToolTip { Content = "Copied", PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, IsOpen = true };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tip.IsOpen = false;
        };
        timer.Start();
    }
}
