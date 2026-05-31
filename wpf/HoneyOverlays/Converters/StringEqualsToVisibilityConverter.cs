using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HoneyOverlays.Converters;

/// <summary>
/// Vergleicht den Bind-Wert (string) mit dem ConverterParameter (string).
/// Gibt Visible zurück wenn gleich, sonst Collapsed.
/// Beispiel: ActiveSection="Layout" + ConverterParameter="Layout" → Visible.
/// </summary>
public class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        var v = value?.ToString() ?? "";
        var p = parameter?.ToString() ?? "";
        return string.Equals(v, p, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}