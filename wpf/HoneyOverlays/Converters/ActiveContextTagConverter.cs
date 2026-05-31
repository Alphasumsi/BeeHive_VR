using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HoneyOverlays.ViewModels;

namespace HoneyOverlays.Converters;

/// <summary>
/// Liefert die P/Q/R/T/S-Pille fürs aktive Layout, abhängig vom OverlayContext.
/// Inputs (MultiBinding): item (CarLayoutViewModel), activeLayout, currentOverlayContext.
/// ConverterParameter:
///   "text"  → string: "" / "P" / "Q" / "R" / "T" / "S"
///   "vis"   → Visibility: Visible wenn aktiv UND Context bekannt, sonst Collapsed
///   default → wie "text".
/// </summary>
public class ActiveContextTagConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType,
                          object? parameter, CultureInfo culture)
    {
        var mode = parameter as string ?? "text";

        if (values is not { Length: >= 3 })
            return mode == "vis" ? Visibility.Collapsed : string.Empty;

        var item = values[0];
        var active = values[1];
        var isActive = item != null && ReferenceEquals(item, active);

        var ctx = values[2] is MainViewModel.OverlayContext c
            ? c
            : MainViewModel.OverlayContext.None;

        var letter = (isActive && ctx != MainViewModel.OverlayContext.None)
            ? ctx switch
            {
                MainViewModel.OverlayContext.Practice => "P",
                MainViewModel.OverlayContext.Qualify => "Q",
                MainViewModel.OverlayContext.Race => "R",
                MainViewModel.OverlayContext.TestDrive => "T",
                MainViewModel.OverlayContext.Spotter => "S",
                _ => string.Empty
            }
            : string.Empty;

        if (mode == "vis")
            return string.IsNullOrEmpty(letter) ? Visibility.Collapsed : Visibility.Visible;
        return letter;
    }

    public object[] ConvertBack(object value, Type[] targetTypes,
                                object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
