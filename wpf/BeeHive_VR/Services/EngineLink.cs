using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BeeHiveVR.Models;

namespace BeeHiveVR.Services;

// --- Wire-Format -------------------------------------------------------------
// Jede Nachricht: 4 Byte uint32 little-endian (Länge in Bytes) + UTF-8 JSON-Body.
// JSON-Discriminator: "type"-Feld.

/// <summary>
/// One overlay's contribution to the BeeHive_VR atlas. WPF sends a list of
/// these via <see cref="EngineLink.PushAtlasLayout"/>; Electron maps each
/// entry to a QuadDesc using <see cref="Id"/> as the lookup key against its
/// rect map (Electron owns the atlas packing).
/// </summary>
public sealed class AtlasQuadDto
{
    [JsonPropertyName("id")]      public string Id { get; set; } = "";
    [JsonPropertyName("posX")]    public float PosX { get; set; }
    [JsonPropertyName("posY")]    public float PosY { get; set; }
    [JsonPropertyName("posZ")]    public float PosZ { get; set; } = -1.0f;
    [JsonPropertyName("quatX")]   public float QuatX { get; set; }
    [JsonPropertyName("quatY")]   public float QuatY { get; set; }
    [JsonPropertyName("quatZ")]   public float QuatZ { get; set; }
    [JsonPropertyName("quatW")]   public float QuatW { get; set; } = 1.0f;
    [JsonPropertyName("sizeW")]   public float SizeW { get; set; } = 0.4f;
    [JsonPropertyName("sizeH")]   public float SizeH { get; set; } = 0.3f;
    [JsonPropertyName("visible")] public bool Visible { get; set; } = true;

    /// <summary>
    /// Voll qualifizierte URL des Widgets (z.B. <c>http://localhost:8723/dashie.html?widget=relative</c>).
    /// Electron setzt damit dynamisch den Iframe-Inhalt der zugewiesenen Atlas-Region —
    /// ohne diese Angabe würde der Iframe seine hardcoded Default-URL behalten und
    /// das falsche Widget zeigen.
    /// </summary>
    [JsonPropertyName("target")]  public string? Target { get; set; }

    /// <summary>
    /// 0..1 Multiplier auf RGB+Alpha (Compute-Shader im Layer wendet das pro Quad an).
    /// Default 1.0 = voll deckend. Getrieben vom LayoutPage-Opacity-Slider.
    /// </summary>
    [JsonPropertyName("opacity")] public float Opacity { get; set; } = 1.0f;

    /// <summary>
    /// Wunsch-Pixel-Größe des Widgets im Atlas (C3b, 4.6.2026). WPF liefert
    /// hier die vom User in der Layout-Page eingestellte PixelW/H (= das was
    /// die Preview im browser-host.exe nutzt). Electron-Packer bekommt den
    /// Wunsch, sucht einen passenden Slot im Atlas, schreibt die Ist-Position
    /// (rectX/Y/W/H) ins SHM-QuadSlot — der Layer sieht nur das fertige Rect.
    /// 0 = Default (Electron entscheidet, heute 512×384).
    /// </summary>
    [JsonPropertyName("rectW")]   public int RectW { get; set; }
    [JsonPropertyName("rectH")]   public int RectH { get; set; }
}

public sealed class EngineSourceStatus
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("matched")] public bool Matched { get; set; }
    [JsonPropertyName("width")] public uint Width { get; set; }
    [JsonPropertyName("height")] public uint Height { get; set; }
}

/// <summary>
/// Engine→GUI während Place-in-VR: neue Pose/Scale/Opacity der gedraggten Source.
/// </summary>
public sealed class PlaceUpdate
{
    public string Id { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Scale { get; set; }
    /// <summary>Nullable: Layer treibt Opacity heute nicht (ALT-Drag B10 nicht
    /// implementiert). Wenn null, lässt OnPlaceUpdate die Source-Opacity in
    /// Ruhe — sonst würde jeder Place-in-VR-Drag den Slider auf 0 ziehen.</summary>
    public float? Opacity { get; set; }
}

/// <summary>
/// Named-Pipe-Server für die Verbindung WPF-GUI ↔ Engine (Layer-DLL in iRacing.exe).
///
/// Rollen-Aufteilung:
///  - GUI = Server (langlebig, immer ansprechbar)
///  - Engine = Client (wird gestartet wenn iRacing startet, connectet sich)
///
/// Lifecycle:
///  - Start() startet den Background-Accept-Loop. Akzeptiert eine Engine-Connection
///    zur Zeit (mehrere Engines = mehrere Pipe-Instanzen kommt später).
///  - Bei Disconnect → automatisch neue Connection akzeptieren.
///  - Stop() bricht den Loop ab und schließt die Pipe.
///
/// Thread-Safety:
///  - Public Push*-Methoden können vom UI-Thread aufgerufen werden.
///  - Events werden vom Background-Thread gefeuert — Consumer (MainViewModel)
///    muss bei UI-Updates auf den Dispatcher zurückmarshalln.
/// </summary>
public sealed class EngineLink
{
    private static EngineLink? _instance;
    public static EngineLink Instance => _instance ??= new EngineLink();

    // Pipe-Name follows the BeeHive_VR cross-component naming convention. The
    // counterparty is now the Electron Atlas-Renderer (not the old in-process
    // layer DLL). Memory: project_beehive_vr_pivot.
    private const string PipeName = "BeeHiveVR";

    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private Task? _writerTask;
    private NamedPipeServerStream? _currentPipe;
    private readonly ConcurrentQueue<byte[]> _writeQueue = new();
    private readonly AutoResetEvent _writeSignal = new(false);
    // Letzter setLayout-Payload — bei Slider-Drags coalescen wir Drops in der Queue
    // (kein Build-Up wenn der User schneller produziert als die Pipe entleeren kann).
    private const string LayoutMessageType = "setLayout";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsConnected { get; private set; }

    /// <summary>Engine connected/disconnected. Fired from background thread.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>Engine sendet Status-Update (z.B. Source matched/missing). Background thread.</summary>
    public event EventHandler<IReadOnlyList<EngineSourceStatus>>? StatusReceived;

    /// <summary>Engine sendet neue Pose während Place-in-VR-Drag. Background thread.</summary>
    public event EventHandler<PlaceUpdate>? PlaceUpdateReceived;

    /// <summary>
    /// Place-in-VR-Modus an/aus (gefeuert sobald die Toggle-Nachricht raus ist).
    /// Erlaubt Consumern den Echo-Push zu unterdrücken und am Ende zu flushen.
    /// </summary>
    public event EventHandler<bool>? PlaceModeChanged;

    /// <summary>Letzter via PushPlaceMode gesendeter Zustand.</summary>
    public bool IsPlaceModeOn { get; private set; }

    private EngineLink() { }

    public void Start()
    {
        if (_serverTask != null) return;
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token));
        _writerTask = Task.Run(() => WriterLoop(_cts.Token));
        Logger.Info($"EngineLink: server loop started, pipe=\\\\.\\pipe\\{PipeName}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _writeSignal.Set(); // wake writer
        try { _currentPipe?.Dispose(); } catch { }
        try { _serverTask?.Wait(2000); } catch { }
        try { _writerTask?.Wait(2000); } catch { }
        _serverTask = null;
        _writerTask = null;
        _cts = null;
    }

    public void PushLayout(IEnumerable<SourceModel> sources)
    {
        var payload = new
        {
            type = "setLayout",
            sources = sources,
        };
        SendJson(payload);
    }

    /// <summary>
    /// Pushes the BeeHive_VR atlas layout — one entry per visible overlay,
    /// matched on <see cref="AtlasQuadDto.Id"/> against Electron's static
    /// atlas-rect map. WPF is the authority on pose / size / visibility;
    /// Electron owns the rect packing.
    /// </summary>
    public void PushAtlasLayout(IEnumerable<AtlasQuadDto> quads)
    {
        var list = quads as IList<AtlasQuadDto> ?? quads.ToList();
        // ⚠ Diagnose-Log (3.6.2026): zeigt welches Target pro Quad rausgeht.
        // Wenn Target=<null> trotz vermeintlich gesetzter Source-URL → Bug
        // sitzt im VM→DTO-Pfad (BuildAtlasQuads / ToModel / FromModel).
        foreach (var q in list)
            Logger.Info($"PushAtlasLayout: id={q.Id} target={q.Target ?? "<null>"}");
        var payload = new
        {
            type = "setAtlasLayout",
            quads = list,
        };
        SendJson(payload);
    }

    public void PushMasterVisible(bool visible)
    {
        var payload = new { type = "setMasterVisible", value = visible };
        SendJson(payload);
    }

    /// <summary>Fordert die Engine auf, den Overlay-Anker auf die aktuelle Kopf-Pose zu setzen.</summary>
    public void PushRecenter()
    {
        SendJson(new { type = "recenter" });
    }

    /// <summary>
    /// Schaltet den Place-in-VR-Modus an/aus. Ohne sourceId entscheidet die Engine
    /// per Controller-Ray welches Overlay platziert wird (Point-&-Grab). Mit
    /// sourceId fixiert die Engine direkt auf die Source — z.B. wenn der User den
    /// Place-Button auf einer bestimmten Source-Karte gedrückt hat.
    /// </summary>
    public void PushPlaceMode(bool on, string? sourceId = null)
    {
        IsPlaceModeOn = on;
        if (string.IsNullOrEmpty(sourceId))
            SendJson(new { type = "placeMode", on });
        else
            SendJson(new { type = "placeMode", on, id = sourceId });
        PlaceModeChanged?.Invoke(this, on);
    }

    /// <summary>Wechselt im Place-Modus die Auswahl aufs nächste sichtbare Overlay.</summary>
    public void PushPlaceCycle()
    {
        SendJson(new { type = "placeCycle" });
    }

    // ---------------------------------------------------------------- internals

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                _currentPipe = pipe;
                IsConnected = true;
                Logger.Info("EngineLink: engine connected");
                ConnectionChanged?.Invoke(this, true);

                await ReadLoopAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"EngineLink: server loop error: {ex.Message}");
            }
            finally
            {
                _currentPipe = null;
                if (IsConnected)
                {
                    IsConnected = false;
                    Logger.Info("EngineLink: engine disconnected");
                    ConnectionChanged?.Invoke(this, false);
                }
                try { pipe?.Dispose(); } catch { }
            }
        }
        Logger.Info("EngineLink: server loop stopped");
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            // Length-Prefix
            int read = await ReadExactAsync(pipe, lenBuf, 4, ct).ConfigureAwait(false);
            if (read == 0) return; // clean EOF
            uint payloadLen = BitConverter.ToUInt32(lenBuf, 0);
            if (payloadLen == 0 || payloadLen > 16 * 1024 * 1024)
            {
                Logger.Warn($"EngineLink: invalid payload length {payloadLen}, dropping connection");
                return;
            }

            var payload = new byte[payloadLen];
            read = await ReadExactAsync(pipe, payload, (int)payloadLen, ct).ConfigureAwait(false);
            if (read == 0) return;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                switch (type)
                {
                    case "hello":
                        var app = doc.RootElement.TryGetProperty("app", out var a) ? a.GetString() : "?";
                        var ver = doc.RootElement.TryGetProperty("engineVersion", out var v) ? v.GetString() : "?";
                        Logger.Info($"EngineLink: hello from \"{app}\" engine={ver}");
                        break;
                    case "status":
                        if (doc.RootElement.TryGetProperty("sources", out var srcs) &&
                            srcs.ValueKind == JsonValueKind.Array)
                        {
                            var list = new List<EngineSourceStatus>(srcs.GetArrayLength());
                            foreach (var item in srcs.EnumerateArray())
                            {
                                list.Add(new EngineSourceStatus
                                {
                                    Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                                    Matched = item.TryGetProperty("matched", out var m) && m.GetBoolean(),
                                    Width = item.TryGetProperty("width", out var w) ? w.GetUInt32() : 0,
                                    Height = item.TryGetProperty("height", out var h) ? h.GetUInt32() : 0,
                                });
                            }
                            StatusReceived?.Invoke(this, list);
                        }
                        break;
                    case "placeUpdate":
                        {
                            var root = doc.RootElement;
                            float F(string n) => root.TryGetProperty(n, out var e) &&
                                                 e.ValueKind == JsonValueKind.Number
                                ? e.GetSingle() : 0f;
                            // FOpt liefert null wenn das Feld fehlt — wichtig für
                            // Opacity: Layer schickt es heute nicht (kein ALT-Drag),
                            // OnPlaceUpdate soll dann den User-Slider in Ruhe lassen.
                            float? FOpt(string n) => root.TryGetProperty(n, out var e) &&
                                                     e.ValueKind == JsonValueKind.Number
                                ? e.GetSingle() : (float?)null;
                            var pu = new PlaceUpdate
                            {
                                Id = root.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "",
                                X = F("x"),
                                Y = F("y"),
                                Z = F("z"),
                                Yaw = F("yaw"),
                                Pitch = F("pitch"),
                                Scale = F("scale"),
                                Opacity = FOpt("opacity"),
                            };
                            // placeUpdate kommt pro Frame während Trigger-Grab — kein Log,
                            // Werte stehen live in den Slidern und in der JSON.
                            PlaceUpdateReceived?.Invoke(this, pu);
                        }
                        break;
                    default:
                        Logger.Warn($"EngineLink: unknown message type \"{type}\"");
                        break;
                }
            }
            catch (JsonException jx)
            {
                Logger.Warn($"EngineLink: malformed JSON from engine: {jx.Message}");
            }
        }
    }

    private static async Task<int> ReadExactAsync(Stream s, byte[] buf, int n, CancellationToken ct)
    {
        int total = 0;
        while (total < n)
        {
            int got = await s.ReadAsync(buf.AsMemory(total, n - total), ct).ConfigureAwait(false);
            if (got == 0) return total == 0 ? 0 : throw new EndOfStreamException();
            total += got;
        }
        return total;
    }

    /// <summary>
    /// Serialisiert auf Caller-Thread und enqueued in den Writer-Pool.
    /// Caller (UI-Thread bei Slider-Drag) blockiert nicht auf der Pipe.
    /// </summary>
    private void SendJson(object payload)
    {
        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        }
        catch (Exception ex)
        {
            Logger.Warn($"EngineLink: serialize failed: {ex.Message}");
            return;
        }

        var frame = new byte[4 + json.Length];
        BitConverter.GetBytes((uint)json.Length).CopyTo(frame, 0);
        json.CopyTo(frame, 4);
        _writeQueue.Enqueue(frame);
        _writeSignal.Set();
    }

    /// <summary>
    /// Dedizierter Writer-Loop. Liest Frames aus der Queue und schickt sie an den
    /// aktuell verbundenen Pipe. Coalesced setLayout-Frames: wenn mehrere queued sind,
    /// behalten wir nur den letzten — bei Slider-Drag spielt nur der finale State ne Rolle,
    /// alle Zwischenstände sind veraltet sobald sie an der Reihe wären.
    /// </summary>
    private void WriterLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _writeSignal.WaitOne(500);

            // Coalesce: nur letzte setLayout-Message behalten (4 Bytes Header skip, dann
            // String.Contains auf "setLayout" — robust genug für unseren simplen Protokoll).
            var batch = new List<byte[]>();
            while (_writeQueue.TryDequeue(out var f))
            {
                batch.Add(f);
            }
            CoalesceLayoutMessages(batch);

            var pipe = _currentPipe;
            if (pipe == null || !pipe.IsConnected) continue;

            foreach (var frame in batch)
            {
                try
                {
                    pipe.Write(frame, 0, frame.Length);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"EngineLink: write failed: {ex.Message}");
                    break;
                }
            }
            try { pipe.Flush(); } catch { }
        }
    }

    private static void CoalesceLayoutMessages(List<byte[]> batch)
    {
        // Behalte alle non-setLayout-Frames in Reihenfolge; bei mehreren setLayout: nur letzten.
        int lastLayoutIdx = -1;
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            if (IsType(batch[i], LayoutMessageType))
            {
                lastLayoutIdx = i;
                break;
            }
        }
        if (lastLayoutIdx < 0) return;
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            if (i != lastLayoutIdx && IsType(batch[i], LayoutMessageType))
            {
                batch.RemoveAt(i);
            }
        }
    }

    private static bool IsType(byte[] frame, string type)
    {
        if (frame.Length < 4 + 10) return false; // 4 byte len + minimal JSON
        // Skip 4-byte length prefix, decode UTF-8 (just check for "type":"X")
        var jsonStr = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);
        return jsonStr.Contains($"\"type\":\"{type}\"");
    }
}
