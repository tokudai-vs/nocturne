using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Nocturne.App.Converters;

public class TicksToDurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long ticks && ticks > 0)
        {
            var ts = TimeSpan.FromTicks(ticks);
            if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            }
            return $"{ts.Minutes}m";
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || value == DependencyProperty.UnsetValue)
        {
            return Invert ? Visibility.Visible : Visibility.Collapsed;
        }

        bool isNullOrEmpty = value switch
        {
            string s => string.IsNullOrWhiteSpace(s),
            int i => i <= 0,
            long l => l <= 0,
            double d => d <= 0,
            float f => f <= 0,
            System.Collections.ICollection c => c.Count == 0,
            _ => false
        };

        if (Invert) isNullOrEmpty = !isNullOrEmpty;
        return isNullOrEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        bool invert = Invert || (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase));
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public class RatingFormatter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float r && r > 0)
        {
            return r.ToString("0.0", CultureInfo.InvariantCulture);
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HeatmapLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Level0 = new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    static HeatmapLevelToBrushConverter()
    {
        Level0.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int level = value is int i ? i : 0;
        if (level <= 0) return Level0;

        var baseColor = Color.FromRgb(0xE5, 0xA0, 0x0D);
        if (Application.Current?.Resources["NocturneAmberColor"] is Color c)
        {
            baseColor = c;
        }

        byte alpha = level switch
        {
            1 => 0x40,
            2 => 0x80,
            3 => 0xC0,
            _ => 0xFF
        };

        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HeightRatioToBarHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = value is double r ? r : 0.05;
        double maxHeight = 100.0;
        if (parameter is string pStr && double.TryParse(pStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var customMax))
        {
            maxHeight = customMax;
        }
        return Math.Max(4.0, ratio * maxHeight);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xE5, 0xA0, 0x0D));
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            if (Application.Current?.Resources["NocturneAmberBrush"] is Brush dynamicBrush)
            {
                return dynamicBrush;
            }
            return TrueBrush;
        }

        // Return theme-adaptive high-contrast secondary brush so inactive icons are never washed out
        if (Application.Current?.TryFindResource("TextFillColorSecondaryBrush") is Brush secBrush)
        {
            return secBrush;
        }

        return FalseBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HeroBoolToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush TrueBrush = new(Color.FromRgb(0xE5, 0xA0, 0x0D));
    private static readonly SolidColorBrush FalseBrush = new(Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF));

    static HeroBoolToBrushConverter()
    {
        TrueBrush.Freeze();
        FalseBrush.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            if (Application.Current?.Resources["NocturneAmberBrush"] is Brush dynamicBrush)
            {
                return dynamicBrush;
            }
            return TrueBrush;
        }

        return FalseBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
