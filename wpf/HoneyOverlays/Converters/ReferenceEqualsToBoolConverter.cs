using System;
using System.Globalization;
using System.Windows.Data;

namespace HoneyOverlays.Converters;

/// <summary>
/// MultiValue-Converter: gibt true zurück wenn beide Werte ReferenceEqual sind, sonst false.
/// Pendant zu ReferenceEqualsToVisibilityConverter, aber für Bool-DataTriggers.
/// </summary>
public class ReferenceEqualsToBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes,
                                object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}