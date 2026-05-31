// PERF-WORK DEAKTIVIERT (Chat 30.5.2026) — kompletter Inhalt eingerahmt,
// damit nichts kompiliert wird. Reaktivieren: #if false / #endif entfernen.
#if false
using System;
using System.Collections.Generic;

namespace HoneyOverlays.Services;

/// <summary>
/// Pro-Dashie Default-Tickrate (Hz für requestAnimationFrame im WebView2).
/// Wird vom <see cref="BrowserHostManager"/> beim Spawn aus der URL abgeleitet,
/// falls die Source nicht explizit <c>TickRateHz</c> gesetzt hat.
///
/// Werte tunen: hier editieren, App neu starten — gilt für alle bestehenden +
/// neue Sources gleichzeitig. Werte basieren auf Perf-HUD-Messung 30.5.2026.
///
/// Mapping läuft über Substring-Match in der URL (irdashies-URL hat den Widget-Id
/// als Pfad-Komponente — siehe <see cref="IrdashiesPreviewService.BuildUrl"/>).
/// </summary>
public static class DashiesTickRateDefaults
{
    /// <summary>
    /// Fallback wenn URL kein bekannter Widget-Id matcht (z.B. SimHub-Overlays,
    /// Custom-HTML). Aggressiv gewählt — die meisten Daten-Dashes brauchen
    /// keine hohe Rate. Hochsetzen wenn ein Overlay sichtbar stockt.
    /// </summary>
    public const int FallbackHz = 15;

    /// <summary>
    /// widget-id → Hz. Iterierte Werte (Test 30.5.2026):
    ///  - input: 60 — Steering-Graph samplet im rAF, niedriger = Aliasing/falsche Werte
    ///  - blindspotmonitor: 60 — Detection passiert im rAF, niedriger = evtl. Cars verpasst
    ///    (10 Hz nie sauber getestet — schwer reproduzierbar)
    ///  - relative/map: 5 — verifiziert OK
    ///  - standings/fuel/pitlanehelper: 5 — Datentafeln
    ///
    /// HINWEIS: 60 wirkt wie „kein Throttle" — BrowserHostManager filtert Targets >= 60
    /// und passt --tick-rate dann nicht (Wrap würde sonst Frame-Jitter erzeugen).
    /// </summary>
    private static readonly Dictionary<string, int> ByWidgetId =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["input"]            = 60,
        ["blindspotmonitor"] = 60,
        ["relative"]         = 5,
        ["map"]              = 5,
        ["standings"]        = 5,
        ["fuel"]             = 5,
        ["pitlanehelper"]    = 5,
    };

    /// <summary>
    /// Resolved die Default-Tickrate aus einer URL. Sucht in zwei Stellen:
    ///  1. Query-Param "widget" (irdashies-Format: <c>...?widget=relative</c>)
    ///  2. Pfad-Segmente (für hypothetische REST-Style-URLs)
    /// Exakter Match (case-insensitive) — kein Substring, damit z.B. „map" nicht
    /// in „mappings" matcht.
    /// </summary>
    public static int Resolve(string url)
    {
        if (string.IsNullOrEmpty(url)) return FallbackHz;

        Uri? uri = null;
        try { uri = new Uri(url, UriKind.Absolute); } catch { /* fall through */ }

        // 1. Query-String: ?widget=<id> (irdashies-Format)
        if (uri != null && !string.IsNullOrEmpty(uri.Query))
        {
            // Query startet mit '?', danach key=value-Paare via &.
            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, eq));
                if (!key.Equals("widget", StringComparison.OrdinalIgnoreCase)) continue;
                var value = Uri.UnescapeDataString(pair.Substring(eq + 1));
                if (ByWidgetId.TryGetValue(value, out var hz))
                    return hz;
            }
        }

        // 2. Pfad-Segmente (Fallback für REST-Style oder andere URL-Schemas).
        var path = uri?.AbsolutePath ?? url;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (ByWidgetId.TryGetValue(seg, out var hz))
                return hz;
        }
        return FallbackHz;
    }
}
#endif
