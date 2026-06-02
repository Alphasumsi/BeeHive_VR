using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IRSDKSharper;

namespace BeeHiveVR.Services;

/// <summary>
/// Hostet das irdashies-Frontend (statisch) und streamt iRacing-Daten über
/// WebSocket im irdashies-bridgeProxy-Protokoll. HTTP + WS auf EINEM Port.
///
/// Häppchen 2: generischer Telemetrie-Pass-through mit EIGENER IRacingSdk-
/// Instanz (~30 Hz) — die globale IRacingService (2 Hz, headset-verifizierte
/// Session/Spotter-Logik) bleibt unangetastet.
/// </summary>
public sealed class IrdashiesAdapterService
{
    private static IrdashiesAdapterService? _instance;
    public static IrdashiesAdapterService Instance => _instance ??= new IrdashiesAdapterService();

    // Bewusst nicht 3000 (irdashies-Default) / 8888 (SimHub) — kollisionsarm.
    public const int Port = 8723;

    // Vite-Build der Dashies. Wird aus dem WPF-Output-Folder geliefert
    // (BeeHive_VR.csproj kopiert WebRoot\dashies-dist\ neben die Exe).
    // ResolveWebRoot() macht den Lookup zur Laufzeit — keine Hard-Codes mehr
    // auf D:\VBdev\irdashies.
    private static string WebRoot => DashiesAssets.ResolveWebRoot();

    // Echte irdashies-Mock-Daten (für die Vorschau ohne iRacing). Als Embedded
    // Resource gebundelt — keine Laufzeit-Abhängigkeit zur irdashies-Source.
    // session.json hat volle SessionInfo, telemetry.json hat IsOnTrack=true etc.
    // → alle Widget-Gates (Session-Visibility, isDriving) passen.
    private const string MockSessionResource   = "BeeHiveVR.Assets.MockData.session.json";
    private const string MockTelemetryResource = "BeeHiveVR.Assets.MockData.telemetry.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Hüllen-/Settings-Keys camelCase. Dictionary-Keys (Telemetrie-Varnamen)
        // bleiben davon UNberührt — System.Text.Json wendet die Policy nicht auf
        // Dictionary-Keys an. Genau das wollen wir (CarIdxLapDistPct etc.).
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // Session: irdashies-Typen spiegeln iRacing-YAML 1:1 (PascalCase) — KEINE
    // Namens-Policy. Wird zu einem JsonElement serialisiert; System.Text.Json
    // schreibt JsonElement verbatim, unabhängig von der Hüllen-Policy.
    private static readonly JsonSerializerOptions SessionJsonOpts = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private sealed class Client
    {
        public required WebSocket Ws;
        public readonly SemaphoreSlim SendGate = new(1, 1);
        public int Sending; // Interlocked-Flag: telemetry-Frame droppen wenn busy
    }

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    private IRacingSdk? _sdk;
    private volatile bool _isRunning;
    private volatile byte[]? _latestTelemetryBytes;     // Snapshot für Spät-Verbinder
    private volatile object? _latestSessionElement;     // boxed JsonElement (PascalCase)

    // Mock-Mode: echte irdashies-Mock-Daten für die Vorschau (ohne iRacing).
    // Zwei voneinander unabhängige Mock-Anfragen:
    //   - Animated: Dashies-Tab Preview (Sin/Sawtooth-Animationen für Input/Fuel/BlindSpot)
    //   - Static:   VR-Layouts Edit-Mode (feste Demo-Werte, damit Overlays beim VR-Placement sichtbar sind)
    // Konfliktauflösung: Static gewinnt (siehe BuildMockTelemetry).
    // Der Loop läuft, sobald mindestens eine der beiden an ist.
    private volatile bool _animatedRequested;
    private volatile bool _staticRequested;
    private volatile string? _previewWidgetId;          // welches Widget grade in der Preview gezeigt wird (für widget-spezifische Mock-Daten)
    private volatile bool _realConnected;               // echtes iRacing verbunden → Mock pausiert
    private CancellationTokenSource? _mockCts;
    private Dictionary<string, JsonElement>? _mockBaseValues; // VarName → value-Array (aus telemetry.json)
    private object? _mockSessionElement;                       // SessionInfo (aus session.json)

    public bool MockEnabled => _animatedRequested || _staticRequested;

    private IrdashiesAdapterService() { }

    public void Start()
    {
        if (_listener != null) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        // localhost-Präfix: ohne Admin/urlacl erlaubt (127.0.0.1 verlangte eine
        // netsh-urlacl-Reservierung).
        _listener.Prefixes.Add($"http://localhost:{Port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Logger.Error($"IrdashiesAdapter: HttpListener.Start fehlgeschlagen ({ex.Message}). " +
                         $"Falls Zugriff verweigert: einmalig 'netsh http add urlacl url=http://localhost:{Port}/ user=Jeder'.");
            _listener = null;
            return;
        }

        // Eigene SDK-Instanz, schneller als die globale 2-Hz-IRacingService.
        _sdk = new IRacingSdk();
        _sdk.UpdateInterval = 2; // 60/2 = ~30 Hz
        _sdk.OnConnected += OnSdkConnected;
        _sdk.OnDisconnected += OnSdkDisconnected;
        _sdk.OnTelemetryData += OnSdkTelemetry;
        _sdk.OnSessionInfo += OnSdkSession;
        _sdk.OnException += ex => Logger.Warn($"IrdashiesAdapter: SDK-Exception: {ex.Message}");
        _sdk.Start();

        _ = Task.Run(() => ListenLoopAsync(_cts.Token));

        // BeeHive_VR (1.6.2026): solange iRacing nicht verbunden ist, treiben
        // wir die Dashies mit Static-Mock-Daten. OnSdkConnected / -Disconnected
        // togglen den Schalter automatisch.
        SetMockStatic(true);

        Logger.Info($"IrdashiesAdapter: läuft auf http://localhost:{Port}/");
    }

    public void Stop()
    {
        try { _mockCts?.Cancel(); } catch { }
        _mockCts = null;
        _animatedRequested = false;
        _staticRequested = false;
        _previewWidgetId = null;
        try { _cts?.Cancel(); } catch { }
        try { _sdk?.Stop(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _sdk = null;
        _listener = null;
        _cts = null;
        _clients.Clear();
        _latestTelemetryBytes = null;
        _latestSessionElement = null;
        Logger.Info("IrdashiesAdapter: gestoppt");
    }

    // --- iRacing-SDK-Callbacks (laufen auf SDK-Hintergrund-Tasks) ---

    private void OnSdkConnected()
    {
        _isRunning = true;
        _realConnected = true; // echtes iRacing gewinnt → Mock pausiert automatisch
        // BeeHive_VR: static-Mock-Fallback aus, sobald echtes iRacing da ist.
        SetMockStatic(false);
        BroadcastFireAndForget(new { type = "runningState", data = true });
        Logger.Info("IrdashiesAdapter: iRacing verbunden");
    }

    private void OnSdkDisconnected()
    {
        _isRunning = false;
        _realConnected = false;
        _latestTelemetryBytes = null;
        _latestSessionElement = null;
        // BeeHive_VR: kein iRacing → static-Mock an, damit die Dashies in VR
        // nicht leerlaufen (IsOnTrack=true, feste Demo-Werte).
        SetMockStatic(true);
        // Bei aktivem Mock bleibt „running" true (Mock-Loop sendet weiter).
        BroadcastFireAndForget(new { type = "runningState", data = MockEnabled });
        Logger.Info("IrdashiesAdapter: iRacing getrennt");
    }

    // --- Mock-Mode: synthetische Telemetrie für die Vorschau ---

    /// <summary>Animated-Mock (Dashies-Tab-Preview): Sin/Sawtooth-Animationen auf
    /// Input/Fuel/BlindSpot. Bei echtem iRacing pausiert der Mock automatisch.
    /// widgetId aktiviert widget-spezifische Mock-Pfade (z.B. „pitlanehelper" zyklt
    /// durch Approach/At-Pitbox/Past-Box/Pit-Exit, damit alle Sub-Sektionen sichtbar
    /// werden).</summary>
    public void SetMock(bool on, string? widgetId = null)
    {
        bool widgetChanged = on && _previewWidgetId != widgetId;
        if (_animatedRequested == on && !widgetChanged) return;
        _animatedRequested = on;
        _previewWidgetId = on ? widgetId : null;
        UpdateMockLoop(reason: on ? $"animated AN ({widgetId ?? "?"})" : "animated AUS");
    }

    /// <summary>Static-Mock (VR-Layouts Edit-Mode): feste Demo-Werte + IsOnTrack=true,
    /// damit Overlays beim VR-Placement sichtbar bleiben. Gewinnt gegen Animated bei
    /// gleichzeitiger Aktivierung.</summary>
    public void SetMockStatic(bool on)
    {
        if (_staticRequested == on) return;
        _staticRequested = on;
        UpdateMockLoop(reason: on ? "static AN" : "static AUS");
    }

    /// <summary>Startet/Stoppt den Mock-Loop je nach kombiniertem Soll-Zustand
    /// (animated || static). Idempotent — mehrfach aktivieren startet nicht doppelt.</summary>
    private void UpdateMockLoop(string reason)
    {
        bool shouldRun = _animatedRequested || _staticRequested;

        if (shouldRun && _mockCts == null)
        {
            LoadMockData();
            // Session einmal senden (Late-Joiner-Snapshot + Broadcast) → Session-Gates passen.
            if (_mockSessionElement != null)
            {
                _latestSessionElement = _mockSessionElement;
                BroadcastFireAndForget(new { type = "sessionData", data = _mockSessionElement });
            }
            var cts = new CancellationTokenSource();
            _mockCts = cts;
            _ = Task.Run(() => MockLoopAsync(cts.Token));
            BroadcastFireAndForget(new { type = "runningState", data = true });
            Logger.Info($"IrdashiesAdapter: Mock-Mode AN ({reason})");
        }
        else if (!shouldRun && _mockCts != null)
        {
            try { _mockCts?.Cancel(); } catch { }
            _mockCts = null;
            if (!_realConnected)
                BroadcastFireAndForget(new { type = "runningState", data = false });
            Logger.Info($"IrdashiesAdapter: Mock-Mode AUS ({reason})");
        }
        else
        {
            // Bereits laufend, nur Modus-Wechsel — BuildMockTelemetry switcht automatisch.
            Logger.Info($"IrdashiesAdapter: Mock-Mode Update ({reason})");
        }
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            // Static gewinnt IMMER — auch gegen echtes iRacing (User-Use-Case: VR-Placement
            // im Cockpit, dort läuft iRacing). Animated dagegen pausiert bei iRacing, weil
            // sonst Fake-Pedale/Lenkung deine echten Inputs überschreiben würden.
            bool send = _staticRequested || !_realConnected;
            if (send)
            {
                var dict = BuildMockTelemetry(sw.Elapsed.TotalSeconds);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new { type = "telemetry", data = dict }, JsonOpts);
                SendTelemetryToAll(bytes);
            }
            try { await Task.Delay(33, ct); } catch { break; } // ~30 Hz
        }
    }

    /// <summary>Lädt die Mock-JSONs aus den Embedded Resources (einmalig).
    /// telemetry.json → Basis-Werte je Var (value-Array), session.json → SessionInfo.</summary>
    private void LoadMockData()
    {
        if (_mockBaseValues != null) return; // schon geladen
        try
        {
            var asm = typeof(IrdashiesAdapterService).Assembly;

            using (var telStream = asm.GetManifestResourceStream(MockTelemetryResource))
            {
                if (telStream != null)
                {
                    using var doc = JsonDocument.Parse(telStream);
                    var dict = new Dictionary<string, JsonElement>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object &&
                            prop.Value.TryGetProperty("value", out var val))
                        {
                            dict[prop.Name] = val.Clone();
                        }
                    }
                    _mockBaseValues = dict;
                    Logger.Info($"IrdashiesAdapter: Mock-Telemetrie geladen ({dict.Count} Vars).");
                }
                else
                {
                    _mockBaseValues = new Dictionary<string, JsonElement>();
                    Logger.Warn($"IrdashiesAdapter: Mock telemetry resource fehlt ({MockTelemetryResource}).");
                }
            }

            using (var sesStream = asm.GetManifestResourceStream(MockSessionResource))
            {
                if (sesStream != null)
                {
                    using var sdoc = JsonDocument.Parse(sesStream);
                    _mockSessionElement = sdoc.RootElement.Clone();
                }
                else
                {
                    Logger.Warn($"IrdashiesAdapter: Mock session resource fehlt ({MockSessionResource}).");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesAdapter: Mock-Daten laden fehlgeschlagen: {ex.Message}");
            _mockBaseValues ??= new Dictionary<string, JsonElement>();
        }
    }

    // Basis = echte Mock-Telemetrie (IsOnTrack etc.), animierte Kanäle drübergelegt.
    private Dictionary<string, object> BuildMockTelemetry(double t)
    {
        static object V(object arr) => new Dictionary<string, object> { ["value"] = arr };

        var result = new Dictionary<string, object>();
        if (_mockBaseValues != null)
            foreach (var kv in _mockBaseValues)
                result[kv.Key] = new Dictionary<string, object> { ["value"] = kv.Value };

        // --- Gemeinsame Visibility-Werte (animated + static): forcieren Sichtbarkeit
        //     der Widgets, die ohne diese Felder ihre eigenen Gates schließen.
        //     - IsOnTrack/IsOnTrackCar: Standings, Relative, BlindSpot
        //     - DisplayUnits: Auto-Speed-Einheit (KM/H als Default)
        //     - Pit-Road-Werte: Pitlane Helper (zeigt sonst nichts)
        result["IsOnTrack"]          = V(new[] { true });
        result["IsOnTrackCar"]       = V(new[] { true });
        result["DisplayUnits"]       = V(new[] { 1 }); // 0=MPH, 1=KM/H
        result["PlayerTrackSurface"] = V(new[] { 2 }); // 2 = on pit road
        result["OnPitRoad"]          = V(new[] { true });
        var pitRoadFlags = new bool[64];
        pitRoadFlags[4] = true; // Driver (idx 4 aus Mock-Session) in Pit Lane
        result["CarIdxOnPitRoad"]    = V(pitRoadFlags);

        // Player-Position nahe Pit-Box (DriverPitTrkPct=0.987 in Mock-Session) →
        // distanceToPit ≈ 193 m → Pitlane-Helper-Progress-Bar zeigt Countdown,
        // statt leer zu bleiben. Basis hat den Player bei 0.028 (Lap-Anfang).
        const int mockDriverIdx = 4;
        var lapPctsOverride = new float[64];
        for (int i = 0; i < lapPctsOverride.Length; i++) lapPctsOverride[i] = -1f;
        if (_mockBaseValues != null &&
            _mockBaseValues.TryGetValue("CarIdxLapDistPct", out var baseLP) &&
            baseLP.ValueKind == JsonValueKind.Array)
        {
            int n = System.Math.Min(64, baseLP.GetArrayLength());
            for (int i = 0; i < n; i++)
            {
                try { lapPctsOverride[i] = baseLP[i].GetSingle(); } catch { }
            }
        }
        lapPctsOverride[mockDriverIdx] = 0.95f;
        result["CarIdxLapDistPct"] = V(lapPctsOverride);

        // --- Static-Mode (VR-Placement): keine Animation, feste Demo-Werte für
        //     Pedale/Gang/Lenkung/Speed → User platziert das Overlay in VR-Ruhe.
        //     Static gewinnt gegen Animated bei gleichzeitiger Aktivierung.
        if (_staticRequested)
        {
            result["Throttle"]            = V(new[] { 0.4f });
            result["ThrottleRaw"]         = V(new[] { 0.4f });
            result["Brake"]               = V(new[] { 0.0f });
            result["BrakeRaw"]            = V(new[] { 0.0f });
            result["Clutch"]              = V(new[] { 1.0f });
            result["ClutchRaw"]           = V(new[] { 1.0f });
            result["Gear"]                = V(new[] { 1 });
            result["Speed"]               = V(new[] { 22.0f }); // ~80 km/h, über Pit-Limit
            result["SteeringWheelAngle"] = V(new[] { 0.1f });
            result["BrakeABSactive"]      = V(new[] { false });

            result["FuelLevel"]          = V(new[] { 45.0f });
            result["FuelLevelPct"]       = V(new[] { 0.75f });
            result["FuelUsePerHour"]     = V(new[] { 4.5f });
            result["Lap"]                = V(new[] { 5 });
            result["LapDistPct"]         = V(new[] { 0.5f });
            result["SessionTime"]        = V(new[] { 300.0 });
            result["SessionTimeTotal"]   = V(new[] { 3600.0 });
            result["SessionTimeRemain"]  = V(new[] { 3300.0 });
            result["SessionLapsRemain"]  = V(new[] { 35 });

            // BlindSpot: 3-wide an, idx 0 & 1 auf Driver-pct → Marker mittig.
            // Driver-pct = 0.95 (gleich wie shared block) → konsistent mit
            // Pitlane-Helper-Progress-Bar.
            const int stDriverIdx = 4;
            const float stDriverPct = 0.95f;
            var stLapPcts = new float[64];
            for (int i = 0; i < stLapPcts.Length; i++) stLapPcts[i] = -1f;
            if (_mockBaseValues != null &&
                _mockBaseValues.TryGetValue("CarIdxLapDistPct", out var stBaseLP) &&
                stBaseLP.ValueKind == JsonValueKind.Array)
            {
                int n = System.Math.Min(64, stBaseLP.GetArrayLength());
                for (int i = 0; i < n; i++)
                {
                    try { stLapPcts[i] = stBaseLP[i].GetSingle(); } catch { }
                }
            }
            stLapPcts[stDriverIdx] = stDriverPct;
            stLapPcts[0] = stDriverPct;
            stLapPcts[1] = stDriverPct;
            result["CarIdxLapDistPct"] = V(stLapPcts);
            result["CarLeftRight"]      = V(new[] { 4 });

            return result;
        }

        // --- Pitlane-Helper-Preview: 18-Sekunden-Zyklus durch alle Phasen, damit
        //     Pit-Box-Bar, At-Pitbox-Badge, Pit-Exit-Bar, Limiter-Warn, Early-Pitbox,
        //     Pit-Exit-Inputs und Traffic-Badge alle nacheinander sichtbar werden.
        //     Andere Widget-Previews nehmen den allgemeinen Animated-Pfad unten und
        //     setzen OnPitRoad wieder auf false. ---
        if (_previewWidgetId == "pitlanehelper")
        {
            BuildPitlanePreviewMock(result, t);
            return result;
        }

        double throttle = System.Math.Sin(t * 2.0) * 0.5 + 0.5;          // 0..1
        double brake = System.Math.Max(0, System.Math.Sin(t * 2.0 + System.Math.PI)) * 0.9; // 0..0.9
        double clutchRaw = 1.0 - System.Math.Max(0, System.Math.Sin(t * 0.7)) * 0.8;        // 1=gelöst
        int gear = (int)(t / 1.5) % 6 + 1;                               // 1..6
        double speed = throttle * 60.0 + 5.0;                            // m/s
        double steer = System.Math.Sin(t * 1.3) * 1.0;                   // rad ±1
        bool abs = brake > 0.6;

        result["Throttle"] = V(new[] { (float)throttle });
        result["ThrottleRaw"] = V(new[] { (float)throttle });
        result["Brake"] = V(new[] { (float)brake });
        result["BrakeRaw"] = V(new[] { (float)brake });
        result["Clutch"] = V(new[] { (float)clutchRaw });
        result["ClutchRaw"] = V(new[] { (float)clutchRaw });
        result["Gear"] = V(new[] { gear });
        result["Speed"] = V(new[] { (float)speed });
        result["SteeringWheelAngle"] = V(new[] { (float)steer });
        result["BrakeABSactive"] = V(new[] { abs });

        // --- Fuel-Mock: Verbrauch über simulierte Runden, damit der Fuel-Calculator
        //     Last/Avg/Min/Max/Laps-Remaining/Pit-Window berechnen kann. Ohne echtes
        //     iRacing gibt's sonst nur einen statischen Schnappschuss → leere Werte.
        //     Eine Runde alle 30 s; Verbrauch leicht variiert für unterschiedliche
        //     Avg/Min/Max. Tank 60 L reicht ~24 Runden (danach klemmt FuelLevel). ---
        static double LapCons(int i) => 2.5 + 0.3 * System.Math.Sin(i * 1.1); // ~2.2..2.8 L
        const double lapDuration = 30.0;
        const double tank = 60.0;
        double lapFloat = t / lapDuration;
        int lapNum = (int)lapFloat + 1;                 // 1-basiert
        double lapPct = lapFloat - System.Math.Floor(lapFloat);
        double cumulative = 0;
        for (int i = 1; i < lapNum; i++) cumulative += LapCons(i);
        cumulative += LapCons(lapNum) * lapPct;
        double fuel = System.Math.Max(0.3, tank - cumulative);
        double usePerHour = LapCons(lapNum) / lapDuration * 3600.0;

        result["Lap"] = V(new[] { lapNum });
        result["LapDistPct"] = V(new[] { (float)lapPct });
        result["FuelLevel"] = V(new[] { (float)fuel });
        result["FuelLevelPct"] = V(new[] { (float)(fuel / tank) });
        result["FuelUsePerHour"] = V(new[] { (float)usePerHour });
        result["OnPitRoad"] = V(new[] { false });
        result["SessionTime"] = V(new[] { t });
        result["SessionTimeTotal"] = V(new[] { 3600.0 });
        result["SessionTimeRemain"] = V(new[] { System.Math.Max(0.0, 3600.0 - t) });
        result["SessionLapsRemain"] = V(new[] { System.Math.Max(0, 40 - (lapNum - 1)) });

        // --- Blind-Spot-Mock: angelehnt an irdashies CarsPassingBothSidesAnimation
        //     (Storybook): beide Marker laufen linear synchron von -1 nach +1 in 5 s,
        //     dann Snap zurück auf -1. Bei uns über CarIdxLapDistPct gefüttert, weil
        //     unser Preview das echte Widget rendert (kann den Hook nicht umgehen).
        //
        //     Mit übersteuerter TrackLength=200 m (siehe LoadMockData) und distAhead=
        //     distBehind=5 m: maxDistAPct = 0.025. Sweep ±0.025 deckt die volle Bar
        //     ab; die 0.001-Quantisierung gibt nur noch ~4 % Bar-Höhe pro Tick → CSS-
        //     Transitions glätten zwischen Frames sauber durch.
        //
        //     Mock-Session: DriverCarIdx=4. ---
        const int driverIdx = 4;
        const double driverPct = 0.5;
        const double sweepPct = 0.025;
        const double sawPeriod = 5.0;   // 5 s pro Bar-Durchlauf (wie Storybook 0.02/50 ms)

        const double cycle = 11.0;       // 10 s Action + 1 s Pause
        const double clearDur = 1.0;
        const double primeDur = 0.2;
        double phase = t - System.Math.Floor(t / cycle) * cycle;

        int carLR;
        int leftIdx = -1, rightIdx = -1;
        double leftPct = 0, rightPct = 0;

        if (phase < clearDur)
        {
            // 1 s Pause — beide Bars aus (max 1 s Off, wie gefordert).
            carLR = 1;
        }
        else
        {
            double actionElapsed = phase - clearDur;                  // 0..10
            // Sawtooth: 0..1 in sawPeriod Sekunden, dann Snap.
            double saw = (actionElapsed % sawPeriod) / sawPeriod;
            // diff von -sweep nach +sweep linear → percent -1..+1.
            double diff = -sweepPct + saw * 2.0 * sweepPct;
            // Beide Seiten synchron (wie irdashies-Story „CarsPassingBothSides"):
            // Gegner links steigt von hinten nach vorn, Gegner rechts ebenso.
            leftPct = (driverPct + diff + 1.0) % 1.0;
            rightPct = (driverPct + diff + 1.0) % 1.0;

            if (actionElapsed < primeDur)
            {
                // Kurze CarLeft-Prime-Phase (0.2 s): nur idx 0 sichtbar → Widget
                // schnappt leftCarIdx=0 fest. idx 1 noch -1.
                carLR = 2;
                leftIdx = 0;
            }
            else
            {
                // 3-wide: beide Slots aktiv. Widget findet rightCarIdx via
                // findClosestExcluding(0) → 1 (einziger anderer valider Slot).
                carLR = 4;
                leftIdx = 0;
                rightIdx = 1;
            }
        }

        result["CarLeftRight"] = V(new[] { carLR });

        // 64 Slots (iRacing-Max) — Treiber fix, animierte Gegner, Rest = -1.
        var lapPcts = new float[64];
        for (int i = 0; i < lapPcts.Length; i++) lapPcts[i] = -1f;
        lapPcts[driverIdx] = (float)driverPct;
        if (leftIdx >= 0) lapPcts[leftIdx] = (float)leftPct;
        if (rightIdx >= 0) lapPcts[rightIdx] = (float)rightPct;
        result["CarIdxLapDistPct"] = V(lapPcts);

        // IsOnTrack/SessionNum etc. kommen aus der Basis (echte Mock-Werte).
        return result;
    }

    /// <summary>Widget-spezifischer Mock-Pfad für Pitlane Helper. Zyklt 18s durch
    /// Approach → At Pitbox → Past Box → Pit Exit. Setzt Player + 3 Traffic-Autos
    /// auf der Pit-Road, Speed schwankt um das Pit-Limit, Limiter nie engaged
    /// (= „⚠ ACTIVATE LIMITER" sichtbar), Pedale animieren für Pit-Exit-Inputs.
    /// Annahme: pitboxPct=0.987 (Mock-Session VIR), pitExitPct=0.04 (Stub-Bridge
    /// in honey-widget-entry.tsx).</summary>
    private void BuildPitlanePreviewMock(Dictionary<string, object> result, double t)
    {
        static object V(object arr) => new Dictionary<string, object> { ["value"] = arr };

        const double cyclePeriod = 18.0;
        double phase = (t % cyclePeriod) / cyclePeriod; // 0..1

        double playerPct;
        if (phase < 0.4)
        {
            // 0–7.2s: Approach 0.93 → 0.987 → Pit-Box-Bar Countdown
            playerPct = 0.93 + (0.987 - 0.93) * (phase / 0.4);
        }
        else if (phase < 0.5)
        {
            // 7.2–9s: At Pitbox → At-Pitbox-Badge, Exit-Inputs (atPitbox-Phase)
            playerPct = 0.987;
        }
        else if (phase < 0.75)
        {
            // 9–13.5s: Past Box 0.987 → wrap → 0.005
            double t1 = (phase - 0.5) / 0.25;
            double raw = 0.987 + (1.005 - 0.987) * t1;
            playerPct = raw - System.Math.Floor(raw);
        }
        else
        {
            // 13.5–18s: Anfahrt Pit-Exit 0.005 → 0.04 → Pit-Exit-Bar
            double t1 = (phase - 0.75) / 0.25;
            playerPct = 0.005 + (0.04 - 0.005) * t1;
        }

        // Player + 3 Traffic-Autos auf Pit-Road. Relativpositionen → Traffic-Badge
        // zeigt „2 ahead · 1 behind".
        const int playerIdx = 4;
        var pitRoadFlags = new bool[64];
        pitRoadFlags[playerIdx] = true;
        pitRoadFlags[0] = true;
        pitRoadFlags[1] = true;
        pitRoadFlags[2] = true;
        result["CarIdxOnPitRoad"] = V(pitRoadFlags);

        var lapPcts = new float[64];
        for (int i = 0; i < lapPcts.Length; i++) lapPcts[i] = -1f;
        if (_mockBaseValues != null &&
            _mockBaseValues.TryGetValue("CarIdxLapDistPct", out var baseLP) &&
            baseLP.ValueKind == JsonValueKind.Array)
        {
            int n = System.Math.Min(64, baseLP.GetArrayLength());
            for (int i = 0; i < n; i++)
            {
                try { lapPcts[i] = baseLP[i].GetSingle(); } catch { }
            }
        }
        lapPcts[playerIdx] = (float)playerPct;
        lapPcts[0] = (float)((playerPct - 0.005 + 1.0) % 1.0);
        lapPcts[1] = (float)((playerPct + 0.005) % 1.0);
        lapPcts[2] = (float)((playerPct + 0.012) % 1.0);
        result["CarIdxLapDistPct"] = V(lapPcts);

        // Speed schwankt um Pit-Limit (60 kph = 16.67 m/s), damit Delta-Farbe und
        // Speed-Bar animieren.
        double speedMs = 16.67 + System.Math.Sin(t * 1.5) * 4.0; // ~12.7..20.7 m/s
        result["Speed"] = V(new[] { (float)speedMs });

        // Limiter nie engaged → „⚠ ACTIVATE LIMITER" zeigt durchgängig.
        result["EngineWarnings"]          = V(new[] { (uint)0 });
        result["dcPitSpeedLimiterToggle"] = V(new[] { false });
        result["PitstopActive"]           = V(new[] { false });

        // Pedale für Pit-Exit-Inputs-Animation.
        double throttle = 0.25 + System.Math.Max(0, System.Math.Sin(t * 1.2)) * 0.4;
        double clutch = 0.5 + System.Math.Sin(t * 0.8) * 0.5; // 0..1
        result["Throttle"]    = V(new[] { (float)throttle });
        result["ThrottleRaw"] = V(new[] { (float)throttle });
        result["Clutch"]      = V(new[] { (float)clutch });
        result["ClutchRaw"]   = V(new[] { (float)clutch });
        result["Brake"]       = V(new[] { 0.0f });
        result["BrakeRaw"]    = V(new[] { 0.0f });
        result["Gear"]        = V(new[] { 1 });
    }

    private void OnSdkTelemetry()
    {
        var data = _sdk?.Data;
        if (data == null) return;

        // Static-Mock-Override: User platziert VR-Overlays im Cockpit und will Demo-Daten
        // sehen — echte Telemetrie unterdrücken, damit Mock-Frames nicht überschrieben werden.
        if (_staticRequested) return;

        // SDK-Thread darf nicht lange blockieren → Builder + Serialize nur,
        // Versand pro Client fire-and-forget mit latest-wins-Drop.
        var telemetry = BuildTelemetry(data);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { type = "telemetry", data = telemetry }, JsonOpts);
        SendTelemetryToAll(bytes);
    }

    /// <summary>Telemetrie-Frame an alle offenen Clients (latest-wins, busy-Client wird
    /// übersprungen). Von echtem SDK und Mock-Loop genutzt.</summary>
    private void SendTelemetryToAll(byte[] bytes)
    {
        _latestTelemetryBytes = bytes;
        foreach (var c in _clients.Values)
        {
            if (c.Ws.State != WebSocketState.Open) continue;
            if (Interlocked.CompareExchange(ref c.Sending, 1, 0) != 0) continue;
            _ = SendRawThenClearAsync(c, bytes);
        }
    }

    private void OnSdkSession()
    {
        var info = _sdk?.Data?.SessionInfo;
        if (info == null) return;

        // Static-Mock-Override: Mock-Session (Demo-Fahrer, Demo-Strecke) soll aktiv bleiben,
        // nicht alle ~2 s vom echten SessionInfo überschrieben werden.
        if (_staticRequested) return;

        // SessionInfo → PascalCase-JSON → losgelöstes JsonElement (Clone, da
        // das JsonDocument danach disposed wird).
        try
        {
            var utf8 = JsonSerializer.SerializeToUtf8Bytes(info, SessionJsonOpts);
            using var doc = JsonDocument.Parse(utf8);
            var element = doc.RootElement.Clone();
            _latestSessionElement = element;
            BroadcastFireAndForget(new { type = "sessionData", data = element });
            // sessionData tickt zu oft fürs Log (~2 Hz) — Wert nicht diagnostisch.
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesAdapter: Session-Serialisierung fehlgeschlagen: {ex.Message}");
        }
    }

    // irdashies-Telemetry-Form: { "VarName": { "value": [...] }, ... }
    private static Dictionary<string, object> BuildTelemetry(IRacingSdkData data)
    {
        var props = data.TelemetryDataProperties;
        var result = new Dictionary<string, object>(props.Count);

        foreach (var kv in props)
        {
            var d = kv.Value;
            int n = d.Count;
            object value;

            switch (d.VarType)
            {
                case IRacingSdkEnum.VarType.Bool:
                {
                    var a = new bool[n];
                    for (int i = 0; i < n; i++) a[i] = data.GetBool(d, i);
                    value = a;
                    break;
                }
                case IRacingSdkEnum.VarType.Int:
                {
                    var a = new int[n];
                    for (int i = 0; i < n; i++) a[i] = data.GetInt(d, i);
                    value = a;
                    break;
                }
                case IRacingSdkEnum.VarType.BitField:
                {
                    var a = new uint[n];
                    for (int i = 0; i < n; i++) a[i] = data.GetBitField(d, i);
                    value = a;
                    break;
                }
                case IRacingSdkEnum.VarType.Float:
                {
                    var a = new float[n];
                    for (int i = 0; i < n; i++) a[i] = data.GetFloat(d, i);
                    value = a;
                    break;
                }
                case IRacingSdkEnum.VarType.Double:
                {
                    var a = new double[n];
                    for (int i = 0; i < n; i++) a[i] = data.GetDouble(d, i);
                    value = a;
                    break;
                }
                case IRacingSdkEnum.VarType.Char:
                default:
                {
                    // Char ist selten; als Zahlencode (irdashies erwartet number[]).
                    var a = new int[n];
                    for (int i = 0; i < n; i++) a[i] = data.GetChar(d, i);
                    value = a;
                    break;
                }
            }

            result[d.Name] = new Dictionary<string, object> { ["value"] = value };
        }

        return result;
    }

    // --- HTTP ---

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"IrdashiesAdapter: GetContext-Fehler: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(ctx, ct));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            if (ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                await HandleWebSocketAsync(wsCtx.WebSocket, ct);
            }
            else
            {
                ServeHttp(ctx);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesAdapter: Verbindungsfehler: {ex.Message}");
            try { ctx.Response.Abort(); } catch { }
        }
    }

    // Häppchen 4b: statischer irdashies-Build aus WebRoot ausliefern.
    private void ServeHttp(HttpListenerContext ctx)
    {
        var rsp = ctx.Response;
        try
        {
            var rel = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/");
            if (rel == "/" || rel == "/index.html") rel = "/index-dashboard-view.html";
            rel = rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            // Traversal-Schutz: aufgelöster Pfad MUSS unter WebRoot liegen.
            var rootFull = Path.GetFullPath(WebRoot);
            var full = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                rsp.StatusCode = 404;
                rsp.Close();
                return;
            }

            if (full.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                // Pures irdashies — KEINE Modifikation (User-Wunsch: alles
                // original). HTML nicht cachen (kein Hash im Dateinamen).
                var hb = File.ReadAllBytes(full);
                rsp.ContentType = "text/html; charset=utf-8";
                rsp.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                rsp.StatusCode = 200;
                rsp.ContentLength64 = hb.LongLength;
                rsp.OutputStream.Write(hb, 0, hb.Length);
                rsp.Close();
                return;
            }

            var bytes = File.ReadAllBytes(full);
            rsp.ContentType = GetMime(full);
            rsp.StatusCode = 200;
            rsp.ContentLength64 = bytes.LongLength;
            rsp.OutputStream.Write(bytes, 0, bytes.Length);
            rsp.Close();
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesAdapter: ServeHttp-Fehler: {ex.Message}");
            try { rsp.Abort(); } catch { }
        }
    }

    private static string GetMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".map" => "application/json",
        _ => "application/octet-stream",
    };

    // --- WebSocket ---

    private async Task HandleWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var client = new Client { Ws = ws };
        _clients[id] = client;
        Logger.Info($"IrdashiesAdapter: WS-Client verbunden ({_clients.Count} aktiv)");

        try
        {
            // Pflicht: sofort initialState (mit aktuellem Snapshot + Status).
            object? telemetrySnapshot = null;
            var snap = _latestTelemetryBytes;
            if (snap != null)
            {
                using var d = JsonDocument.Parse(snap);
                telemetrySnapshot = d.RootElement.GetProperty("data").Clone();
            }

            await SendJsonAsync(client, new
            {
                type = "initialState",
                data = new
                {
                    telemetry = telemetrySnapshot,
                    sessionData = _latestSessionElement,
                    isRunning = _isRunning,
                    dashboard = BuildDashboard(),
                    isDemoMode = false,
                }
            }, ct);

            var buf = new byte[8192];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                } while (!res.EndOfMessage);

                await HandleClientMessageAsync(client, sb.ToString(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            Logger.Info($"IrdashiesAdapter: WS-Client getrennt ({_clients.Count} aktiv)");
        }
    }

    private async Task HandleClientMessageAsync(Client c, string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            string? rid = root.TryGetProperty("requestId", out var ridEl)
                ? ridEl.GetString() : null;

            switch (type)
            {
                // DashboardProvider mit ?profile=… lädt hierüber → MUSS beantwortet
                // werden, sonst „Loading dashboard…" (5 s-Timeout → null → hängt).
                case "getDashboardForProfile":
                    await SendJsonAsync(c, new { type, requestId = rid, data = BuildDashboard() }, ct);
                    break;

                case "getDashboard":
                    await SendJsonAsync(c, new { type = "dashboard", data = BuildDashboard() }, ct);
                    break;

                // Schnell beantworten statt 5 s-Fallback (currentProfile-Gate).
                case "listProfiles":
                    await SendJsonAsync(c, new { type, requestId = rid,
                        data = new[] { new { id = "default", name = "Default" } } }, ct);
                    break;

                case "getCurrentProfile":
                    await SendJsonAsync(c, new { type, requestId = rid,
                        data = new { id = "default", name = "Default" } }, ct);
                    break;

                case "getAppVersion":
                    await SendJsonAsync(c, new { type, requestId = rid, data = "0.0.0-honey" }, ct);
                    break;

                // Sicherheitsnetz: falls der Client doch reload triggert.
                case "reloadDashboard":
                    await SendJsonAsync(c, new { type = "dashboardUpdated",
                        data = new { dashboard = BuildDashboard(), profileId = "default" } }, ct);
                    break;

                // Übrige RPCs bewusst ignoriert (Client-Fallback greift).
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesAdapter: Client-Message-Parsefehler: {ex.Message}");
        }
    }

    // --- Versand (pro Client SendGate: nie zwei SendAsync gleichzeitig auf einem Socket) ---

    private async Task SendJsonAsync(Client c, object payload, CancellationToken ct)
    {
        if (c.Ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        await c.SendGate.WaitAsync(ct);
        try
        {
            if (c.Ws.State == WebSocketState.Open)
                await c.Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally { c.SendGate.Release(); }
    }

    private async Task SendRawThenClearAsync(Client c, byte[] bytes)
    {
        await c.SendGate.WaitAsync();
        try
        {
            if (c.Ws.State == WebSocketState.Open)
                await c.Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally
        {
            c.SendGate.Release();
            Interlocked.Exchange(ref c.Sending, 0);
        }
    }

    private void BroadcastFireAndForget(object payload)
    {
        foreach (var c in _clients.Values)
            _ = SendJsonAsync(c, payload, CancellationToken.None);
    }

    // DashboardLayout kommt jetzt aus dem app-eigenen Store (liest/schreibt,
    // migriert einmalig aus %APPDATA%\irdashies). Siehe IrdashiesConfigStore.
    private static object BuildDashboard() => IrdashiesConfigStore.Instance.GetDashboardObject();

    /// <summary>Sendet das aktuelle Dashboard an alle Clients → Live-Reload nach
    /// einer Config-Änderung (z.B. Toggle im Dashies-Tab).</summary>
    public void BroadcastDashboardUpdated()
    {
        BroadcastFireAndForget(new
        {
            type = "dashboardUpdated",
            data = new
            {
                dashboard = BuildDashboard(),
                profileId = IrdashiesConfigStore.Instance.CurrentProfile,
            },
        });
    }

}
