using System;
using System.Globalization;
using System.Windows.Data;

namespace BeeHiveVR.Converters;

/// <summary>
/// Übersetzt den internen Section-Key in den Anzeige-Namen.
/// Der interne Key "Layout" bleibt unverändert (Bindings/Settings),
/// nur die Anzeige zeigt "VR-Layouts". Alle anderen Sections unverändert.
/// </summary>
public class SectionDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        var v = value?.ToString() ?? "";
        return v == "Layout" ? "VR-Layouts" : v;
    }

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
