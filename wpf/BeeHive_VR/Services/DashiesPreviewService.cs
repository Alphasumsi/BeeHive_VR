using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BeeHiveVR.Services;

/// <summary>
/// Spawnt die <c>browser-host.exe</c> als sichtbares Vorschaufenster für ein
/// einzelnes Dashies-Widget. Wird von der DashiesPage angesteuert:
///   1. <see cref="Show"/> startet einen frischen Prozess (alten killen) mit
///      <c>--chromeless</c> und fester <c>--title=BeeHiveVR-Preview</c>.
///      <c>--render-scale=1</c> — Pixel == CSS-Pixel, kein Zoom.
///   2. <see cref="GetContentSize"/> liest die echte aktuelle Client-Rect
///      via <c>GetClientRect</c> auf <c>Process.MainWindowHandle</c> — live,
///      egal wie oft der User das Fenster resized.
///   3. <see cref="Close"/> beendet den Prozess.
/// Single-Process-Garantie: nur eine offene Preview gleichzeitig (Show killt
/// erst den alten Prozess).
/// </summary>
public sealed class DashiesPreviewService
{
    private static DashiesPreviewService? _instance;
    public static DashiesPreviewService Instance => _instance ??= new DashiesPreviewService();

    private DashiesPreviewService() { }

    // Fester Window-Title → fester Size-Report-Pfad → Single-Process-Garantie.
    private const string WindowTitle = "BeeHiveVR-Preview";

    private Process? _process;
    private readonly object _gate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // Virtual-Screen-Bounding-Rect = der gesamte sichtbare Desktop über alle
    // Monitore (single, ultrawide, triple … alles dasselbe Rechteck). Dient nur
    // dazu, die gemerkte Position beim Öffnen in den sichtbaren Bereich zu klemmen.
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    // Widget-Id der aktuell offenen Preview — Schlüssel für die Per-Widget-
    // Positionsspeicherung beim Schließen.
    private string? _currentWidgetId;

    /// <summary>
    /// Baut die Per-Widget-URL gegen den lokalen Adapter. <paramref name="variantId"/>
    /// ist Vorbereitung für Multi-Variant-Support: bei null/leer wird die URL
    /// byte-identisch zur heutigen erzeugt. Sobald der Variant-Workflow live
    /// geht, hängt sich <c>&amp;variant=&lt;id&gt;</c> automatisch dran.
    /// </summary>
    public static string BuildUrl(string widgetId, string? variantId = null)
    {
        int port = DashieAdapterService.Port;
        var url = $"http://localhost:{port}/dashie.html?widget={widgetId}";
        if (!string.IsNullOrEmpty(variantId))
            url += $"&variant={Uri.EscapeDataString(variantId)}";
        return url;
    }

    /// <summary>
    /// Startet das Vorschaufenster für <paramref name="widgetId"/>. Eine ggf.
    /// laufende Preview wird vorher beendet (Re-Show mit anderer Größe =
    /// Prozess-Neustart, weil <c>main.cpp</c> Size-Args nur initial liest).
    /// Returnt <c>false</c> wenn <c>browser-host.exe</c> nicht auffindbar ist.
    /// </summary>
    public bool Show(string widgetId, int width = 420, int height = 240)
    {
        lock (_gate)
        {
            CloseLocked();              // speichert die Position des bisher offenen Widgets
            _currentWidgetId = widgetId; // ab jetzt gilt die neue Widget-Id für den Capture

            var exePath = ResolveExePath();
            if (exePath == null)
            {
                Logger.Warn("PreviewService.Show: browser-host.exe not found");
                return false;
            }

            // Args nach engine/browser-host/main.cpp:
            //   --url=<url>           dashie-Adapter-URL pro Widget
            //   --width/--height      Initial-Format in CSS-Pixel
            //   --render-scale=1      Pixel == CSS-Pixel (Default 2.0 wäre Zoom 2×)
            //   --chromeless          rahmenlos + WS_THICKFRAME + eigene Move/Resize-Griffe
            //   --bg=1F1535           opaker WebView2-Background — verhindert Resize-
            //                         Artefakte vom alten Frame-Buffer UND markiert die
            //                         Fläche visuell als „Preview, nicht VR-transparent".
            //                         Dunkelviolett: klar vom Atlas-Anthrazit (#101018)
            //                         abweichend, kein Konflikt mit dem Amber-Akzent.
            //   --title=<key>         fester Window-Title (Single-Process-Garantie)
            var url = BuildUrl(widgetId);
            var args =
                $"--url={url} " +
                $"--width={width} --height={height} " +
                "--render-scale=1 " +
                "--chromeless " +
                "--bg=1F1535 " +
                $"--title={WindowTitle}" +
                PositionArgs(widgetId, width, height);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = false, // GUI-Prozess; true würde SW_HIDE setzen → unsichtbar
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                };
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Logger.Warn("PreviewService.Show: Process.Start returned null");
                    return false;
                }
                Logger.Info($"PreviewService: browser-host started, pid={_process.Id}, exe={exePath}");
                ChildProcessJob.Assign(_process); // stirbt mit der WPF (auch bei hartem Kill)
            }
            catch (Exception ex)
            {
                Logger.Warn($"PreviewService.Show: Process.Start failed: {ex.Message}");
                _process = null;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Aktuelle Pixel-Größe der Preview (Client-Rect des chromeless Fensters).
    /// Live — nicht gecached — also bei jedem Aufruf der wirkliche Resize-Stand.
    /// Null wenn keine Preview läuft oder das Fenster noch nicht erzeugt ist.
    /// </summary>
    public (int Width, int Height)? GetContentSize()
    {
        lock (_gate)
        {
            if (_process == null || _process.HasExited) return null;
            // Process cached MainWindowHandle bis Refresh — vor dem Lookup
            // refreshen weil das Handle anfangs (Process.Start sofort) noch 0 ist.
            _process.Refresh();
            var hwnd = _process.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return null;
            if (!GetClientRect(hwnd, out var rc)) return null;
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) return null;
            return (w, h);
        }
    }

    /// <summary>Beendet den Preview-Prozess. Idempotent.</summary>
    public void Close()
    {
        lock (_gate) CloseLocked();
    }

    private void CloseLocked()
    {
        if (_process != null)
        {
            CapturePositionLocked();
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
            catch { /* best effort */ }
            _process = null;
        }
    }

    /// <summary>
    /// Liest die aktuelle Fenster-Position (linke obere Ecke, Screen-Pixel) und
    /// persistiert sie pro Widget. Wird vor jedem Kill aufgerufen — auch bei
    /// Resize-Neustart, damit die Position erhalten bleibt. Best-effort: fehlendes
    /// HWND oder minimiertes Fenster (Sentinel -32000) werden ignoriert.
    /// </summary>
    private void CapturePositionLocked()
    {
        if (_process == null || _process.HasExited || _currentWidgetId == null) return;
        _process.Refresh();
        var hwnd = _process.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out var rc)) return;
        if (rc.Left <= -30000 || rc.Top <= -30000) return; // minimiert → nicht speichern
        SettingsStore.Current.DashiesPreviewPos[_currentWidgetId] = new[] { rc.Left, rc.Top };
        SettingsStore.Save();
    }

    /// <summary>
    /// Liefert " --x=.. --y=.." für die zuletzt gemerkte Position dieses Widgets,
    /// in den sichtbaren Desktop geklemmt. Kein Verwerfen, keine Schwelle: liegt
    /// die Position im Bild (Normalfall), bleibt sie unverändert; hat sich die
    /// Auflösung/Anordnung geändert und das Fenster läge teils außerhalb, wird es
    /// nur in Sicht gezogen statt auf Default zu springen. "" nur wenn dieses
    /// Widget noch nie eine Position hatte → browser-host nutzt sein Default.
    /// </summary>
    private static string PositionArgs(string widgetId, int width, int height)
    {
        if (!SettingsStore.Current.DashiesPreviewPos.TryGetValue(widgetId, out var pos)
            || pos == null || pos.Length < 2)
            return "";
        int sx = pos[0], sy = pos[1];

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return $" --x={sx} --y={sy}"; // Metrics weg → ungeprüft

        // In den sichtbaren Desktop klemmen. Fenster breiter/höher als der Desktop
        // → an die linke/obere Kante (Math.Max gewinnt gegen das negative Limit).
        int cx = Math.Max(vx, Math.Min(sx, vx + vw - width));
        int cy = Math.Max(vy, Math.Min(sy, vy + vh - height));
        return $" --x={cx} --y={cy}";
    }

    /// <summary>
    /// Sucht <c>browser-host.exe</c> in dieser Reihenfolge:
    ///   1. Neben der WPF-Exe (flaches Layout)
    ///   2. Install-Unterordner engine\ (Setup-Layout %LOCALAPPDATA%\Programs\BeeHive_VR)
    ///   3. Walk-up vom WPF-Assembly-Folder nach <c>engine\bin\x64\Release\browser-host.exe</c>
    /// </summary>
    private static string? ResolveExePath()
    {
        var asm = Assembly.GetEntryAssembly()?.Location;
        var asmDir = string.IsNullOrEmpty(asm) ? null : Path.GetDirectoryName(asm);
        if (asmDir == null) return null;

        // 1. Sibling (flaches Layout)
        var sibling = Path.Combine(asmDir, "browser-host.exe");
        if (File.Exists(sibling)) return sibling;

        // 2. Install-Unterordner engine\ (Setup-Layout)
        var installed = Path.Combine(asmDir, "engine", "browser-host.exe");
        if (File.Exists(installed)) return installed;

        // 3. Walk-up bis Repo-Root, suche engine\bin\x64\Release\browser-host.exe
        var dir = asmDir;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var cand = Path.Combine(dir, "engine", "bin", "x64", "Release", "browser-host.exe");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
