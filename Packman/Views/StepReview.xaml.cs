using System.Windows;
using System.Windows.Controls;

namespace Packman.Views;

public partial class StepReview : UserControl
{
    public StepReview() => InitializeComponent();

    /// <summary>Copies the command line held in the button's Tag.</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } && text.Length > 0)
        {
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy; the user can retry */ }
        }
    }
}
