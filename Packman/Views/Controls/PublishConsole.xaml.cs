using Packman.Helpers;
using Packman.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Packman.Views.Controls;

/// <summary>The publish console: a scrolling log that follows the newest line until the owner scrolls up.</summary>
public partial class PublishConsole : UserControl
{
    private bool _follow = true;

    public PublishConsole() => InitializeComponent();

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The owner moved it: follow again once they are back at the bottom.
            _follow = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1;
        }
        else if (_follow || Scroller.ScrollableHeight == 0)
        {
            // New lines, or a cleared log for the next run.
            _follow = true;
            Scroller.ScrollToEnd();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && DataContext is PublishRunViewModel run) ClipboardCopy.Copy(button, run.LogText);
    }
}
