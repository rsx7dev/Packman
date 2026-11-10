using Packman.Helpers;
using Packman.Services;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Packman.Views.Controls;

/// <summary>
/// The live console: a scrolling view of the shared <see cref="ActivityLog"/> that follows the
/// newest line until the owner scrolls up. Docked in a rail, or <see cref="IsDetached"/> in the
/// pop-out <see cref="ConsoleWindow"/>; both share <see cref="ConsoleState"/>.
/// </summary>
public partial class ActivityConsole : UserControl
{
    private static ConsoleState State => ConsoleState.Shared;
    private static ActivityLog Log => ActivityLog.Shared;

    private readonly ListCollectionView _lines;
    private bool _follow = true;

    public ActivityConsole()
    {
        InitializeComponent();
        _lines = new ListCollectionView(Log.Lines) { Filter = Show };
        LinesList.ItemsSource = _lines;
        Loaded += (_, _) =>
        {
            State.PropertyChanged += OnStateChanged;
            Log.PropertyChanged += OnLogChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            State.PropertyChanged -= OnStateChanged;
            Log.PropertyChanged -= OnLogChanged;
        };
    }

    /// <summary>True for the console in its own window: it fills the window and docks back instead of popping out.</summary>
    public bool IsDetached { get; set; }

    private static bool Show(object item) =>
        !State.IssuesOnly || item is ActivityLine { Level: ActivityLevel.Warning or ActivityLevel.Error };

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsoleState.IssuesOnly)) _lines.Refresh();
        Refresh();
    }

    private void OnLogChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var awayInWindow = State.IsPoppedOut && !IsDetached;
        var showLines = IsDetached || (!State.IsPoppedOut && !State.IsCollapsed);

        Grip.Visibility = !IsDetached && showLines ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = showLines ? Visibility.Visible : Visibility.Collapsed;
        Body.Height = IsDetached ? double.NaN : State.Height;
        PoppedOutNote.Visibility = awayInWindow ? Visibility.Visible : Visibility.Collapsed;

        CollapseButton.IsEnabled = !IsDetached && !State.IsPoppedOut;
        Chevron.Visibility = CollapseButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ChevronTurn.Angle = State.IsCollapsed ? 0 : 90;

        PlaceButton.ToolTip = IsDetached ? "Dock the console back into the main window" : "Open the console in its own window";
        PlaceButton.Visibility = awayInWindow ? Visibility.Collapsed : Visibility.Visible;
        FilterButton.Content = State.IssuesOnly ? "Show all" : "Issues only";

        WarningBadge.Visibility = Log.WarningCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningCount.Text = Log.WarningCount.ToString();
        AutomationProperties.SetName(WarningBadge, $"{Log.WarningCount} warnings");
        ErrorBadge.Visibility = Log.ErrorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorCount.Text = Log.ErrorCount.ToString();
        AutomationProperties.SetName(ErrorBadge, $"{Log.ErrorCount} errors");

        EmptyNote.Text = Log.Lines.Count == 0 ? "Nothing yet. Generating, testing and publishing report here."
            : _lines.IsEmpty ? "No warnings or errors." : "";
        EmptyNote.Visibility = EmptyNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
        if (e.ExtentHeightChange != 0) Refresh();
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Up makes it taller. Never past most of the window, so the rail's own content stays reachable.
        var window = Window.GetWindow(this);
        var max = window == null ? 900 : Math.Max(ConsoleState.MinHeight, window.ActualHeight * 0.7);
        State.Height = Math.Min(max, State.Height - e.VerticalChange);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => State.IsCollapsed = !State.IsCollapsed;

    private void Filter_Click(object sender, RoutedEventArgs e) => State.IssuesOnly = !State.IssuesOnly;

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        if (State.IsPoppedOut) ConsoleWindow.Dock();
        else ConsoleWindow.PopOut();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ClipboardCopy.Copy((Button)sender, Log.Text);

    private void Clear_Click(object sender, RoutedEventArgs e) => Log.Clear();
}
