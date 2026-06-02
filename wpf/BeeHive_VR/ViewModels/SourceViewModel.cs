using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using System.Xml.Linq;
using BeeHiveVR.Models;

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

    // Engine-Match-Status (UI-State, nicht persistiert).
    // null = kein Status (Layout nicht aktiv), true = gecaptured, false = nicht gefunden.
    [ObservableProperty] private bool? _isMatched;
    [ObservableProperty] private int _captureWidth;
    [ObservableProperty] private int _captureHeight;

    /// <summary>Kurzlabel fürs Badge.</summary>
    public string StatusText => IsMatched switch
    {
        true => "live",
        false => "nicht gefunden",
        _ => "—",
    };

    /// <summary>Detail im Tooltip (Pixelmaße nur hier, nicht in der Zeile).</summary>
    public string StatusTooltip => IsMatched switch
    {
        true => $"Quelle aktiv · {CaptureWidth}×{CaptureHeight}",
        false => "Quelle nicht gefunden — Fenstertitel / URL prüfen",
        _ => "Kein Engine-Status (Layout nicht aktiv)",
    };

    partial void OnIsMatchedChanged(bool? value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTooltip));
    }
    partial void OnCaptureWidthChanged(int value) => OnPropertyChanged(nameof(StatusTooltip));
    partial void OnCaptureHeightChanged(int value) => OnPropertyChanged(nameof(StatusTooltip));

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
    };
}