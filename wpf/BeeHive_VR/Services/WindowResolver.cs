using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace BeeHiveVR.Services;

/// <summary>
/// Window-Capture-Entkopplung (19.7.2026): löst Fenstertitel → HWND auf und hält
/// die Zuordnung aktuell, solange Window-Sources im Layout sind.
///
/// Hintergrund: Chromiums <c>desktopCapturer</c> enumeriert Overlay-/Tool-/Layered-
/// Fenster nicht (Beweis „Bloops Overlay"), unser <c>EnumWindows</c> sieht ALLE.
/// Die HWND wandert per Layout-Push (AtlasQuadDto.Hwnd) → Atlas → WinSrc-SHM →
/// capture-host, der das Fenster nativ per WGC captured.
///
/// HWNDs sind flüchtig (Ziel-App-Neustart) → Re-Resolve-Timer (2 s, nur aktiv wenn
/// Titel überwacht werden, UI-Thread; EnumWindows kostet ~ms). Bei jeder Änderung
/// feuert <see cref="Changed"/> — der MainViewModel pusht dann das Layout neu.
///
/// Titel-Match: exakt (Ordinal), erster Treffer — gleiche Semantik wie der alte
/// desktopCapturer-Pfad, keine Verschlechterung bei Titel-Dubletten.
/// </summary>
public sealed class WindowResolver
{
    private static WindowResolver? _instance;
    public static WindowResolver Instance => _instance ??= new WindowResolver();

    private WindowResolver() { }

    /// <summary>Feuert (UI-Thread), wenn sich mindestens eine Titel→HWND-Zuordnung geändert hat.</summary>
    public event Action? Changed;

    private readonly object _gate = new();
    private readonly Dictionary<string, ulong> _cache = new(StringComparer.Ordinal);
    private HashSet<string> _watched = new(StringComparer.Ordinal);
    private DispatcherTimer? _timer;

    /// <summary>
    /// Löst einen Fenstertitel zur HWND auf (0 = nicht gefunden). Cache-gestützt;
    /// ein gecachter Wert wird per <c>IsWindow</c> validiert (Fenster zu → neu suchen).
    /// </summary>
    public ulong Resolve(string? title)
    {
        if (string.IsNullOrEmpty(title)) return 0;
        lock (_gate)
        {
            if (_cache.TryGetValue(title, out var cached) && cached != 0 &&
                IsWindow((IntPtr)cached))
                return cached;

            ulong found = FindByTitle(title);
            _cache[title] = found;
            return found;
        }
    }

    /// <summary>
    /// Setzt die überwachten Titel (= aktive Window-Sources im Layout). Startet den
    /// Re-Resolve-Timer bei Bedarf, stoppt ihn wenn leer. UI-Thread aufrufen.
    /// </summary>
    public void SetWatched(IEnumerable<string> titles)
    {
        var next = new HashSet<string>(
            titles.Where(t => !string.IsNullOrEmpty(t)), StringComparer.Ordinal);
        lock (_gate) { _watched = next; }

        if (next.Count == 0)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
                Logger.Info("WindowResolver: watch stopped (keine Window-Sources)");
            }
            return;
        }
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Logger.Info($"WindowResolver: watch gestartet ({next.Count} Titel, 2 s)");
        }
    }

    private void Tick()
    {
        bool changed = false;
        List<string> titles;
        lock (_gate) { titles = _watched.ToList(); }
        foreach (var t in titles)
        {
            ulong before;
            lock (_gate) { _cache.TryGetValue(t, out before); }
            // Cache umgehen wenn das Fenster weg ist; Resolve validiert selbst.
            ulong now = Resolve(t);
            if (now != before)
            {
                Logger.Info($"WindowResolver: \"{t}\" hwnd 0x{before:x} → 0x{now:x}");
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private static ulong FindByTitle(string title)
    {
        ulong result = 0;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (string.Equals(sb.ToString(), title, StringComparison.Ordinal))
            {
                result = (ulong)hWnd;
                return false; // erster Treffer gewinnt
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ---- Win32 -----------------------------------------------------------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
