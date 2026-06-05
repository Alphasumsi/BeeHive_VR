using System.Collections.Generic;

namespace BeeHiveVR.Services;

/// <summary>
/// Bindbare Aktionen. Reihenfolge = Anzeige-Reihenfolge in Settings→Keybinds.
/// </summary>
public enum KeybindAction
{
    ToggleOverlays,
    RecenterVr,
    PlaceInVr,
}

/// <summary>
/// Metadaten zu einer bindbaren Aktion. <see cref="IsActive"/> = false bedeutet:
/// bindbar, aber wirkungslos bis das zugehörige Engine-Feature existiert
/// (Recenter / Place-in-VR). UI graut solche Aktionen aus + zeigt Hinweis.
/// </summary>
public sealed class KeybindActionInfo
{
    public KeybindAction Action { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsActive { get; init; }
}

/// <summary>Statischer Katalog aller Aktionen.</summary>
public static class KeybindCatalog
{
    public static readonly IReadOnlyList<KeybindActionInfo> All = new[]
    {
        new KeybindActionInfo
        {
            Action = KeybindAction.ToggleOverlays,
            DisplayName = "Toggle overlays visible",
            Description = "Show or hide all overlays at once",
            IsActive = true,
        },
        new KeybindActionInfo
        {
            Action = KeybindAction.RecenterVr,
            DisplayName = "Recenter VR view",
            Description = "Re-anchor overlays to your current head position and yaw",
            IsActive = true,
        },
        new KeybindActionInfo
        {
            Action = KeybindAction.PlaceInVr,
            DisplayName = "Place in VR",
            Description = "Toggle VR placement mode for the selected overlay",
            IsActive = true,
        },
    };
}
