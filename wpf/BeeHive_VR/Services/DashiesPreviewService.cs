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

    /// <summary>
    /// Baut die Per-Widget-URL gegen den lokalen Adapter. <paramref name="variantId"/>
    /// ist Vorbereitung für Multi-Variant-Support: bei null/leer wird die URL
    /// byte-identisch zur heutigen erzeugt. Sobald der Variant-Workflow live
    /// geht, hängt sich <c>&amp;variant=&lt;id&gt;</c> automatisch dran.
    /// </summary>
    public static string BuildUrl(string widgetId, string? variantId = null)
    {
        int port = IrdashiesAdapterService.Port;
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
            CloseLocked();

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
                $"--title={WindowTitle}";

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
    /// Sucht <c>browser-host.exe</c> in dieser Reihenfolge:
    ///   1. Settings-Override <c>BrowserHostExecutable</c> (Dev / Custom-Install)
    ///   2. Neben der WPF-Exe (Installer-Layout, Phase G)
    ///   3. Walk-up vom WPF-Assembly-Folder nach <c>engine\bin\x64\Release\browser-host.exe</c>
    /// </summary>
    private static string? ResolveExePath()
    {
        // 1. Settings-Override
        try
        {
            var p = SettingsStore.Current?.BrowserHostExecutable;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        }
        catch { /* SettingsStore noch nicht initialisiert — kein Blocker */ }

        var asm = Assembly.GetEntryAssembly()?.Location;
        var asmDir = string.IsNullOrEmpty(asm) ? null : Path.GetDirectoryName(asm);
        if (asmDir == null) return null;

        // 2. Sibling (Installer-Layout)
        var sibling = Path.Combine(asmDir, "browser-host.exe");
        if (File.Exists(sibling)) return sibling;

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
