using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace HoneyOverlays.Services;

/// <summary>
/// Löst einen RawInput-Gerätepfad (\\?\HID#VID_xxxx&amp;PID_xxxx#…) in den
/// menschenlesbaren Produktnamen auf (HidD_GetProductString). Ergebnis wird
/// gecacht — der Pfad bleibt die stabile Identität fürs Binding, nur die
/// Anzeige wird hübsch.
/// </summary>
public static class HidDeviceNames
{
    private static readonly ConcurrentDictionary<string, string> _cache = new();

    public static string FriendlyName(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return devicePath ?? "";
        return _cache.GetOrAdd(devicePath, Resolve);
    }

    private static string Resolve(string path)
    {
        try
        {
            // Access = 0 → reine Abfrage, vermeidet Exklusiv-Open-Konflikte.
            IntPtr h = CreateFileW(path, 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
            if (h != INVALID_HANDLE_VALUE)
            {
                try
                {
                    var buf = new char[128];
                    if (HidD_GetProductString(h, buf, (uint)(buf.Length * 2)))
                    {
                        int len = Array.IndexOf(buf, '\0');
                        if (len < 0) len = buf.Length;
                        var name = new string(buf, 0, len).Trim();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
                finally { CloseHandle(h); }
            }
        }
        catch
        {
            // egal — Fallback unten
        }
        return VidPidFallback(path);
    }

    /// <summary>Fallback wenn kein Produktstring: VID/PID aus dem Pfad.</summary>
    private static string VidPidFallback(string id)
    {
        var up = id.ToUpperInvariant();
        int vid = up.IndexOf("VID_", StringComparison.Ordinal);
        int pid = up.IndexOf("PID_", StringComparison.Ordinal);
        if (vid >= 0 && pid >= 0 && vid + 8 <= up.Length && pid + 8 <= up.Length)
            return $"HID {up.Substring(vid, 8)} {up.Substring(pid, 8)}";
        return id.Length > 24 ? "HID …" + id[^20..] : id;
    }

    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr HidDeviceObject,
        char[] Buffer, uint BufferLength);
}
