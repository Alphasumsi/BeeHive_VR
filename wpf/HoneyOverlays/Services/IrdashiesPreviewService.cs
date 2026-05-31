using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HoneyOverlays.Services;

/// <summary>
/// Verwaltet EIN sichtbares Vorschau-Fenster (browser-host.exe OHNE --cloak) für
/// den Dashies-Tab. Lädt das Per-Widget-Frontend gegen den lokalen Adapter
/// (Port 8723). Ein Fenster: erneuter Show()-Aufruf killt das alte und öffnet neu.
/// </summary>
public sealed class IrdashiesPreviewService
{
    private static IrdashiesPreviewService? _instance;
    public static IrdashiesPreviewService Instance => _instance ??= new IrdashiesPreviewService();

    private Process? _proc;

    private IrdashiesPreviewService() { }

    /// <summary>Baut die Per-Widget-URL gegen den lokalen Adapter.</summary>
    public static string BuildUrl(string widgetId)
    {
        int port = IrdashiesAdapterService.Port;
        // wsUrl/profile entfallen: das Widget nimmt als WS-URL seine eigene Herkunft
        // (window.location.origin) und ohne profile das Default-Profil.
        return $"http://localhost:{port}/honeyvr.html?widget={widgetId}";
    }

    /// <summary>Öffnet (oder ersetzt) das Vorschaufenster für ein Widget.</summary>
    public bool Show(string widgetId, int width = 420, int height = 240)
    {
        Close();

        var exe = BrowserHostManager.ResolveBrowserHostPath();
        if (exe == null)
        {
            Logger.Warn("IrdashiesPreview: browser-host.exe nicht gefunden — " +
                        "Settings.BrowserHostExecutable setzen.");
            return false;
        }

        var url = BuildUrl(widgetId);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                // WICHTIG: NICHT CreateNoWindow=true — das setzt STARTUPINFO.wShowWindow
                // auf SW_HIDE, und browser-host ruft ShowWindow(hwnd, nCmdShow) → Fenster
                // bliebe unsichtbar. Fürs sichtbare Preview muss es normal angezeigt werden.
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            psi.ArgumentList.Add($"--url={url}");
            psi.ArgumentList.Add("--title=honey-dashies-preview");
            psi.ArgumentList.Add($"--width={width}");
            psi.ArgumentList.Add($"--height={height}");
            // Opaker Hintergrund (Honey BgPrimary) — sonst ist das Fenster transparent.
            psi.ArgumentList.Add("--bg=1E1E1E");
            // Chromeless: rahmenlos + resizebar (WS_THICKFRAME) + Eck-Drag-Handle.
            // Kein Titelbalken (Schließen via In-App „Close Preview"-Button).
            psi.ArgumentList.Add("--chromeless");
            psi.ArgumentList.Add($"--caption=Honey Preview — {widgetId}");

            _proc = Process.Start(psi);
            Logger.Info($"IrdashiesPreview: opened pid={_proc?.Id} widget={widgetId} url=\"{url}\"");
            return _proc != null;
        }
        catch (Exception ex)
        {
            Logger.Error($"IrdashiesPreview: spawn failed: {ex.Message}");
            return false;
        }
    }

    // browser-host rendert mit ZoomFactor 2.0 → CSS-Größe = Client-Pixel / RenderScale.
    private const double RenderScale = 2.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    /// <summary>
    /// Aktuelle Client-Größe des Vorschaufensters als CSS-Content-Größe (Client-Pixel
    /// ÷ RenderScale) — passt zur PixelWidth/PixelHeight-Konvention der Sources.
    /// null, wenn kein Fenster offen ist.
    /// </summary>
    public (int Width, int Height)? GetContentSize()
    {
        var p = _proc;
        if (p == null) return null;
        try
        {
            if (p.HasExited) return null;
            p.Refresh();
            var hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return null;
            if (!GetClientRect(hwnd, out var r)) return null;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return null;
            return ((int)Math.Round(w / RenderScale), (int)Math.Round(h / RenderScale));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Schließt das Vorschaufenster, falls offen.</summary>
    public void Close()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesPreview: close failed: {ex.Message}");
        }
        finally
        {
            try { p.Dispose(); } catch { }
        }
    }
}
