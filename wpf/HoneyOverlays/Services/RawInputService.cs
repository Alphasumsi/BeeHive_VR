using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace HoneyOverlays.Services;

/// <summary>
/// Globaler Eingabe-Hook via Raw Input (WM_INPUT). Fängt Tastatur + HID-Joystick/
/// Gamepad/ButtonBox auch wenn die App NICHT im Vordergrund ist (RIDEV_INPUTSINK),
/// damit Keybinds im VR / mit fokussiertem iRacing funktionieren.
///
/// Zwei Modi:
///  - Normal: erkannte <see cref="InputChord"/> → <see cref="Resolver"/> → Dispatch.
///  - Capture: nächste Eingabe wird über <see cref="ChordCaptured"/> gemeldet
///             (zum Belegen in der UI), kein Dispatch.
/// </summary>
public sealed class RawInputService
{
    private static readonly Lazy<RawInputService> _instance = new(() => new RawInputService());
    public static RawInputService Instance => _instance.Value;
    private RawInputService() { }

    /// <summary>Liefert zu einer Eingabe die gebundene Aktion (oder null). Wird vom Binding-Manager gesetzt.</summary>
    public Func<InputChord, KeybindAction?>? Resolver { get; set; }

    /// <summary>Wird im Capture-Modus mit der erfassten Eingabe gefeuert (UI-Thread).</summary>
    public event EventHandler<InputChord>? ChordCaptured;

    private HwndSource? _src;
    private bool _capturing;

    private readonly HashSet<ushort> _downKeys = new();
    private readonly Dictionary<IntPtr, HashSet<int>> _hidPrev = new();
    private readonly Dictionary<IntPtr, (string name, byte[] preparsed)> _devCache = new();

    public void BeginCapture() => _capturing = true;
    public void CancelCapture() => _capturing = false;

    public void Start()
    {
        if (_src != null) return;
        try
        {
            var p = new HwndSourceParameters("HoneyOverlaysRawInput")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            _src = new HwndSource(p);
            _src.AddHook(WndProc);

            var rid = new RAWINPUTDEVICE[3];
            rid[0] = Dev(0x01, 0x06); // Keyboard
            rid[1] = Dev(0x01, 0x04); // Joystick
            rid[2] = Dev(0x01, 0x05); // Gamepad
            if (!RegisterRawInputDevices(rid, (uint)rid.Length,
                    (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                Logger.Warn($"RawInput: RegisterRawInputDevices fehlgeschlagen (Err {Marshal.GetLastWin32Error()})");
            }
            else
            {
                Logger.Info("RawInput: global keyboard + HID hook aktiv");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("RawInput: Start fehlgeschlagen", ex);
        }
    }

    public void Stop()
    {
        try
        {
            _src?.RemoveHook(WndProc);
            _src?.Dispose();
        }
        catch { }
        _src = null;
    }

    private RAWINPUTDEVICE Dev(ushort page, ushort usage) => new()
    {
        UsagePage = page,
        Usage = usage,
        Flags = RIDEV_INPUTSINK,
        hwndTarget = _src!.Handle,
    };

    // ---- Message-Pump --------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        try { HandleRawInput(lParam); }
        catch (Exception ex) { Logger.Warn($"RawInput: WM_INPUT-Fehler: {ex.Message}"); }
        return IntPtr.Zero;
    }

    private void HandleRawInput(IntPtr lParam)
    {
        uint size = 0;
        uint hdrSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hdrSize) != 0 || size == 0)
            return;

        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, hdrSize) != size) return;

            uint type = (uint)Marshal.ReadInt32(buf, 0);
            IntPtr hDevice = Marshal.ReadIntPtr(buf, 8); // x64: dwType(4)+dwSize(4)+hDevice(8)
            int headerLen = (int)hdrSize;                // x64: 24

            if (type == RIM_TYPEKEYBOARD)
                HandleKeyboard(buf, headerLen);
            else if (type == RIM_TYPEHID)
                HandleHid(buf, headerLen, hDevice);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private void HandleKeyboard(IntPtr buf, int off)
    {
        // RAWKEYBOARD: MakeCode(0,u16) Flags(2,u16) Reserved(4,u16) VKey(6,u16) Message(8,u32) Extra(12,u32)
        ushort flags = (ushort)Marshal.ReadInt16(buf, off + 2);
        ushort vk = (ushort)Marshal.ReadInt16(buf, off + 6);
        bool up = (flags & RI_KEY_BREAK) != 0;

        if (vk == 0 || vk == 0xFF) return;
        if (IsModifier(vk)) return; // Bare Modifier nicht binden

        if (up)
        {
            _downKeys.Remove(vk);
            return;
        }
        if (!_downKeys.Add(vk)) return; // Auto-Repeat unterdrücken

        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

        Emit(InputChord.Keyboard(vk, ctrl, shift, alt));
    }

    private void HandleHid(IntPtr buf, int off, IntPtr hDevice)
    {
        // RAWHID: dwSizeHid(off+0,u32) dwCount(off+4,u32) bRawData(off+8)
        uint sizeHid = (uint)Marshal.ReadInt32(buf, off + 0);
        uint count = (uint)Marshal.ReadInt32(buf, off + 4);
        if (sizeHid == 0 || count == 0) return;

        if (!TryGetDevice(hDevice, out var dev)) return;

        var pressed = new HashSet<int>();
        for (uint r = 0; r < count; r++)
        {
            IntPtr report = buf + off + 8 + (int)(r * sizeHid);
            CollectButtons(dev.preparsed, report, sizeHid, pressed);
        }

        if (!_hidPrev.TryGetValue(hDevice, out var prev)) prev = new HashSet<int>();

        // FriendlyName liefert bei busy/unreachable Devices nur den VID/PID-Fallback
        // ("HID VID_xxxx PID_xxxx") — diesen NICHT persistieren, sonst frieren wir ihn
        // ein. Leer übergeben → beim nächsten Restart heilt KeybindManager.Load() nach.
        var fn = HidDeviceNames.FriendlyName(dev.name);
        if (string.IsNullOrEmpty(fn) || fn.StartsWith("HID ", System.StringComparison.Ordinal))
            fn = "";

        foreach (var b in pressed)
            if (!prev.Contains(b))
                Emit(InputChord.Hid(dev.name, b, fn));
        _hidPrev[hDevice] = pressed;
    }

    private static void CollectButtons(byte[] preparsed, IntPtr report, uint reportLen,
                                       HashSet<int> outButtons)
    {
        const ushort BUTTON_PAGE = 0x09;
        int maxLen = HidP_MaxUsageListLength(HIDP_INPUT, BUTTON_PAGE, preparsed);
        if (maxLen <= 0) return;

        var usages = new ushort[maxLen];
        int len = maxLen;
        int rc = HidP_GetUsages(HIDP_INPUT, BUTTON_PAGE, 0, usages, ref len,
                                preparsed, report, reportLen);
        if (rc != HIDP_STATUS_SUCCESS) return;
        for (int i = 0; i < len; i++)
            outButtons.Add(usages[i]); // Usage = Button-Nummer (1-basiert)
    }

    private bool TryGetDevice(IntPtr hDevice, out (string name, byte[] preparsed) dev)
    {
        if (_devCache.TryGetValue(hDevice, out dev)) return dev.preparsed.Length > 0;

        string name = GetDeviceName(hDevice);
        byte[] pre = GetPreparsed(hDevice);
        dev = (name, pre);
        _devCache[hDevice] = dev;
        return pre.Length > 0;
    }

    private static string GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size) != 0 || size == 0)
            return hDevice.ToString();
        var p = Marshal.AllocHGlobal((int)(size * 2));
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, p, ref size) > 0)
                return Marshal.PtrToStringAnsi(p) ?? hDevice.ToString();
            return hDevice.ToString();
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static byte[] GetPreparsed(IntPtr hDevice)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0)
            return Array.Empty<byte>();
        var p = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, p, ref size) == unchecked((uint)-1))
                return Array.Empty<byte>();
            var b = new byte[size];
            Marshal.Copy(p, b, 0, (int)size);
            return b;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private void Emit(InputChord chord)
    {
        if (_capturing)
        {
            _capturing = false;
            try { ChordCaptured?.Invoke(this, chord); }
            catch (Exception ex) { Logger.Warn($"RawInput: ChordCaptured-Handler-Fehler: {ex.Message}"); }
            return;
        }
        var action = Resolver?.Invoke(chord);
        if (action.HasValue)
            KeybindService.Instance.Dispatch(action.Value);
    }

    private static bool IsModifier(ushort vk)
        => vk is 0x10 or 0x11 or 0x12          // SHIFT / CONTROL / MENU
              or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5  // L/R variants
              or 0x5B or 0x5C;                 // L/R Win

    // ---- Win32 ---------------------------------------------------------

    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIM_TYPEHID = 2;
    private const ushort RI_KEY_BREAK = 0x01;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const int HIDP_INPUT = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand,
        IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("hid.dll")]
    private static extern int HidP_MaxUsageListLength(int reportType, ushort usagePage,
        byte[] preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage,
        ushort linkCollection, ushort[] usageList, ref int usageLength,
        byte[] preparsedData, IntPtr report, uint reportLength);
}
