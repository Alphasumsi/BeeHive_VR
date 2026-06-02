using System;
using System.Windows;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR.Services;

/// <summary>
/// Zentrale Stelle, die eine ausgelöste <see cref="KeybindAction"/> ausführt.
/// Input-Capture (Chunk 2) und Persistenz (Chunk 3) hängen sich hier ein;
/// dieser Service kennt nur "Aktion → was passiert".
/// </summary>
public sealed class KeybindService
{
    private static readonly Lazy<KeybindService> _instance = new(() => new KeybindService());
    public static KeybindService Instance => _instance.Value;

    private KeybindService() { }

    // Place-in-VR Toggle-State. Welches Overlay platziert wird, entscheidet
    // die Engine per Controller-Ray. Nur auf UI-Thread (BeginInvoke) berührt.
    private bool _placeModeOn;

    /// <summary>
    /// Führt die Aktion aus. Threadsicher aufrufbar (z.B. aus dem Input-Hook) —
    /// marshallt selbst auf den UI-Thread.
    /// </summary>
    public void Dispatch(KeybindAction action)
    {
        var app = Application.Current;
        if (app == null) return;

        app.Dispatcher.BeginInvoke(() =>
        {
            switch (action)
            {
                case KeybindAction.ToggleOverlays:
                    if (app.MainWindow?.DataContext is MainViewModel vm)
                    {
                        vm.ToggleOverlaysVisibleCommand.Execute(null);
                        Logger.Info("Keybind: Toggle overlays visible");
                    }
                    break;

                case KeybindAction.RecenterVr:
                    EngineLink.Instance.PushRecenter();
                    Logger.Info("Keybind: Recenter VR — Kommando an Engine gesendet");
                    break;

                case KeybindAction.PlaceInVr:
                    _placeModeOn = !_placeModeOn;
                    EngineLink.Instance.PushPlaceMode(_placeModeOn);
                    Logger.Info($"Keybind: Place in VR {(_placeModeOn ? "ON" : "OFF")}");
                    break;

                case KeybindAction.PlaceCycle:
                    EngineLink.Instance.PushPlaceCycle();
                    Logger.Info("Keybind: Place cycle → next overlay");
                    break;
            }
        });
    }
}
