using Packman.Helpers;
using Packman.Services;
using System.Windows;
using System.Windows.Controls;

namespace Packman.Views.Controls;

/// <summary>The live console: a scrolling view of the shared <see cref="ActivityLog"/> that follows the newest line until the owner scrolls up.</summary>
public partial class ActivityConsole : UserControl
{
    private bool _follow = true;

    public ActivityConsole() => InitializeComponent();

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The owner moved it: follow again once they are back at the bottom.
            _follow = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1;
        }
        else if (_follow || Scroller.ScrollableHeight == 0)
        {
            _follow = true;
            Scroller.ScrollToEnd();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ClipboardCopy.Copy((Button)sender, ActivityLog.Shared.Text);

    private void Clear_Click(object sender, RoutedEventArgs e) => ActivityLog.Shared.Clear();
}
