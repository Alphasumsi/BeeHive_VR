using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HoneyOverlays.Converters;

/// <summary>
/// Slider-Wert → gerundeter Wert + Einheit aus ConverterParameter (z.B. "px", "%").
/// Für wertbasierte Slider, wo der echte Wert klarer ist als der Bereichsanteil.
/// </summary>
public class SliderUnitConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (value is not double d) return string.Empty;
        var unit = parameter as string ?? string.Empty;
        return ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture) + unit;
    }

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Slider-Wert → Prozent seines Bereichs (0–100 %). MultiBinding-Reihenfolge:
/// [0]=Value, [1]=Minimum, [2]=Maximum. Ergibt z.B. "60%".
/// </summary>
public class SliderPercentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (values.Length < 3
            || values[0] is not double v
            || values[1] is not double min
            || values[2] is not double max
            || max <= min)
            return "0%";
        var pct = (int)Math.Round((v - min) / (max - min) * 100.0);
        return pct + "%";
    }

    public object[] ConvertBack(object value, Type[] targetTypes,
                                object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// null → Collapsed, irgendein Wert → Visible.
/// Wird für das "Active"-Tag oben rechts verwendet.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// bool → !bool. Praktisch für IsEnabled-Bindings auf "NICHT EditingSpotter" o.ä.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>
/// Vergleicht einen Enum-Wert mit dem ConverterParameter.
/// true wenn gleich. Wird für Session-Pills (Practice/Qualify/Race)
/// und Tab-Markierung benutzt um den aktiven Eintrag zu highlighten.
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString()!.Equals(
            parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null)
            return Enum.Parse(targetType, parameter.ToString()!);
        return Binding.DoNothing;
    }
}

/// <summary>
/// Beide values nicht null UND nicht ReferenceEqual → Visible (Warnung anzeigen),
/// sonst Collapsed. Wird für den „Editing X — Active is Y"-Hinweis verwendet wenn
/// SelectedLayout ≠ ActiveLayout.
/// </summary>
public class NotReferenceEqualsToVisibilityConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return System.Windows.Visibility.Collapsed;
        if (values[0] == null || values[1] == null) return System.Windows.Visibility.Collapsed;
        return ReferenceEquals(values[0], values[1])
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes,
                                object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Multipliziert mit 100 für die UI-Anzeige, dividiert beim Zurückschreiben.
/// Wird für den Scale-Slider benutzt: intern Meter (z.B. 0.05), Slider arbeitet
/// auf einer 100-fach-Skala damit die Werte für User intuitiv bleiben.
/// </summary>
public class MultiplyBy100Converter : IValueConverter
{
    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (value is float f) return (double)(f * 100);
        if (value is double d) return d * 100;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
    {
        double n;
        if (value is double d) n = d;
        else if (value is float f) n = f;
        else if (value is int i) n = i;
        else if (value is string s && double.TryParse(s, NumberStyles.Any,
                                                     CultureInfo.InvariantCulture, out var parsed)) n = parsed;
        else if (value is string s2 && double.TryParse(s2, NumberStyles.Any, culture, out var parsed2)) n = parsed2;
        else return DependencyProperty.UnsetValue;
        return (float)(n / 100);
    }
}

/// <summary>
/// Engine-Match-Status (bool?) → Brush für das Source-Status-Badge.
/// true = grün (live), false = rot (nicht gefunden), null = grau (kein Status).
/// </summary>
public class MatchStatusToBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.SolidColorBrush Live =
        new(System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly System.Windows.Media.SolidColorBrush Missing =
        new(System.Windows.Media.Color.FromRgb(0xE5, 0x53, 0x4B));
    private static readonly System.Windows.Media.SolidColorBrush Unknown =
        new(System.Windows.Media.Color.FromRgb(0x6E, 0x76, 0x81));

    public object Convert(object? value, Type targetType,
                          object? parameter, CultureInfo culture)
        => value switch
        {
            true => Live,
            false => Missing,
            _ => Unknown,
        };

    public object ConvertBack(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Vergleicht zwei Objekte per Reference-Equality.
/// values[0] == values[1] → Visible, sonst Collapsed.
/// Wird benutzt um das Pin-Icon nur am aktiven Layout zu zeigen.
/// </summary>
public class ReferenceEqualsToVisibilityConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return System.Windows.Visibility.Collapsed;
        return ReferenceEquals(values[0], values[1])
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes,
                                object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}