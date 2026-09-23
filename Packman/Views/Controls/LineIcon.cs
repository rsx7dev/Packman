using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Packman.Views.Controls;

/// <summary>
/// A stroke icon from Icons.xaml drawn on its 24×24 grid, so every icon keeps the same
/// scale and weight whatever its bounds. Stroke follows Foreground, which inherits from
/// the button or text around it. Template in Themes/Styles.xaml.
/// </summary>
public class LineIcon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(LineIcon), new PropertyMetadata(null));
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(LineIcon), new PropertyMetadata(1.8));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
}
