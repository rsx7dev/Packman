using System.Windows;
using System.Windows.Controls;

namespace Packman.Views.Controls;

/// <summary>
/// The layout every page shares: a header card (breadcrumb, title, actions and an optional
/// strip below such as the stepper or tabs), the body, an optional footer card, and an
/// optional inspector rail on the right. The template lives in Themes/Styles.xaml.
/// Below <see cref="CompactWidth"/> the rail stops taking a column and opens over the body
/// from a header button instead, so the form keeps its width on small or scaled screens.
/// </summary>
public class PageFrame : ContentControl
{
    /// <summary>Frame width below which the rail folds away.</summary>
    public const double CompactWidth = 1080;

    public static readonly DependencyProperty TitleProperty = Register<string>(nameof(Title), "");
    public static readonly DependencyProperty SubtitleProperty = Register<string>(nameof(Subtitle), "");
    public static readonly DependencyProperty LeadingProperty = Register<object?>(nameof(Leading), null);
    public static readonly DependencyProperty BreadcrumbProperty = Register<object?>(nameof(Breadcrumb), null);
    public static readonly DependencyProperty HeaderActionsProperty = Register<object?>(nameof(HeaderActions), null);
    public static readonly DependencyProperty HeaderBottomProperty = Register<object?>(nameof(HeaderBottom), null);
    public static readonly DependencyProperty FooterProperty = Register<object?>(nameof(Footer), null);
    public static readonly DependencyProperty RailProperty = Register<object?>(nameof(Rail), null);
    public static readonly DependencyProperty IsBodyScrollableProperty = Register(nameof(IsBodyScrollable), true);
    public static readonly DependencyProperty IsCompactProperty = Register(nameof(IsCompact), false);
    public static readonly DependencyProperty IsRailOpenProperty = Register(nameof(IsRailOpen), false);

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>Shown left of the title, e.g. the app tile on Application detail.</summary>
    public object? Leading { get => GetValue(LeadingProperty); set => SetValue(LeadingProperty, value); }

    /// <summary>Small trail above the title, e.g. "Applications / Contoso Reader".</summary>
    public object? Breadcrumb { get => GetValue(BreadcrumbProperty); set => SetValue(BreadcrumbProperty, value); }

    /// <summary>Buttons or links at the right of the title.</summary>
    public object? HeaderActions { get => GetValue(HeaderActionsProperty); set => SetValue(HeaderActionsProperty, value); }

    /// <summary>Strip under the title inside the header card: the stepper or a tab row.</summary>
    public object? HeaderBottom { get => GetValue(HeaderBottomProperty); set => SetValue(HeaderBottomProperty, value); }

    /// <summary>Action bar card under the body. Hidden when null.</summary>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    /// <summary>Inspector on the right. Hidden when null.</summary>
    public object? Rail { get => GetValue(RailProperty); set => SetValue(RailProperty, value); }

    /// <summary>False for bodies that manage their own scrolling (the script editor hosts a WebView2).</summary>
    public bool IsBodyScrollable { get => (bool)GetValue(IsBodyScrollableProperty); set => SetValue(IsBodyScrollableProperty, value); }

    /// <summary>Set by the frame from its own width. Read it, don't set it.</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    /// <summary>Compact mode only: the rail is showing over the body.</summary>
    public bool IsRailOpen { get => (bool)GetValue(IsRailOpenProperty); set => SetValue(IsRailOpenProperty, value); }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var compact = sizeInfo.NewSize.Width < CompactWidth;
        if (compact == IsCompact) return;
        IsCompact = compact;
        if (!compact) IsRailOpen = false;
    }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(PageFrame), new PropertyMetadata(defaultValue));
}
