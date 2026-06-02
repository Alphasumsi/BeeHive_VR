using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BeeHiveVR.Services;

namespace BeeHiveVR.ViewModels;

/// <summary>
/// Eine Zeile in Settings→Keybinds: Aktion + aktuelle Belegung + Rebind/Clear.
/// Capture läuft über RawInputService (global), Esc bricht ab.
/// </summary>
public partial class KeybindRowViewModel : ObservableObject
{
    private static KeybindRowViewModel? _activeCapture;

    public KeybindAction Action { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public bool IsActive { get; }

    [ObservableProperty] private bool _isCapturing;

    public KeybindRowViewModel(KeybindActionInfo info)
    {
        Action = info.Action;
        DisplayName = info.DisplayName;
        Description = info.Description;
        IsActive = info.IsActive;
    }

    /// <summary>Anzeige der Belegung bzw. Capture-Hinweis.</summary>
    public string BindingText
    {
        get
        {
            if (IsCapturing) return "Press a key or button…  (Esc cancels)";
            var c = KeybindManager.Instance.Get(Action);
            return c?.Describe() ?? "Not set";
        }
    }

    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(BindingText));

    [RelayCommand]
    private void Rebind()
    {
        if (IsCapturing) { CancelCapture(); return; }

        // Evtl. laufendes Capture einer anderen Zeile abbrechen
        _activeCapture?.CancelCapture();
        _activeCapture = this;

        IsCapturing = true;
        RawInputService.Instance.ChordCaptured += OnChordCaptured;
        RawInputService.Instance.BeginCapture();
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsCapturing) CancelCapture();
        KeybindManager.Instance.Clear(Action);
        OnPropertyChanged(nameof(BindingText));
    }

    private void CancelCapture()
    {
        RawInputService.Instance.ChordCaptured -= OnChordCaptured;
        RawInputService.Instance.CancelCapture();
        if (_activeCapture == this) _activeCapture = null;
        IsCapturing = false;
    }

    private void OnChordCaptured(object? sender, InputChord chord)
    {
        RawInputService.Instance.ChordCaptured -= OnChordCaptured;
        if (_activeCapture == this) _activeCapture = null;
        IsCapturing = false;

        // Esc (ohne Modifier) = abbrechen, nicht binden
        if (chord.Kind == InputKind.Keyboard && chord.VirtualKey == 0x1B
            && !chord.Ctrl && !chord.Shift && !chord.Alt)
        {
            OnPropertyChanged(nameof(BindingText));
            return;
        }

        KeybindManager.Instance.Set(Action, chord);
        OnPropertyChanged(nameof(BindingText));
        Logger.Info($"Keybind gesetzt: {DisplayName} → {chord.Describe()}");
    }
}
