using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Nodes;
using System.Web;
using System.Windows.Input;
using System.Xml.Linq;
using BeeHiveVR.Models;
using BeeHiveVR.Services;

namespace BeeHiveVR.ViewModels;

/// <summary>
/// ViewModel für eine einzelne Source. Spiegelt SourceModel und macht
/// alle Properties bindbar. [ObservableProperty] generiert automatisch
/// die Properties + INotifyPropertyChanged.
/// </summary>
public partial class SourceViewModel : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private SourceType _type = SourceType.Browser;
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private bool _visible = true;

    // VR-Platzierung
    [ObservableProperty] private float _x = 0.0f;
    [ObservableProperty] private float _y = 0.0f;
    [ObservableProperty] private float _z = -0.8f;
    [ObservableProperty] private float _yaw = 0.0f;
    [ObservableProperty] private float _pitch = 0.0f;
    [ObservableProperty] private float _scale = 0.10f;
    [ObservableProperty] private float _opacity = 1.0f;

    // Browser-Sources: Pixelgröße des browser-host-Fensters. 0 = Auto.
    [ObservableProperty] private int _pixelWidth = 0;
    [ObservableProperty] private int _pixelHeight = 0;

    /// <summary>Optionale Variant-Id (Vorbereitung Multi-Variant-Support).
    /// Heute nicht gelesen; null/leer = Default-Config.</summary>
    [ObservableProperty] private string? _variantId;

    // UI-Zustand (nicht in JSON gespeichert)
    [ObservableProperty] private bool _isExpanded = false;
    [ObservableProperty] private bool _isRenaming = false;

    /// <summary>Reiner UI-State: true während die Karte per Drag verschoben wird
    /// (Amber-Hervorhebung, wie in Dashies). Nicht persistiert.</summary>
    [ObservableProperty] private bool _isDragging = false;

    /// <summary>UI-State: true während diese Source aktiv im Place-in-VR-Modus
    /// platziert wird. Steuert den Button-State (Place → Save/Cancel) in der
    /// Source-Karte. Nicht persistiert.</summary>
    [ObservableProperty] private bool _isPlacing = false;

    /// <summary>Phase 3 (5.6.2026): true wenn der Layer-Aim auf dieser Source
    /// stabilisiert ist (Hover ≥ 150 ms) ODER aktuell gegrabbt wird. UI-State
    /// für die Source-Listen-Pille (Highlight), nicht persistiert.</summary>
    [ObservableProperty] private bool _isHighlighted = false;

    // ---- Dashies-Background-Opacity (Per-Widget global in irdashies-config.json) ----
    // IsDashie + DashieWidgetId leiten sich aus Target ab (URL ?widget=<id>).
    // DashieBgOpacity liest/schreibt direkt in den IrdashiesConfigStore — der
    // Slider in der LayoutPage-Source-Card patcht damit dieselbe Config wie früher
    // der DashiesPage-Slider, und broadcastet `dashboardUpdated` für Live-Update
    // in allen Atlas-iframes + Preview.
    public bool IsDashie
    {
        get
        {
            var id = ParseWidgetIdFromTarget();
            // TrackMap hat keinen Background-CSS-Container — Slider wäre wirkungslos.
            return id != null && id != "map";
        }
    }

    public string? DashieWidgetId => ParseWidgetIdFromTarget();

    private string? ParseWidgetIdFromTarget()
    {
        if (string.IsNullOrEmpty(Target) ||
            !Target.Contains("/dashie.html", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var uri = new Uri(Target);
            return HttpUtility.ParseQueryString(uri.Query)["widget"];
        }
        catch { return null; }
    }

    public float DashieBgOpacity
    {
        get
        {
            var widgetId = DashieWidgetId;
            if (widgetId == null) return 0f;
            var cfg = IrdashiesConfigStore.Instance.GetWidgetConfig(widgetId);
            try
            {
                if (cfg?["background"]?["opacity"] is JsonNode op)
                    return (float)(op.GetValue<double>() / 100.0);
            }
            catch { }
            return 0f;
        }
        set
        {
            var widgetId = DashieWidgetId;
            if (widgetId == null) return;
            int pct = (int)System.Math.Round(System.Math.Clamp(value, 0f, 1f) * 100f);
            IrdashiesConfigStore.Instance.PatchWidgetConfig(widgetId, cfg =>
            {
                if (cfg["background"] is not JsonObject bg)
                {
                    bg = new JsonObject();
                    cfg["background"] = bg;
                }
                bg["opacity"] = JsonValue.Create(pct);
            });
            IrdashiesAdapterService.Instance.BroadcastDashboardUpdated();
            OnPropertyChanged();
        }
    }

    partial void OnTargetChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashie));
        OnPropertyChanged(nameof(DashieWidgetId));
        OnPropertyChanged(nameof(DashieBgOpacity));
    }

    // Backup vom Namen für Cancel-Funktion (Esc beim Editieren)
    private string _nameBackup = "";

    /// <summary>Startet den Edit-Modus für den Namen (Doppelklick).</summary>
    public void BeginRename()
    {
        _nameBackup = Name;
        IsRenaming = true;
    }

    /// <summary>Bestätigt den neuen Namen (Enter oder LostFocus).</summary>
    public void CommitRename()
    {
        // Leeren Namen nicht zulassen — fallback zum alten
        if (string.IsNullOrWhiteSpace(Name))
            Name = _nameBackup;
        IsRenaming = false;
    }

    /// <summary>Verwirft den Edit-Modus und stellt den alten Namen wieder her (Escape).</summary>
    public void CancelRename()
    {
        Name = _nameBackup;
        IsRenaming = false;
    }

    /// <summary>Erzeugt ein VM aus einem Model (für Mock-Daten + JSON-Loader).</summary>
    public static SourceViewModel FromModel(SourceModel m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Type = m.Type,
        Target = m.Target,
        Visible = m.Visible,
        X = m.X,
        Y = m.Y,
        Z = m.Z,
        Yaw = m.Yaw,
        Pitch = m.Pitch,
        Scale = m.Scale,
        Opacity = m.Opacity,
        PixelWidth = m.PixelWidth,
        PixelHeight = m.PixelHeight,
        VariantId = m.VariantId,
    };

    /// <summary>Schreibt das VM zurück in ein Model (für späteren Save).</summary>
    public SourceModel ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Type = Type,
        Target = Target,
        Visible = Visible,
        X = X,
        Y = Y,
        Z = Z,
        Yaw = Yaw,
        Pitch = Pitch,
        Scale = Scale,
        Opacity = Opacity,
        PixelWidth = PixelWidth,
        PixelHeight = PixelHeight,
        VariantId = VariantId,
    };
}