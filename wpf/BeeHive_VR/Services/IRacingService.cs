using System.Linq;
using IRSDKSharper;

namespace BeeHiveVR.Services;

/// <summary>
/// Kapselt die Verbindung zur iRacing-Telemetrie via IRSDKSharper.
/// 
/// Vier orthogonale Konzepte werden bereitgestellt:
///  1. Connected   — App verbunden mit iRacing
///  2. Session     — Auto/Strecke/SessionType (aus YAML, langsam)
///  3. Location    — wo physisch (Pit/Garage/Track/OutOfCar/...)
///  4. SessionState — wo in der Session (Warmup/Racing/Cooldown/...) + IsReplay
/// 
/// Update-Frequenz Telemetrie: 2 Hz (UpdateInterval=60). Events feuern
/// nur bei tatsaechlicher Aenderung — kein Spam im Logger.
/// </summary>
public sealed class IRacingService
{
    private static IRacingService? _instance;
    public static IRacingService Instance => _instance ??= new IRacingService();

    private readonly IRacingSdk _sdk;
    private bool _started;

    /// <summary>Read-only SDK-Zugriff für andere Services (TradingPaints, etc.).</summary>
    public IRacingSdk Sdk => _sdk;

    // SessionNum → SessionType (aus YAML gecacht). Die *aktuelle* Session kommt
    // live aus der Telemetrie ("SessionNum"), NICHT FirstOrDefault.
    private System.Collections.Generic.Dictionary<int, string> _sessionTypeByNum = new();
    private int _currentSessionNum = -1;

    private IRacingService()
    {
        _sdk = new IRacingSdk();
        _sdk.UpdateInterval = 30;  // 30 Hz / 30 = 2 Hz

        _sdk.OnConnected += OnConnected;
        _sdk.OnDisconnected += OnDisconnected;
        _sdk.OnException += OnException;
        _sdk.OnSessionInfo += OnSessionInfo;
        _sdk.OnTelemetryData += OnTelemetryData;
    }

    // ---- Connection -----------------------------------------------------
    public bool IsConnected { get; private set; }
    public event System.EventHandler<bool>? ConnectionChanged;

    // ---- Session-Info (aus YAML, aendert sich selten) ------------------
    public string CurrentCar { get; private set; } = "";
    public string CurrentTrack { get; private set; } = "";
    public string CurrentSessionType { get; private set; } = "";
    public event System.EventHandler? SessionInfoUpdated;

    // ---- Location + SessionState (aus Telemetrie, ~2 Hz) ----------------
    public TrackLocation Location { get; private set; } = TrackLocation.Unknown;
    public string SessionState { get; private set; } = "";
    /// <summary>true wenn iRacing gerade ein Replay abspielt.</summary>
    public bool IsReplay { get; private set; }
    public event System.EventHandler? LocationChanged;
    public event System.EventHandler? SessionStateChanged;

    // ---- Lifecycle ------------------------------------------------------
    public void Start()
    {
        if (_started) return;
        _started = true;
        Logger.Info("IRacingService: starting (waiting for iRacing simulator...)");
        _sdk.Start();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _sdk.Stop();
        Logger.Info("IRacingService: stopped");
    }

    // ---- Event-Handler --------------------------------------------------

    private void OnConnected()
    {
        IsConnected = true;
        Logger.Info("iRacing: CONNECTED");
        InvokeOnUi(() => ConnectionChanged?.Invoke(this, true));
    }

    private void OnDisconnected()
    {
        IsConnected = false;
        CurrentCar = CurrentTrack = CurrentSessionType = "";
        Location = TrackLocation.Unknown;
        SessionState = "";
        IsReplay = false;
        _sessionTypeByNum = new();
        _currentSessionNum = -1;
        Logger.Info("iRacing: DISCONNECTED");
        InvokeOnUi(() =>
        {
            ConnectionChanged?.Invoke(this, false);
            SessionInfoUpdated?.Invoke(this, System.EventArgs.Empty);
            LocationChanged?.Invoke(this, System.EventArgs.Empty);
            SessionStateChanged?.Invoke(this, System.EventArgs.Empty);
        });
    }

    private void OnException(System.Exception ex)
    {
        Logger.Error("IRacingService background-task exception", ex);
    }

    private void OnSessionInfo()
    {
        try
        {
            var info = _sdk.Data?.SessionInfo;
            if (info == null) return;

            var track = info.WeekendInfo?.TrackDisplayName ?? "";

            // Session-Liste cachen: SessionNum → SessionType. Die *aktuelle*
            // Session wird in der Telemetrie via "SessionNum" aufgelöst.
            var map = new System.Collections.Generic.Dictionary<int, string>();
            var sessions = info.SessionInfo?.Sessions;
            if (sessions != null)
                foreach (var sn in sessions)
                    map[sn.SessionNum] = sn.SessionType ?? "";
            _sessionTypeByNum = map;
            var sessionType = ResolveSessionType();

            string car = "";
            var drivers = info.DriverInfo?.Drivers;
            int myIdx = info.DriverInfo?.DriverCarIdx ?? -1;
            if (drivers != null && myIdx >= 0)
            {
                foreach (var d in drivers)
                {
                    if (d.CarIdx == myIdx)
                    {
                        car = d.CarScreenName ?? "";
                        break;
                    }
                }
            }

            if (car == CurrentCar && track == CurrentTrack && sessionType == CurrentSessionType)
                return;

            CurrentCar = car;
            CurrentTrack = track;
            CurrentSessionType = sessionType;

            Logger.Info($"iRacing session: car='{car}' track='{track}' sessionType='{sessionType}'");
            InvokeOnUi(() => SessionInfoUpdated?.Invoke(this, System.EventArgs.Empty));
        }
        catch (System.Exception ex)
        {
            Logger.Error("OnSessionInfo failed", ex);
        }
    }

    /// <summary>Aktueller SessionType aus dem SessionNum-Cache (leer wenn unbekannt).</summary>
    private string ResolveSessionType()
        => _sessionTypeByNum.TryGetValue(_currentSessionNum, out var t) ? t : "";

    private void OnTelemetryData()
    {
        try
        {
            var data = _sdk.Data;
            if (data == null) return;

            // ---- Location: IsOnTrack hat Vorrang vor PlayerTrackSurface ---
            // Variante A: praeziser Status, "OutOfCar" wenn Fahrer raus aber Auto noch in Welt
            bool isOnTrack = data.GetBool("IsOnTrack");
            bool inGarage = data.GetBool("IsInGarage");
            int surface = data.GetInt("PlayerTrackSurface");

            TrackLocation newLoc;
            if (inGarage)
            {
                newLoc = TrackLocation.InGarage;
            }
            else if (!isOnTrack)
            {
                // Fahrer nicht aktiv im Auto. Wenn Surface noch was zeigt -> ausgestiegen.
                // Surface == -1 oder unbekannt -> NotInWorld (Replay/Spectator/vor Session).
                newLoc = surface < 0 ? TrackLocation.NotInWorld : TrackLocation.OutOfCar;
            }
            else
            {
                // Im Auto, aktiv
                newLoc = surface switch
                {
                    0 => TrackLocation.OffTrack,
                    1 => TrackLocation.InPit,        // InPitStall
                    2 => TrackLocation.InPit,        // ApproachingPits
                    3 => TrackLocation.OnTrack,
                    _ => TrackLocation.Unknown,
                };
            }

            // ---- SessionState (iRacing-Enum 0..6) ----
            int stateInt = data.GetInt("SessionState");
            string newState = stateInt switch
            {
                0 => "Invalid",
                1 => "GetInCar",
                2 => "Warmup",
                3 => "ParadeLaps",
                4 => "Racing",
                5 => "Checkered",
                6 => "CoolDown",
                _ => "Unknown"
            };

            // ---- Aenderungs-Detection + Events ----
            if (newLoc != Location)
            {
                Location = newLoc;
                Logger.Info($"iRacing location: {newLoc}");
                InvokeOnUi(() => LocationChanged?.Invoke(this, System.EventArgs.Empty));
            }
            if (newState != SessionState)
            {
                SessionState = newState;
                Logger.Info($"iRacing sessionState: {newState}");
                InvokeOnUi(() => SessionStateChanged?.Invoke(this, System.EventArgs.Empty));
            }

            // ---- Aktuelle Session via SessionNum (Fix: nicht FirstOrDefault) ----
            int sessionNum = data.GetInt("SessionNum");
            if (sessionNum != _currentSessionNum)
            {
                _currentSessionNum = sessionNum;
                var liveType = ResolveSessionType();
                if (liveType != CurrentSessionType)
                {
                    CurrentSessionType = liveType;
                    Logger.Info($"iRacing sessionType (live): '{liveType}' (SessionNum={sessionNum})");
                    InvokeOnUi(() => SessionInfoUpdated?.Invoke(this, System.EventArgs.Empty));
                }
            }

            // ---- Replay ----
            bool isReplay = data.GetBool("IsReplayPlaying");
            if (isReplay != IsReplay)
            {
                IsReplay = isReplay;
                Logger.Info($"iRacing replay: {isReplay}");
                InvokeOnUi(() => SessionStateChanged?.Invoke(this, System.EventArgs.Empty));
            }
        }
        catch (System.Exception ex)
        {
            Logger.Error("OnTelemetryData failed", ex);
        }
    }

    private static void InvokeOnUi(System.Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}