using Packman.Services;
using Packman.ViewModels;

namespace Packman.Views.Controls;

/// <summary>
/// How the live console is shown, shared by every page's rail and the pop-out window so the
/// console keeps its height, filter and place as you move around the app.
/// </summary>
public sealed class ConsoleState : ObservableObject
{
    public const double MinHeight = 90;
    public const double DefaultHeight = 220;

    public static ConsoleState Shared { get; } = new();

    private ConsoleState()
    {
        // A collapsed console must not hide a warning or an error: it opens for one.
        ActivityLog.Shared.IssueAdded += _ => IsCollapsed = false;
    }

    private double _height = DefaultHeight;
    /// <summary>The docked console's height; dragged with the grip on its top edge.</summary>
    public double Height { get => _height; set => Set(ref _height, Math.Max(MinHeight, value)); }

    private bool _isCollapsed;
    /// <summary>Docked console shows its header only.</summary>
    public bool IsCollapsed { get => _isCollapsed; set => Set(ref _isCollapsed, value); }

    private bool _isPoppedOut;
    /// <summary>The console is in its own window; the rails show where it went.</summary>
    public bool IsPoppedOut { get => _isPoppedOut; set => Set(ref _isPoppedOut, value); }

    private bool _issuesOnly;
    /// <summary>Only warnings and errors are listed.</summary>
    public bool IssuesOnly { get => _issuesOnly; set => Set(ref _issuesOnly, value); }
}
