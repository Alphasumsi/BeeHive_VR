using System;
using System.Globalization;
using System.Windows.Data;

namespace BeeHiveVR.Converters;

/// <summary>
/// MultiValue-Converter: vergleicht zwei Strings auf Gleichheit, gibt Bool zurück.
/// Verwendet im SettingsSubNavButton-Style um den aktiven Sub-Tab zu erkennen.
/// </summary>
public class StringEqualsToBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        var a = values[0]?.ToString() ?? "";
        var b = values[1]?.ToString() ?? "";
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes,
                                object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}