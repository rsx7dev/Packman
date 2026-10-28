using Packman.Views;
using System.Windows;

namespace Packman.Services;

/// <summary>
/// Prompts a view model needs to raise. The WPF implementation shows a themed dialog for
/// questions and message boxes for notices; a test can answer them from code.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Asks before an action that destroys or interrupts something. The button named
    /// <paramref name="action"/> does it; <paramref name="safe"/> is focused, the default and
    /// what Esc picks. True when the action was chosen.
    /// </summary>
    bool Confirm(string message, string title, string action, string safe = "Cancel");

    /// <summary>
    /// Asks to <paramref name="yes"/>, <paramref name="no"/> or Cancel. Cancel is the safe
    /// choice, focused by default; null for Cancel.
    /// </summary>
    bool? ConfirmOrCancel(string message, string title, string yes, string no);

    void Warn(string message, string title);
    void Info(string message, string title);
}

public sealed class MessageBoxDialogService : IDialogService
{
    public bool Confirm(string message, string title, string action, string safe = "Cancel")
        => ConfirmDialog.Ask(message, title, action, null, safe) == ConfirmDialog.Choice.Action;

    public bool? ConfirmOrCancel(string message, string title, string yes, string no)
        => ConfirmDialog.Ask(message, title, yes, no, "Cancel") switch
        {
            ConfirmDialog.Choice.Action => true,
            ConfirmDialog.Choice.Second => false,
            _ => null,
        };

    public void Warn(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Info(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
