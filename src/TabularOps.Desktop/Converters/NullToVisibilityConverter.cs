using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TabularOps.Desktop.Converters;

/// <summary>Returns Collapsed when value is null, Visible when not null.</summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns Visible when value is null, Collapsed when not null.</summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns Visible when int value is 0, Collapsed otherwise.</summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns Visible when bool is true, Collapsed when false.</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Multiplies (double totalWidth) × (double ratio) to produce a pixel width.
/// Used by the partition-cell fill bar: Width = ActualWidth × FillRatio.
/// </summary>
public sealed class RatioWidthMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2
            && values[0] is double totalWidth
            && values[1] is double ratio)
        {
            return Math.Max(0, totalWidth * ratio);
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
