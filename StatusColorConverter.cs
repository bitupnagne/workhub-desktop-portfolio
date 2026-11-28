using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Portfolio.OfficeSystem;

internal static class TokenLookup
{
    public static object Find(string key, object fallback)
        => Application.Current?.TryFindResource(key) ?? fallback;
}

public class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string status = value as string ?? "";
        return status switch
        {
            "work" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")), // Green
            "away" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")), // Orange
            "off" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3B30")),  // Vivid Red (Was Gray)
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3B30"))      // Default Red
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string color = value as string ?? "#2563EB";
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
        catch
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class MessageAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isMine) return isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        return HorizontalAlignment.Center;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class MessageBackgroundConverter : IValueConverter
{
    // 내 말풍선: 웹과 동일한 brand-light2(#E0E7FF). 상대: 흰 카드(SurfaceBrush).
    private static readonly SolidColorBrush MineBrush =
        new((Color)ColorConverter.ConvertFromString("#E0E7FF"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (bool)value
            ? MineBrush
            : TokenLookup.Find("SurfaceBrush", Brushes.White);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class MessageForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (bool)value
            ? TokenLookup.Find("Text1Brush", Brushes.Black)
            : TokenLookup.Find("Text2Brush", Brushes.Black);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InvertedBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return b ? Visibility.Collapsed : Visibility.Visible;
        if (value == null) return Visibility.Visible;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ExpandIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 펼침 화살표 회전 각도 — 오른쪽 꺾쇠를 펼치면 아래로(90도)
        return value is true ? 90.0 : 0.0;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

// Always surface-colored; accent only comes from the left 4px bar (OrgNodeBorderConverter).
public class OrgNodeBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => TokenLookup.Find("SurfaceBrush", Brushes.White);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

// Accent color used as the left 4px bar.
public class OrgNodeBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string type = value as string ?? "";
        string key = type switch
        {
            "ROOT" => "BrandBrush",
            "DIVISION" => "InfoBrush",
            "TEAM" => "Gray400Brush",
            _ => "BorderBrush"
        };
        return TokenLookup.Find(key, Brushes.Gray);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class OrgNodeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string type = value as string ?? "";
        return type switch
        {
            "ROOT" => "그룹",
            "DIVISION" => "본부",
            "TEAM" => "팀",
            _ => "조직"
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class SystemMessageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NormalMessageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
