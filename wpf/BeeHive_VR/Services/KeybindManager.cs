using System;
using System.Collections.Generic;

namespace BeeHiveVR.Services;

/// <summary>
/// Hält die Aktion↔Eingabe-Zuordnung, lädt/speichert sie in settings.json
/// und verdrahtet den <see cref="RawInputService"/>-Resolver.
/// </summary>
public sealed class KeybindManager
{
    private static readonly Lazy<KeybindManager> _instance = new(() => new KeybindManager());
    public static KeybindManager Instance => _instance.Value;
    private KeybindManager() { }

    private readonly Dictionary<KeybindAction, InputChord> _byAction = new();

    /// <summary>Liest die Bindings aus dem Store und aktiviert den Resolver.</summary>
    public void Load()
    {
        _byAction.Clear();
        bool healed = false;
        var store = SettingsStore.Current.Keybinds;
        if (store != null)
        {
            foreach (var kv in store)
            {
                if (!Enum.TryParse<KeybindAction>(kv.Key, out var action)) continue;
                var chord = InputChord.Deserialize(kv.Value);
                if (chord == null) continue;

                // Legacy-Healing: HID-Binding ohne persistierten Gerätenamen
                // (vor A2 18.5. belegt, oder damals war Auflösung gescheitert).
                // Wenn das Gerät jetzt erreichbar ist und einen echten Produktstring
                // liefert (kein "HID VID_xxxx PID_xxxx"-Fallback), Chord ersetzen +
                // gleich persistieren — ab jetzt 4-teilig sauber.
                if (chord.Kind == InputKind.HidButton
                    && string.IsNullOrWhiteSpace(chord.DeviceName)
                    && !string.IsNullOrWhiteSpace(chord.DeviceId))
                {
                    var fresh = HidDeviceNames.FriendlyName(chord.DeviceId);
                    if (!string.IsNullOrWhiteSpace(fresh)
                        && !fresh.StartsWith("HID ", StringComparison.Ordinal))
                    {
                        chord = InputChord.Hid(chord.DeviceId, chord.Button, fresh);
                        healed = true;
                        Logger.Info($"Keybind healed: {action} → DeviceName=\"{fresh}\"");
                    }
                }
                _byAction[action] = chord;
            }
        }
        RawInputService.Instance.Resolver = Resolve;
        Logger.Info($"Keybinds geladen: {_byAction.Count} Bindung(en)");
        if (healed) Persist();
    }

    private KeybindAction? Resolve(InputChord input)
    {
        foreach (var kv in _byAction)
            if (kv.Value.Equals(input))
                return kv.Key;
        return null;
    }

    public InputChord? Get(KeybindAction action)
        => _byAction.TryGetValue(action, out var c) ? c : null;

    /// <summary>Belegt eine Aktion (überschreibt). Eine Eingabe wird vorher von
    /// anderen Aktionen gelöst, damit es keine Doppelbelegung gibt.</summary>
    public void Set(KeybindAction action, InputChord chord)
    {
        var dupes = new List<KeybindAction>();
        foreach (var kv in _byAction)
            if (kv.Key != action && kv.Value.Equals(chord))
                dupes.Add(kv.Key);
        foreach (var d in dupes) _byAction.Remove(d);

        _byAction[action] = chord;
        Persist();
    }

    public void Clear(KeybindAction action)
    {
        if (_byAction.Remove(action)) Persist();
    }

    private void Persist()
    {
        var dict = new Dictionary<string, string>();
        foreach (var kv in _byAction)
            dict[kv.Key.ToString()] = kv.Value.Serialize();
        SettingsStore.Current.Keybinds = dict;
        SettingsStore.Save();
    }
}
