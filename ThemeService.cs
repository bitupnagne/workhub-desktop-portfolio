using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Portfolio.OfficeSystem;

public enum ThemeMode { Light, Dark }

public static class ThemeService
{
    public static ThemeMode Current { get; private set; } = ThemeMode.Light;
    private static SystemThemeModel? _systemTheme;

    public static event Action<ThemeMode>? ThemeChanged;

    public static void Apply(ThemeMode mode)
    {
        var app = Application.Current;
        if (app == null) return;

        var newUri = mode == ThemeMode.Dark
            ? new Uri("Themes/Dark.xaml", UriKind.Relative)
            : new Uri("Themes/Light.xaml", UriKind.Relative);

        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Source != null
            && (d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)
                || d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)));

        var newDict = new ResourceDictionary { Source = newUri };
        if (existing != null)
        {
            var idx = dicts.IndexOf(existing);
            dicts[idx] = newDict;
        }
        else
        {
            dicts.Add(newDict);
        }

        Current = mode;
        ApplySystemTheme(_systemTheme);
        ThemeChanged?.Invoke(mode);
    }

    public static void Toggle() => Apply(Current == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light);

    public static void ApplySystemTheme(SystemThemeModel? theme)
    {
        if (theme == null || Application.Current == null) return;
        _systemTheme = theme;
        SetBrush("BrandBrush", theme.Brand);
        SetBrush("BrandDarkBrush", theme.BrandDark);
        SetBrush("BrandLightBrush", Current == ThemeMode.Dark ? Darken(theme.Brand, 0.22) : theme.BrandLight);
    }

    private static void SetBrush(string key, string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { }
    }

    private static string Darken(string hex, double factor)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            byte r = (byte)Math.Clamp(color.R * factor, 0, 255);
            byte g = (byte)Math.Clamp(color.G * factor, 0, 255);
            byte b = (byte)Math.Clamp(color.B * factor, 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch
        {
            return "#1E1B4B";
        }
    }
}
