using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProxyManager.Views;

/// <summary>取显示名首字（应用列表缺省头像）。</summary>
public class FirstLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return "?";
        return s.Substring(0, 1).ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>日志级别 → 颜色。</summary>
public class LevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value as string;
        return level switch
        {
            "错误" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113)),
            "警告" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21)),
            "引擎" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 165, 250)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>空集合 → 可见（空状态提示）。</summary>
public class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var visibleWhenEmpty = parameter?.ToString() != "hide";
        return count == 0
            ? (visibleWhenEmpty ? Visibility.Visible : Visibility.Collapsed)
            : (visibleWhenEmpty ? Visibility.Collapsed : Visibility.Visible);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
