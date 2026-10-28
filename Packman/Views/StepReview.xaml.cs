using Packman.Helpers;
using System.Windows;
using System.Windows.Controls;

namespace Packman.Views;

public partial class StepReview : UserControl
{
    public StepReview() => InitializeComponent();

    /// <summary>Copies the command line held in the button's Tag.</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } button) ClipboardCopy.Copy(button, text);
    }
}
