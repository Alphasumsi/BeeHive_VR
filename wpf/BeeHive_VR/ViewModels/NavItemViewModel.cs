using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeeHiveVR.ViewModels;

/// <summary>
/// Eine Top-Group-Position der Icon-Nav. Die Reihenfolge der Collection in
/// <see cref="MainViewModel.NavItems"/> bestimmt die Anzeigereihenfolge im
/// Nav-Bar (und im Appearance-Drag-List). Sichtbarkeit (IsVisible) und
/// Active-Highlight (IsActive) sind Live-Spiegel der MainViewModel-Werte
/// — MainViewModel hält sie über partial OnXChanged-Handler aktuell.
/// </summary>
public partial class NavItemViewModel : ObservableObject
{
    /// <summary>Schlüssel (Section-Name) — was MainViewModel.ActiveSection vergleicht.</summary>
    public string Section { get; init; } = "";

    /// <summary>Tooltip + Label in der Appearance-Drag-List.</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>Path-Data des SVG-ähnlichen Icons.</summary>
    public string IconGeometry { get; init; } = "";

    /// <summary>Dev-Akzentfarbe (Autostart/Buttonbox sind Platzhalter).</summary>
    public bool IsExperimental { get; init; }

    /// <summary>Sichtbarkeit der Icon-Nav-Zeile (Spiegel von MainViewModel.ShowX).</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>True, wenn ActiveSection == Section. Treibt den Aktiv-Look im Template.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Callback wenn IsVisible aus der UI heraus geändert wird (Drag-List-Toggle).</summary>
    public Action<NavItemViewModel>? VisibilityChanged { get; init; }

    partial void OnIsVisibleChanged(bool value) => VisibilityChanged?.Invoke(this);
}
