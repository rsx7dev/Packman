using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Packman.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Packman.Services;

/// <summary>
/// Swaps the palette dictionary at runtime. Every brush resolves with DynamicResource,
/// so the live window restyles without a restart. WPF UI's theme is switched alongside
/// it so the Fluent controls and the Mica backdrop follow.
/// </summary>
public static class ThemeService
{
    /// <summary>Packman amber, handed to WPF UI so Fluent controls match.</summary>
    private static readonly Color Accent = Color.FromRgb(0xE0, 0xA0, 0x50);

    // Absolute pack URIs: a relative Source resolves against the caller's context,
    // which is not the application root once this runs from a view model. They name the
    // Packman assembly, so they also resolve when another program hosts the app's
    // resources, as the preview renderer and the WPF tests do.
    private const string Dark = "pack://application:,,,/Packman;component/Themes/DarkTheme.xaml";
    private const string Light = "pack://application:,,,/Packman;component/Themes/LightTheme.xaml";

    /// <summary>True while the dark palette is loaded. Read by hosts that theme non-WPF surfaces.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the palette swapped, so surfaces outside WPF (Monaco) can follow.</summary>
    public static event Action? Changed;

    /// <summary>True while a Windows contrast theme is on and its colours replace the palette.</summary>
    public static bool IsHighContrast { get; private set; }

    private static AppTheme _requested = AppTheme.System;
    private static ResourceDictionary? _palette;
    private static bool _following;

    public static void Apply(AppTheme theme)
    {
        _requested = theme;
        FollowWindows();

        // A Windows contrast theme wins over the app's own choice: the user picked those
        // colours because they need them.
        var contrast = SystemParameters.HighContrast;
        var light = contrast
            ? Luminance(SystemColors.WindowColor) > 0.5
            : theme switch
            {
                AppTheme.Light => true,
                AppTheme.Dark => false,
                _ => SystemPrefersLight(),
            };
        var wanted = light ? Light : Dark;
        var wasDark = IsDark;
        var wasContrast = IsHighContrast;
        IsDark = !light;
        IsHighContrast = contrast;

        // WPF UI first: applying its theme resets the accent, so amber goes back after.
        var uiTheme = contrast ? ApplicationTheme.HighContrast : light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(uiTheme, WindowBackdropType.Mica, updateAccent: false);
        if (!contrast)
            ApplicationAccentColorManager.Apply(Accent, uiTheme, systemGlassColor: false, systemAccentColor: false);

        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null) return;

        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString ?? "";
            var isPalette = ReferenceEquals(merged[i], _palette)
                         || source.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)
                         || source.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase);
            if (!isPalette) continue;
            // Compare on file name: App.xaml declares a relative source, later swaps
            // use absolute pack URIs. A contrast palette is rebuilt every time, because
            // the system colours behind it may have changed.
            var wantedFile = wanted[(wanted.LastIndexOf('/') + 1)..];
            if (!contrast && !wasContrast && source.EndsWith(wantedFile, StringComparison.OrdinalIgnoreCase)) return;

            var palette = new ResourceDictionary { Source = new Uri(wanted, UriKind.Absolute) };
            _palette = contrast ? HighContrastPalette(palette) : palette;
            merged[i] = _palette;
            break;
        }

        if (wasDark != IsDark || wasContrast != IsHighContrast || contrast) Changed?.Invoke();
    }

    /// <summary>
    /// Re-applies the chosen theme when Windows changes underneath: a contrast theme turned
    /// on or off, or the light/dark app setting while Packman is set to System.
    /// </summary>
    private static void FollowWindows()
    {
        if (_following || Application.Current is null) return;
        _following = true;

        // Windows raises these in bursts; only a change that alters the outcome re-themes.
        void Reapply() => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var contrast = SystemParameters.HighContrast;
            var themeMoved = _requested == AppTheme.System && SystemPrefersLight() == IsDark;
            if (contrast || contrast != IsHighContrast || themeMoved) Apply(_requested);
        });
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Reapply();
        };
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
                Reapply();
        };
    }

    /// <summary>
    /// The palette with every colour replaced by the Windows contrast theme's own: surfaces
    /// take the window colour, text and every line the window text colour, the accent the
    /// highlight pair. Keys are matched by role, so a colour added to the themes later
    /// falls in with its group.
    /// </summary>
    private static ResourceDictionary HighContrastPalette(ResourceDictionary basis)
    {
        var palette = new ResourceDictionary();
        foreach (var key in basis.Keys.OfType<string>().ToList())
        {
            var isBrush = key.EndsWith("Brush", StringComparison.Ordinal);
            var isColor = key.EndsWith("Color", StringComparison.Ordinal);
            if (!isBrush && !isColor)
            {
                palette[key] = key == "IsDarkTheme" ? IsDark : basis[key];
                continue;
            }

            var color = ContrastColor(key[..^5]);
            if (isColor) palette[key] = color;
            else
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                palette[key] = brush;
            }
        }
        return palette;
    }

    private static readonly HashSet<string> ContrastSurfaces =
    [
        "Bg", "Shell", "Surface", "Flyout", "Panel", "Card", "Card2", "Rail", "Field", "Control", "ControlHover",
        "CodeBg", "OkSoft", "WarnSoft", "BadSoft", "PrimaryFaint", "PrimarySoft", "PrimaryDim", "Dock", "DockHover",
    ];

    private static Color ContrastColor(string role) => role switch
    {
        _ when ContrastSurfaces.Contains(role) => SystemColors.WindowColor,
        "Primary" or "Primary2" or "DockActive" or "AccentMark" => SystemColors.HighlightColor,
        "PrimaryFg" or "DockActiveText" => SystemColors.HighlightTextColor,
        "AccentText" => SystemColors.HotTrackColor,
        // Text, icons, hairlines, control outlines and status colours.
        _ => SystemColors.WindowTextColor,
    };

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    /// <summary>Windows stores the app-theme preference as a DWORD; unreadable means dark.</summary>
    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch { return false; }
    }
}
