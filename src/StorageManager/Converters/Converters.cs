using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StorageManager.Services;

namespace StorageManager.Converters;

public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {Units[u]}";
    }
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => ByteFormat.Format(l),
        int i => ByteFormat.Format(i),
        ulong ul => ByteFormat.Format((long)ul),
        null => parameter as string ?? "—",
        _ => value.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? $"{d:0.0} %" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SafetyToBrushConverter : IValueConverter
{
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x9E, 0x5B)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x82, 0x0B)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Safety.Safe => Green,
        Safety.Caution => Amber,
        _ => Red,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colors a usage bar: green below 75 %, amber below 90 %, red above.</summary>
public sealed class UsageToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double d = value is double x ? x : 0;
        var c = d >= 90 ? Color.FromRgb(0xD1, 0x34, 0x38) : d >= 75 ? Color.FromRgb(0xD9, 0x82, 0x0B) : Color.FromRgb(0x00, 0x78, 0xD4);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class UtcToLocalDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime { Ticks: > 0 } dt && dt.Year > 1601 ? dt.ToLocalTime().ToString("yyyy-MM-dd") : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
