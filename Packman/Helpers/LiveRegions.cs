using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Packman.Helpers;

/// <summary>
/// Makes AutomationProperties.LiveSetting work. WPF only records the setting; nothing tells
/// a screen reader that the text changed unless the app raises LiveRegionChanged itself.
/// Every TextBlock marked Polite or Assertive is watched while it is loaded, and its new
/// text is announced.
/// </summary>
public static class LiveRegions
{
    private static readonly DependencyPropertyDescriptor Text =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    private static bool _enabled;

    /// <summary>Called once at startup, before any window is created.</summary>
    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnUnloaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock block || AutomationProperties.GetLiveSetting(block) == AutomationLiveSetting.Off) return;
        // Loaded can fire again without an Unloaded in between; never subscribe twice.
        Text.RemoveValueChanged(block, OnTextChanged);
        Text.AddValueChanged(block, OnTextChanged);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // The descriptor holds the element strongly, so the handler goes with the element.
        if (sender is TextBlock block) Text.RemoveValueChanged(block, OnTextChanged);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock block || string.IsNullOrWhiteSpace(block.Text) || !block.IsVisible) return;
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;

        var peer = UIElementAutomationPeer.FromElement(block) ?? UIElementAutomationPeer.CreatePeerForElement(block);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
