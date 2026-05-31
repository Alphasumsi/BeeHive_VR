using System;
using System.Globalization;

namespace HoneyOverlays.Services;

public enum InputKind
{
    Keyboard,
    HidButton,
}

/// <summary>
/// Eine normalisierte Eingabe, an die eine Aktion gebunden werden kann.
/// Keyboard: VirtualKey + Modifier (Ctrl/Shift/Alt).
/// HidButton: Geräte-ID (RawInput device name) + Button-Index (1-basiert).
///
/// String-Form (für settings.json):
///   "KB:&lt;mods&gt;:&lt;vk&gt;"         z.B. "KB:5:79"  (mods Bitmaske: 1=Ctrl 2=Shift 4=Alt)
///   "HID:&lt;deviceId&gt;:&lt;button&gt;"  deviceId Base64-escaped (kann ':' enthalten)
/// </summary>
public sealed class InputChord : IEquatable<InputChord>
{
    public InputKind Kind { get; }

    // Keyboard
    public ushort VirtualKey { get; }
    public bool Ctrl { get; }
    public bool Shift { get; }
    public bool Alt { get; }

    // HID
    public string DeviceId { get; } = "";
    public int Button { get; }

    /// <summary>Aufgelöster Geräte-Klarname (zur Belegzeit ermittelt, in
    /// settings.json persistiert). Rein kosmetisch — NICHT Teil von
    /// Equals/HashCode (Identität bleibt DeviceId+Button).</summary>
    public string DeviceName { get; } = "";

    private InputChord(InputKind kind, ushort vk, bool ctrl, bool shift, bool alt,
                       string deviceId, int button, string deviceName)
    {
        Kind = kind;
        VirtualKey = vk;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
        DeviceId = deviceId ?? "";
        Button = button;
        DeviceName = deviceName ?? "";
    }

    public static InputChord Keyboard(ushort vk, bool ctrl, bool shift, bool alt)
        => new(InputKind.Keyboard, vk, ctrl, shift, alt, "", 0, "");

    public static InputChord Hid(string deviceId, int button, string deviceName = "")
        => new(InputKind.HidButton, 0, false, false, false, deviceId, button, deviceName);

    // ---- Persistenz ----------------------------------------------------

    public string Serialize()
    {
        if (Kind == InputKind.Keyboard)
        {
            int mods = (Ctrl ? 1 : 0) | (Shift ? 2 : 0) | (Alt ? 4 : 0);
            return $"KB:{mods}:{VirtualKey}";
        }
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(DeviceId));
        var b64n = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(DeviceName));
        return $"HID:{b64}:{Button}:{b64n}";
    }

    public static InputChord? Deserialize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(':');
        try
        {
            if (parts.Length == 3 && parts[0] == "KB")
            {
                int mods = int.Parse(parts[1], CultureInfo.InvariantCulture);
                ushort vk = ushort.Parse(parts[2], CultureInfo.InvariantCulture);
                return Keyboard(vk, (mods & 1) != 0, (mods & 2) != 0, (mods & 4) != 0);
            }
            if ((parts.Length == 3 || parts.Length == 4) && parts[0] == "HID")
            {
                var dev = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                int btn = int.Parse(parts[2], CultureInfo.InvariantCulture);
                // 4. Segment (optional, ältere Bindings ohne): Geräte-Klarname.
                var name = parts.Length == 4
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]))
                    : "";
                return Hid(dev, btn, name);
            }
        }
        catch
        {
            // Korrupte Bindings ignorieren statt Crash
        }
        return null;
    }

    // ---- Anzeige -------------------------------------------------------

    /// <summary>Menschenlesbar fürs UI, z.B. "Ctrl + Shift + O" oder "Device #1 · Button 5".</summary>
    public string Describe()
    {
        if (Kind == InputKind.Keyboard)
        {
            var sb = new System.Text.StringBuilder();
            if (Ctrl) sb.Append("Ctrl + ");
            if (Shift) sb.Append("Shift + ");
            if (Alt) sb.Append("Alt + ");
            sb.Append(KeyName(VirtualKey));
            return sb.ToString();
        }
        // Persistierten Namen bevorzugen → bleibt auch bei AUSgeschaltetem
        // Gerät lesbar; sonst Live-Auflösung (→ ggf. VID/PID-Fallback).
        var devName = !string.IsNullOrWhiteSpace(DeviceName)
            ? DeviceName
            : HidDeviceNames.FriendlyName(DeviceId);
        return $"{devName} · Button {Button}";
    }

    private static string KeyName(ushort vk)
    {
        try
        {
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
            if (key != System.Windows.Input.Key.None) return key.ToString();
        }
        catch { }
        return $"VK_{vk}";
    }

    // ---- Gleichheit (Matching im Normal-Modus) -------------------------

    public bool Equals(InputChord? other)
    {
        if (other is null || other.Kind != Kind) return false;
        return Kind == InputKind.Keyboard
            ? VirtualKey == other.VirtualKey && Ctrl == other.Ctrl
              && Shift == other.Shift && Alt == other.Alt
            : Button == other.Button
              && string.Equals(DeviceId, other.DeviceId, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as InputChord);

    public override int GetHashCode() => Kind == InputKind.Keyboard
        ? HashCode.Combine(Kind, VirtualKey, Ctrl, Shift, Alt)
        : HashCode.Combine(Kind, DeviceId.ToLowerInvariant(), Button);
}
