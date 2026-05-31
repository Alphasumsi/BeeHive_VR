using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HoneyOverlays.Converters;

/// <summary>
/// Konvertiert bool zu Visibility (true → Visible, false → Collapsed).
/// Mit ConverterParameter="Invert" wird die Logik invertiert.
/// Wird im XAML benutzt um Elemente abhängig von einem bool ein-/auszublenden.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}