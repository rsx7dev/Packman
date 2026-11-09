using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Packman.Views.Controls;

/// <summary>
/// One line of a readiness list: a state mark, a label with an optional note under it,
/// and a trailing value ("Ready", "Not connected", "104"). Template in Themes/Styles.xaml.
/// </summary>
public class StatusRow : Control
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatusRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(nameof(Detail), typeof(string), typeof(StatusRow), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatusRow), new PropertyMetadata(""));
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(string), typeof(StatusRow), new PropertyMetadata("pending"));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Second line under the label. Hidden when empty.</summary>
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>ok, pending, warn, bad, off or info.</summary>
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>Read as "Detection: Ready, ok" with the note after it; the state mark is a shape, so its meaning is spoken.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new TextAutomationPeer(this,
        () => TextAutomationPeer.Join(". ",
            TextAutomationPeer.Join(", ", TextAutomationPeer.Join(": ", Label, Value), KindText),
            Detail),
        AutomationControlType.Text);

    private string KindText => Kind switch
    {
        "ok" => "ok",
        "warn" => "warning",
        "bad" => "problem",
        "pending" => "pending",
        "off" => "off",
        _ => "",
    };
}
