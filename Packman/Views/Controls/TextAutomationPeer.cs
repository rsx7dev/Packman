using System.Windows;
using System.Windows.Automation.Peers;

namespace Packman.Views.Controls;

/// <summary>
/// Exposes a templated control to screen readers as one named element. The text inside a
/// control template is not in the automation tree on its own, so without a peer a status
/// row or an app tile is read as nothing at all.
/// </summary>
internal sealed class TextAutomationPeer(FrameworkElement owner, Func<string> name, AutomationControlType type)
    : FrameworkElementAutomationPeer(owner)
{
    protected override string GetNameCore()
    {
        var given = base.GetNameCore();
        return string.IsNullOrEmpty(given) ? name() : given;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => type;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    /// <summary>Joins the parts that have text, so an empty one leaves no stray separator.</summary>
    internal static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
}
