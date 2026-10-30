using System.Windows.Controls;

namespace Packman.Views.Controls;

/// <summary>Package folder choices with a live example tree. DataContext is a <see cref="ViewModels.PackageLayoutViewModel"/>.</summary>
public partial class PackageLayoutEditor : UserControl
{
    public PackageLayoutEditor() => InitializeComponent();
}
