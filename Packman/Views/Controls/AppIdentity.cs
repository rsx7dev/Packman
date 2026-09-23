using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Packman.Views.Controls;

/// <summary>
/// App tile with name, publisher and a mono meta line ("4.2.0 · x64 · System").
/// The tile shows the app icon when there is one, otherwise the first letter of the name.
/// Template in Themes/Styles.xaml.
/// </summary>
public class AppIdentity : Control
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(AppIdentity), new PropertyMetadata("", OnTitleChanged));
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(AppIdentity), new PropertyMetadata(""));
    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(nameof(Meta), typeof(string), typeof(AppIdentity), new PropertyMetadata(""));
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(ImageSource), typeof(AppIdentity), new PropertyMetadata(null));
    public static readonly DependencyProperty TileSizeProperty =
        DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(AppIdentity), new PropertyMetadata(56.0));
    private static readonly DependencyPropertyKey InitialPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Initial), typeof(string), typeof(AppIdentity), new PropertyMetadata("?"));
    public static readonly DependencyProperty InitialProperty = InitialPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IconPathProperty =
        DependencyProperty.Register(nameof(IconPath), typeof(string), typeof(AppIdentity), new PropertyMetadata("", OnIconPathChanged));

    /// <summary>File path of an icon image; loaded into <see cref="Icon"/>. A bad path leaves the letter tile.</summary>
    public string IconPath { get => (string)GetValue(IconPathProperty); set => SetValue(IconPathProperty, value); }

    private static void OnIconPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var path = e.NewValue as string;
        ImageSource? image = null;
        if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
        {
            try
            {
                // OnLoad so the temp icon file isn't held open.
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch { image = null; }
        }
        d.SetValue(IconProperty, image);
    }

    public static readonly DependencyProperty IsTileOnlyProperty =
        DependencyProperty.Register(nameof(IsTileOnly), typeof(bool), typeof(AppIdentity), new PropertyMetadata(false));

    /// <summary>Just the tile, for places where the name is already shown beside it.</summary>
    public bool IsTileOnly { get => (bool)GetValue(IsTileOnlyProperty); set => SetValue(IsTileOnlyProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string Meta { get => (string)GetValue(MetaProperty); set => SetValue(MetaProperty, value); }
    public ImageSource? Icon { get => (ImageSource?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double TileSize { get => (double)GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }

    /// <summary>First letter of the title, for the tile when there is no icon.</summary>
    public string Initial => (string)GetValue(InitialProperty);

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var title = (e.NewValue as string ?? "").Trim();
        d.SetValue(InitialPropertyKey, title.Length == 0 ? "?" : char.ToUpperInvariant(title[0]).ToString());
    }
}
