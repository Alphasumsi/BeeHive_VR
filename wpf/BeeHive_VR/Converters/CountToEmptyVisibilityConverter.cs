using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BeeHiveVR.Converters;

/// <summary>
/// Gibt Visible zurück wenn die Collection leer/null ist, sonst Collapsed.
/// Verwendet für Empty-State-Hinweise ("noch keine Einträge").
/// </summary>
public class CountToEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (value is ICollection coll)
            return coll.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Visible; // null oder unbekannt → empty
    }

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}