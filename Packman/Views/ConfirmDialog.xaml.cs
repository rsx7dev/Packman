using System.Windows;

namespace Packman.Views;

/// <summary>
/// Confirmation that names its action and puts the safe choice first: that button is the
/// focused default, and Enter and Esc both pick it.
/// </summary>
public partial class ConfirmDialog : Window
{
    public enum Choice { Safe, Action, Second }

    private Choice _choice = Choice.Safe;

    private ConfirmDialog(string message, string title, string action, string? second, string safe)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ActionButton.Content = action;
        // Only a destructive action is red; a Save / Don't save question is not.
        if (second != null) ActionButton.Style = (Style)FindResource("GhostButton");
        SecondButton.Content = second;
        SecondButton.Visibility = second == null ? Visibility.Collapsed : Visibility.Visible;
        SafeButton.Content = safe;
        Loaded += (_, _) => SafeButton.Focus();
    }

    /// <summary>Shows the dialog over the main window and returns the button picked. Closing it picks the safe one.</summary>
    public static Choice Ask(string message, string title, string action, string? second, string safe)
    {
        var dialog = new ConfirmDialog(message, title, action, second, safe);
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void Action_Click(object sender, RoutedEventArgs e) => Pick(Choice.Action);
    private void Second_Click(object sender, RoutedEventArgs e) => Pick(Choice.Second);
    private void Safe_Click(object sender, RoutedEventArgs e) => Pick(Choice.Safe);

    private void Pick(Choice choice)
    {
        _choice = choice;
        Close();
    }
}
