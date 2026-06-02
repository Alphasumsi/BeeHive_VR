using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoneyOverlays.Models;
using HoneyOverlays.Services;

namespace HoneyOverlays.ViewModels;

/// <summary>
/// Haupt-ViewModel des Fensters. Hält die Liste aller Layouts,
/// das aktuell ausgewählte Layout (zum Bearbeiten) und das
/// "aktive" Layout (das im Override-Modus in VR angezeigt wird).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>Alle Layouts (Default + ein Eintrag pro Auto).</summary>
    public ObservableCollection<CarLayoutViewModel> Layouts { get; } = new();

    /// <summary>Globales, car-unabhängiges Spotter-Set (Replay/Spectator/Teamkollege).</summary>
    public SpotterLayoutViewModel SpotterLayout { get; } = new();

    /// <summary>Sortierte View: Default oben, dann Favoriten, dann Rest (alphabetisch).</summary>
    public ICollectionView LayoutsView { get; }

    /// <summary>Layout das gerade in der GUI bearbeitet wird.</summary>
    [ObservableProperty] private CarLayoutViewModel? _selectedLayout;

    /// <summary>true = der Editor bearbeitet das globale Spotter-Set statt einer Auto-Session.
    /// Reiner Editier-Zustand — ändert NICHT was live ist (das steuert _overlayContext).</summary>
    [ObservableProperty] private bool _editingSpotter;

    /// <summary>Mock-Daten im VR-Layouts-Edit-Mode: statische Demo-Werte in alle
    /// platzierten irdashies-Overlays speisen, damit sie beim Platzieren sichtbar sind.
    /// Wird per Toggle im Edit-Header gesteuert. Pausiert automatisch bei echtem iRacing.
    /// Nicht persistent (jeden App-Start aus).</summary>
    [ObservableProperty] private bool _mockEnabled;

    partial void OnMockEnabledChanged(bool value)
        => IrdashiesAdapterService.Instance.SetMockStatic(value);

    /// <summary>Die Quellen-Collection, die der Editor gerade bearbeitet:
    /// Spotter-Set oder die Sources des SelectedLayout.</summary>
    public ObservableCollection<SourceViewModel>? EditSources
        => EditingSpotter ? SpotterLayout.Sources : SelectedLayout?.Sources;

    partial void OnEditingSpotterChanged(bool value)
    {
        // Spotter und All-Sessions schließen sich aus (Spotter ist global).
        if (value && SelectedLayout != null && SelectedLayout.EditingAllSessions)
            SelectedLayout.EditingAllSessions = false;
        OnPropertyChanged(nameof(EditSources));
    }

    partial void OnSelectedLayoutChanged(CarLayoutViewModel? value)
        => OnPropertyChanged(nameof(EditSources));

    /// <summary>Layout das via "Set as active" als Override gepinnt wurde.</summary>
    [ObservableProperty] private CarLayoutViewModel? _activeLayout;

    // --- Live-Status (aus IRacingService) ----------------------------------
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _hasActiveSession;
    [ObservableProperty] private string _liveSession = "";
    [ObservableProperty] private string _liveTrack = "";
    [ObservableProperty] private string _liveCar = "";
    [ObservableProperty] private string _liveLocation = "";
    [ObservableProperty] private string _liveSessionState = "";

    // --- Globaler VR-Visible-Master-Schalter ------------------------------
    /// <summary>
    /// Wenn false → die C++ Engine wird angewiesen alle Overlays in VR
    /// auszublenden. Default: true. Wird live via EngineLink gepusht.
    /// </summary>
    [ObservableProperty] private bool _overlaysVisibleInVR = true;

    /// <summary>Toggelt den globalen Visible-Master.</summary>
    [RelayCommand]
    private void ToggleOverlaysVisible()
    {
        OverlaysVisibleInVR = !OverlaysVisibleInVR;
        Logger.Info($"Overlays VR-visibility: {(OverlaysVisibleInVR ? "Visible" : "Hidden")}");
        // Push erfolgt automatisch via OnOverlaysVisibleInVRChanged-Partial.
    }

    /// <summary>Engine-Verbindungsstatus, gespiegelt vom EngineLink.</summary>
    [ObservableProperty] private bool _isEngineConnected;

    // --- Aktive Icon-Nav-Sektion (für Status-Leiste links) -----------------
    [ObservableProperty] private string _activeSection = "Layout";

    // --- Dev-Mode: 5x Klick auf Settings-Icon innerhalb 3s aktiviert/deaktiviert
    [ObservableProperty] private bool _isDevMode;

    // Icon-Nav-Sichtbarkeit — Nav-Buttons binden hier, gesetzt aus den
    // Appearance-Toggles (SettingsViewModel.SyncNav) bzw. initial aus dem Store.
    [ObservableProperty] private bool _showTradingPaints = true;
    [ObservableProperty] private bool _showHtmlOverlays;
    [ObservableProperty] private bool _showAutostart;
    [ObservableProperty] private bool _showButtonbox;
    // Dashies/Autostart/Buttonbox sind experimentell: ihr Sichtbarkeits-Toggle
    // ist nur in der Settings→Developer-Sektion (also nur im Dev-Mode) erreichbar,
    // aber persistent. Einmal aktiviert bleibt der Tab auch nach Neustart sichtbar
    // (Nav-Icon in Dev-Akzentfarbe als Markierung). Bereits ins Layout eingefügte
    // Overlays laufen ohnehin unabhängig vom Tab weiter.
    [ObservableProperty] private bool _showDashies;

    /// <summary>Debug-Nav-Button-Sichtbarkeit. In der Lite-Edition ausgeblendet
    /// (siehe Ctor), sonst immer sichtbar.</summary>
    [ObservableProperty] private bool _showDebug = true;

    /// <summary>Produktname der aktuellen Edition (Voll vs. Lite) — für
    /// Fenstertitel + Titelleiste.</summary>
    public string ProductName => AppEdition.ProductName;

    /// <summary>Dev-Toggle (Live-Spiegel von SettingsStore): neue Layouts-Sidebar.</summary>
    [ObservableProperty] private bool _useLegacyLayoutBar;

    /// <summary>Live-Spiegel: wenn true, ist Default in der Sidebar verborgen
    /// (interner Fallback bleibt aber funktional).</summary>
    [ObservableProperty] private bool _autoCreateLayoutOnNewCar = true;
    private int _devClickCount;
    private System.DateTime _devFirstClickAt = System.DateTime.MinValue;

    private System.DateTime _devLastClickAt = System.DateTime.MinValue;

    /// <summary>Wird vom Settings-Nav-Klick aufgerufen — zählt 5x in 2s = Dev-Mode-Toggle.
    /// Wenn zwischen zwei Klicks mehr als 1s liegt, wird der Counter zurückgesetzt.</summary>
    public void RegisterSettingsClickForDevMode()
    {
        var now = System.DateTime.UtcNow;

        // Reset wenn 1s seit letztem Klick vergangen, ODER wenn 2s-Gesamtfenster überschritten
        if (_devClickCount == 0
            || (now - _devLastClickAt).TotalSeconds > 1
            || (now - _devFirstClickAt).TotalSeconds > 2)
        {
            _devClickCount = 1;
            _devFirstClickAt = now;
            _devLastClickAt = now;
            return;
        }

        _devClickCount++;
        _devLastClickAt = now;

        if (_devClickCount >= 5)
        {
            _devClickCount = 0;
            IsDevMode = !IsDevMode;
            Logger.Info(IsDevMode ? "Dev mode enabled" : "Dev mode disabled");
        }
    }

    // --- Drei interne Clipboards (in-Memory, kein Windows-Clipboard) ------
    private CarLayoutModel? _layoutClipboard;
    private SourceModel? _overlayClipboard;
    private PositionData? _positionClipboard;

    [ObservableProperty] private bool _hasLayoutClipboard = false;
    [ObservableProperty] private bool _hasOverlayClipboard = false;
    [ObservableProperty] private bool _hasPositionClipboard = false;

    // --- Auto-Save -----------------------------------------------------------
    private readonly AutoSaveService _autoSave = new();

    /// <summary>Properties die debounced (500ms) gespeichert werden — kontinuierliche Slider.</summary>
    private static readonly System.Collections.Generic.HashSet<string> DebouncedSourceProps = new()
    {
        nameof(SourceViewModel.X),
        nameof(SourceViewModel.Y),
        nameof(SourceViewModel.Z),
        nameof(SourceViewModel.Yaw),
        nameof(SourceViewModel.Pitch),
        nameof(SourceViewModel.Scale),
        nameof(SourceViewModel.Opacity)
    };

    /// <summary>Properties die SOFORT gespeichert werden — diskrete Toggles/Inputs.</summary>
    private static readonly System.Collections.Generic.HashSet<string> ImmediateSourceProps = new()
    {
        nameof(SourceViewModel.Visible),
        nameof(SourceViewModel.Name),
        nameof(SourceViewModel.Target),
        nameof(SourceViewModel.Type),
        nameof(SourceViewModel.PixelWidth),
        nameof(SourceViewModel.PixelHeight),
    };

    /// <summary>Beim App-Close: pending Save flushen.</summary>
    public void FlushPendingSaves() => _autoSave.Flush();

    public MainViewModel()
    {
        LayoutsView = CollectionViewSource.GetDefaultView(Layouts);
        LayoutsView.SortDescriptions.Add(new SortDescription(nameof(CarLayoutViewModel.IsDefault),
                                                              ListSortDirection.Descending));
        LayoutsView.SortDescriptions.Add(new SortDescription(nameof(CarLayoutViewModel.IsFavorite),
                                                              ListSortDirection.Descending));
        LayoutsView.SortDescriptions.Add(new SortDescription(nameof(CarLayoutViewModel.CarName),
                                                              ListSortDirection.Ascending));

        // Icon-Nav-Sichtbarkeit initial aus dem Store (SettingsStore ist in
        // App.OnStartup vor dem MainWindow geladen).
        var cfg = SettingsStore.Current;
        ShowTradingPaints = cfg.ShowTradingPaints;
        ShowHtmlOverlays = cfg.ShowHtmlOverlays;
        ShowAutostart = cfg.ShowAutostart;
        ShowButtonbox = cfg.ShowButtonbox;
        ShowDashies = cfg.ShowDashies;
        UseLegacyLayoutBar = cfg.UseLegacyLayoutBar;
        AutoCreateLayoutOnNewCar = cfg.AutoCreateLayoutOnNewCar;

        // Lite-Edition: nur VR-Layouts + Dashies (+ Menu/Settings/Support, die
        // immer sichtbar sind). Übrige Tabs hart aus, unabhängig vom Store-Stand.
        // Compile-time gegated (LITE-Konstante), damit im Voll-Build kein toter
        // Code entsteht.
#if LITE
        ShowTradingPaints = false;
        ShowHtmlOverlays = false;
        ShowAutostart = false;
        ShowButtonbox = false;
        ShowDebug = false;
        ShowDashies = true;
#endif

        // Initialer Stand + Live-Updates vom IRacingService
        var iracing = IRacingService.Instance;
        IsConnected = iracing.IsConnected;
        ApplySessionInfo();
        ApplyLocation();
        ApplySessionState();
        _overlayContext = ComputeContext(); // initial sofort, ohne Entprellung

        iracing.ConnectionChanged += (_, connected) =>
        {
            IsConnected = connected;
            HasActiveSession = connected && !string.IsNullOrEmpty(iracing.CurrentCar);
            if (!connected)
            {
                LiveCar = LiveTrack = LiveSession = LiveLocation = LiveSessionState = "";
            }
            RecomputeContextDebounced();
        };
        iracing.SessionInfoUpdated += (_, _) => { ApplySessionInfo(); RecomputeContextDebounced(); };
        iracing.LocationChanged += (_, _) => { ApplyLocation(); RecomputeContextDebounced(); };
        iracing.SessionStateChanged += (_, _) => { ApplySessionState(); RecomputeContextDebounced(); };

        // Engine-Pipe: bei Connect aktuellen Zustand pushen, Status-Flag spiegeln.
        var engine = EngineLink.Instance;
        engine.ConnectionChanged += (_, connected) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsEngineConnected = connected;
                if (connected) PushCurrentStateToEngine();
            });
        };
        engine.StatusReceived += (_, sources) =>
        {
            var s = string.Join(", ", sources.Select(x =>
                $"{x.Id}={(x.Matched ? $"{x.Width}x{x.Height}" : "missing")}"));
            Logger.Info($"EngineLink status: {s}");

            // Status auf die SourceViewModels des aktiven Layouts mappen (UI-Thread).
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyEngineStatus(sources));
        };

        // Place-in-VR (Häppchen 4): Engine pusht neue Pose/Scale/Opacity während
        // der User in VR zieht — wir schreiben sie in die passende SourceVM und
        // damit über die normale Pipe in JSON + UI-Slider.
        engine.PlaceUpdateReceived += (_, pu) =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnPlaceUpdate(pu));

        // Place-Modus wechselt: an = Engine ist autoritativ (Echo-Push aus);
        // aus = Auto-Save flushen + Engine resyncen (Suppress wieder weg).
        engine.PlaceModeChanged += (_, on) =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnPlaceModeChanged(on));

        // Auto-Detect der Browser-Content-Größe via Reporter-Datei
        BrowserHostSizeReporter.Instance.SizeReported += (_, info) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyReportedSize(info.SourceId, info.Width, info.Height));
        };

        // Spotter-Set-Änderung → nur neu pushen wenn Spotter gerade live ist.
        SpotterLayout.OverlaysChanged += (_, _) =>
        {
            if (_overlayContext == OverlayContext.Spotter) PushCurrentLayoutToEngine();
        };

        // Bewusst kein Auto-Load im Konstruktor — wir lassen das MainWindow
        // das nach Loaded triggern, damit Fehler dem User sichtbar gezeigt
        // werden können statt im weißen Splash-Screen zu landen.
    }

    // ---- Live-Status-Sync vom IRacingService -------------------------------

    private string _lastAutoSwitchedCar = "";
    private bool _layoutsLoaded;

    private void ApplySessionInfo()
    {
        var s = IRacingService.Instance;
        LiveCar = s.CurrentCar;
        LiveTrack = s.CurrentTrack;
        LiveSession = s.CurrentSessionType;
        HasActiveSession = s.IsConnected && !string.IsNullOrEmpty(s.CurrentCar);

        // Auto-Switch erst nach LoadFromDisk — sonst würde AutoCreateLayoutOnNewCar
        // bestehende JSON-Files mit leeren Sessions überschreiben (Race-Condition
        // wenn iRacing schon läuft und SessionInfoUpdated vor LoadFromDisk feuert).
        if (!_layoutsLoaded) return;

        // Auto-Layout-Switch (Schritt 14): bei Auto-Wechsel passendes Layout aktivieren.
        if (LiveCar != _lastAutoSwitchedCar)
        {
            _lastAutoSwitchedCar = LiveCar;
            AutoSwitchLayoutForCar(LiveCar);
        }

        // Hinweis: KEIN Auto-Session-Switch mehr auf SelectedSession. Welche
        // Session live ins VR geht, bestimmt _overlayContext (kontext-gegated
        // in PushCurrentLayoutToEngine). Die Pills = nur Bearbeitung.
    }

    /// <summary>
    /// Findet das Layout das zum gegebenen Auto-Namen passt und macht es aktiv.
    /// Bei Mismatch und Setting `AutoCreateLayoutOnNewCar=true` wird ein neues
    /// Layout mit exakt dem iRacing-CarName angelegt und gespeichert.
    /// Sonst Fallback auf Default-Layout. Leerer Auto-Name → kein Switch.
    /// </summary>
    private void AutoSwitchLayoutForCar(string carName)
    {
        if (string.IsNullOrWhiteSpace(carName)) return;

        var match = Layouts.FirstOrDefault(l => !l.IsDefault &&
            string.Equals(l.CarName, carName, System.StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            if (ActiveLayout != match)
            {
                Logger.Info($"Auto-layout-switch: car=\"{carName}\" → layout \"{match.CarName}\"");
                ActiveLayout = match;
            }
            // Editor mitwechseln (nur bei Fahrzeug-Erkennung — manuelle Sidebar-
            // Wahl danach bleibt unangetastet und löst ggf. den Warn-Banner aus).
            if (SelectedLayout != match) SelectedLayout = match;
            return;
        }

        if (SettingsStore.Current.AutoCreateLayoutOnNewCar)
        {
            var newModel = new CarLayoutModel
            {
                CarName = carName,
                CarClass = "",
                IsDefault = false,
                IsFavorite = false,
                Sessions = new List<SessionConfigModel>
                {
                    new() { Session = SessionType.Practice },
                    new() { Session = SessionType.Qualify },
                    new() { Session = SessionType.Race },
                    new() { Session = SessionType.TestDrive },
                },
            };
            if (ConfigStore.Save(newModel))
            {
                var newVm = CarLayoutViewModel.FromModel(newModel);
                Layouts.Add(newVm);
                SubscribeToLayout(newVm);
                UpdateLastFavoriteFlags();
                Logger.Info($"Auto-layout-switch: car=\"{carName}\" → auto-created new layout");
                ActiveLayout = newVm;
                SelectedLayout = newVm; // Editor mitwechseln
                return;
            }
            Logger.Warn($"Auto-layout-switch: car=\"{carName}\" → ConfigStore.Save failed, falling back to default");
        }

        var defaultLayout = Layouts.FirstOrDefault(l => l.IsDefault);
        if (defaultLayout != null)
        {
            if (ActiveLayout != defaultLayout)
            {
                Logger.Info($"Auto-layout-switch: car=\"{carName}\" → no match, using default");
                ActiveLayout = defaultLayout;
            }
            if (SelectedLayout != defaultLayout) SelectedLayout = defaultLayout;
        }
    }

    /// <summary>
    /// Maps iRacing-Session-Type-Strings (z.B. "Open Practice", "Lone Qualify", "Race",
    /// "Heat Race", "Offline Testing") auf unsere SessionType-Enum. null = keine Anwendung.
    /// </summary>
    private static SessionType? MapSessionType(string iracingSession)
    {
        if (string.IsNullOrEmpty(iracingSession)) return null;
        if (iracingSession.Contains("Race", System.StringComparison.OrdinalIgnoreCase)) return SessionType.Race;
        if (iracingSession.Contains("Qualif", System.StringComparison.OrdinalIgnoreCase)) return SessionType.Qualify;
        if (iracingSession.Contains("Practice", System.StringComparison.OrdinalIgnoreCase)) return SessionType.Practice;
        if (iracingSession.Contains("Testing", System.StringComparison.OrdinalIgnoreCase)) return SessionType.TestDrive;
        if (iracingSession.Contains("Warmup", System.StringComparison.OrdinalIgnoreCase)) return SessionType.Race;
        return null;
    }

    private void ApplyLocation()
    {
        LiveLocation = IRacingService.Instance.Location.ToString();
    }

    private void ApplySessionState()
    {
        LiveSessionState = IRacingService.Instance.SessionState;
    }

    /// <summary>
    /// Lädt alle Configs aus dem AppData-Ordner. Falls leer / nicht vorhanden,
    /// wird ein leeres Default-Layout angelegt. Wird vom MainWindow.Loaded
    /// aufgerufen.
    /// </summary>
    public void LoadFromDisk()
    {
        Layouts.Clear();

        // Default sicherstellen — gibt entweder das vorhandene oder ein
        // frisch angelegtes leeres Default zurück.
        var defaultModel = ConfigStore.EnsureDefaultExists();

        // Alle Configs einlesen
        var allModels = ConfigStore.LoadAll();

        // Default ggf. ersetzen falls auch über LoadAll gefunden — wir wollen
        // nur eine einzige Default-Instanz in der Liste.
        var nonDefaults = allModels.Where(m => !m.IsDefault).ToList();
        var loadedDefault = allModels.FirstOrDefault(m => m.IsDefault) ?? defaultModel;

        // Default zuerst, dann der Rest
        var defaultVm = CarLayoutViewModel.FromModel(loadedDefault);
        Layouts.Add(defaultVm);
        SubscribeToLayout(defaultVm);

        foreach (var m in nonDefaults)
        {
            var vm = CarLayoutViewModel.FromModel(m);
            Layouts.Add(vm);
            SubscribeToLayout(vm);
        }

        SelectedLayout = Layouts.FirstOrDefault();
        ActiveLayout = null;

        SpotterLayout.Load();

        UpdateLastFavoriteFlags();

        Logger.Info($"MainViewModel initialized with {Layouts.Count} layout(s)");

        // Auto-Switch jetzt freigeben — vorher hätte AutoCreateLayoutOnNewCar
        // existierende JSONs mit leerem Inhalt überschrieben (Race-Condition).
        _layoutsLoaded = true;
        _lastAutoSwitchedCar = "";
        ApplySessionInfo();
        _overlayContext = ComputeContext();
        RecomputeContextDebounced();
        ApplyEditorFollow(); // Editor initial auf die Live-Session stellen
    }

    // --- Layout-Selection / Active --------------------------------------------

    [RelayCommand]
    private void SelectLayout(CarLayoutViewModel layout) => SelectedLayout = layout;

    [RelayCommand]
    private void SetAsActive(CarLayoutViewModel layout)
    {
        ActiveLayout = layout;
        // ActiveLayout ist UI-State im MainVM, nicht Teil des Layout-Models —
        // hier muss nichts gespeichert werden.
    }

    [RelayCommand]
    private void ClearActive() => ActiveLayout = null;

    [RelayCommand]
    private void SelectSession(SessionType session)
    {
        if (SelectedLayout != null)
            SelectedLayout.SelectedSession = session;
    }

    public void RefreshLayoutSort()
    {
        UpdateLastFavoriteFlags();
        LayoutsView.Refresh();
    }

    // ---- Engine-Integration -------------------------------------------------

    /// <summary>
    /// Hook für CommunityToolkit-Mvvm: feuert nach ActiveLayout-Änderung.
    /// Pusht den neuen Stand an die Engine (sofern verbunden).
    /// </summary>
    partial void OnActiveLayoutChanged(CarLayoutViewModel? value) => PushCurrentLayoutToEngine();

    partial void OnOverlaysVisibleInVRChanged(bool value)
    {
        // Schon in ToggleOverlaysVisibleCommand gepusht. Hier zusätzlich für
        // andere Set-Pfade (z.B. künftige Settings-Restoration).
        EngineLink.Instance.PushMasterVisible(value);
    }

    // ==== Overlay-Kontext-State-Machine =================================
    // Bestimmt aus den iRacing-Signalen, WAS gerade ins VR gehört.
    public enum OverlayContext { None, Practice, Qualify, Race, TestDrive, Spotter }

    private OverlayContext _overlayContext = OverlayContext.None;
    public OverlayContext CurrentOverlayContext => _overlayContext;

    private System.Windows.Threading.DispatcherTimer? _contextDebounce;

    /// <summary>
    /// Priorität: Garage→None · Replay→Spotter · (Session live & nicht im Auto)→Spotter
    /// · (im Auto & gemappte Session)→P/Q/R · sonst→None (Laden/Pre-Session).
    /// </summary>
    private OverlayContext ComputeContext()
    {
        var s = IRacingService.Instance;
        if (!s.IsConnected) return OverlayContext.None;
        if (s.Location == TrackLocation.InGarage) return OverlayContext.None;
        if (s.IsReplay) return OverlayContext.Spotter;

        bool liveSession = s.SessionState is "GetInCar" or "Warmup" or "ParadeLaps"
                                            or "Racing" or "Checkered" or "CoolDown";
        bool inCar = s.Location is TrackLocation.OnTrack or TrackLocation.OffTrack
                                 or TrackLocation.InPit;

        if (inCar)
        {
            return MapSessionType(s.CurrentSessionType) switch
            {
                SessionType.Qualify => OverlayContext.Qualify,
                SessionType.Race => OverlayContext.Race,
                SessionType.Practice => OverlayContext.Practice,
                SessionType.TestDrive => OverlayContext.TestDrive,
                _ => OverlayContext.None,
            };
        }
        // Nicht im Auto: Spectator / Teamkollege fährt → Spotter, sofern Session läuft
        if (liveSession) return OverlayContext.Spotter;
        return OverlayContext.None; // Laden / Pre-Session / Invalid
    }

    /// <summary>Kontext neu berechnen, aber erst nach ~0,5 s Stabilität umschalten
    /// (verhindert Flackern bei Tow/Reset-Blips).</summary>
    private void RecomputeContextDebounced()
    {
        if (ComputeContext() == _overlayContext) return; // schon stabil
        _contextDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(500)
        };
        _contextDebounce.Tick -= ContextDebounceTick;
        _contextDebounce.Tick += ContextDebounceTick;
        _contextDebounce.Stop();
        _contextDebounce.Start();
    }

    private void ContextDebounceTick(object? sender, System.EventArgs e)
    {
        _contextDebounce?.Stop();
        var settled = ComputeContext();
        if (settled == _overlayContext) return;
        _overlayContext = settled;
        Logger.Info($"OverlayContext → {settled}");
        OnPropertyChanged(nameof(CurrentOverlayContext));
        PushCurrentLayoutToEngine();
        ApplyEditorFollow();
    }

    private OverlayContext _lastFollowedContext = OverlayContext.None;

    /// <summary>
    /// Editor-Ansicht (Pills / Spotter) folgt der Live-Session — aber nur beim
    /// tatsächlichen WECHSEL des Kontexts. Dazwischen bleibt eine manuelle
    /// Pill-Auswahl erhalten. None reißt die Ansicht nicht weg.
    /// </summary>
    private void ApplyEditorFollow()
    {
        var ctx = _overlayContext;
        if (ctx == OverlayContext.None) return;
        if (ctx == _lastFollowedContext) return;
        _lastFollowedContext = ctx;

        if (ctx == OverlayContext.Spotter)
        {
            EditingSpotter = true;
            return;
        }

        var sess = ctx switch
        {
            OverlayContext.Qualify => SessionType.Qualify,
            OverlayContext.Race => SessionType.Race,
            OverlayContext.TestDrive => SessionType.TestDrive,
            _ => SessionType.Practice,
        };
        EditingSpotter = false;
        if (SelectedLayout != null && SelectedLayout.SelectedSession != sess)
        {
            Logger.Info($"Editor folgt Live-Session → {sess}");
            SelectedLayout.SelectedSession = sess;
        }
    }

    /// <summary>Pusht Visible-Master und aktive Layout-Sources frisch an die Engine.</summary>
    private void PushCurrentStateToEngine()
    {
        EngineLink.Instance.PushMasterVisible(OverlaysVisibleInVR);
        PushCurrentLayoutToEngine();
    }

    /// <summary>
    /// Pusht die Source-Liste des aktiven Layouts an die Engine und
    /// reconciled die browser-host-Children-Prozesse.
    /// Wenn kein Layout aktiv ist → leere Liste (Engine rendert nichts, alle Children beendet).
    /// </summary>
    private void PushCurrentLayoutToEngine()
    {
        // Welche Quellen live gehen, bestimmt der Overlay-Kontext.
        switch (_overlayContext)
        {
            case OverlayContext.None:
                PushSources(new List<SourceModel>());
                return;

            case OverlayContext.Spotter:
                PushSources(SpotterLayout.Sources.Select(vm => vm.ToModel()).ToList());
                return;

            default:
                var layout = ActiveLayout;
                if (layout == null) { PushSources(new List<SourceModel>()); return; }
                layout.CommitCurrentSession();
                var pushSession = _overlayContext switch
                {
                    OverlayContext.Qualify => SessionType.Qualify,
                    OverlayContext.Race => SessionType.Race,
                    OverlayContext.TestDrive => SessionType.TestDrive,
                    _ => SessionType.Practice,
                };
                PushSources(layout.GetSessionSources(pushSession)
                                  .Select(vm => vm.ToModel()).ToList());
                return;
        }
    }

    /// <summary>
    /// Fügt eine Source dem AKTIVEN Auto-Layout hinzu (allen drei Sessions, dieselbe
    /// VM-Instanz → ein Overlay pro Auto). Speichert + pusht live. Liefert false,
    /// wenn kein Layout „set as active" ist (Dashies zeigt dann eine Fehlermeldung).
    /// </summary>
    public bool AddSourceToActiveLayout(SourceViewModel sv)
    {
        // Spotter aktiv → ins car-unabhängige Spotter-Set (Persist + Engine-Push laufen
        // über SpotterLayout-Hooks; Push nur wenn Spotter gerade live ist).
        if (EditingSpotter)
        {
            SpotterLayout.Sources.Add(sv);
            return true;
        }

        var layout = ActiveLayout;
        if (layout == null) return false;

        layout.CommitCurrentSession();
        foreach (SessionType st in System.Enum.GetValues<SessionType>())
        {
            var list = layout.GetSessionSources(st).ToList();
            list.Add(sv);
            layout.SetSessionSources(st, list);
        }
        layout.RefreshActiveSession(); // live Sources-Collection (falls editiert) nachziehen

        _autoSave.SaveImmediate(layout);
        PushCurrentLayoutToEngine();
        return true;
    }

    /// <summary>Reconciled browser-host-Children + pusht die Liste an die Engine.</summary>
    private static void PushSources(List<SourceModel> sources)
    {
        // Browser-Host-Prozesse synchronisieren (nur Browser-Sources).
        var browserSources = sources.Where(s => s.Type == SourceType.Browser).ToList();
        BrowserHostManager.Instance.Apply(browserSources);

        // Für die Engine: bei Browser-Sources den Match-Key auf source.Id umbiegen
        // (der browser-host wird mit --title=<id> gestartet, der Layer matcht über
        // Window-Title-Substring). Bei Window-Sources bleibt target wie eingegeben.
        var enginePayload = sources.Select(s => s.Type == SourceType.Browser
            ? new SourceModel
            {
                Id = s.Id, Name = s.Name, Type = s.Type, Target = s.Id, Visible = s.Visible,
                X = s.X, Y = s.Y, Z = s.Z, Yaw = s.Yaw, Pitch = s.Pitch, Scale = s.Scale, Opacity = s.Opacity,
                PixelWidth = s.PixelWidth, PixelHeight = s.PixelHeight,
            }
            : s).ToList();
        EngineLink.Instance.PushLayout(enginePayload);

        // BeeHive_VR atlas — same trigger, new wire format. Electron consumes
        // this; the old layer never saw it. See PushAtlasLayout / wpf-link.ts.
        EngineLink.Instance.PushAtlasLayout(BuildAtlasQuads(sources));
    }

    // Atlas-Rect-Vergabe ist Electron-Sache (dynamischer Packer in
    // app/src/main.ts). WPF schickt die echte Source-Id durch — damit kommt
    // placeUpdate vom Layer mit derselben Id zurück und FindLiveSourceById
    // greift. Sources werden hart auf die Atlas-Größe gedeckelt (Electron
    // hat heute 3 freie Regions).
    private const int AtlasSlotsAvailable = 3;

    private static IEnumerable<AtlasQuadDto> BuildAtlasQuads(IEnumerable<SourceModel> sources)
    {
        int i = 0;
        foreach (var s in sources)
        {
            if (i++ >= AtlasSlotsAvailable) yield break;
            var (qx, qy, qz, qw) = YawPitchToQuat(s.Yaw, s.Pitch);

            // Scale = quad width in meters (alter Layer-Vertrag). Height aus
            // Pixel-Aspect ableiten; ohne Aspekt 4:3 als Fallback.
            float widthM  = MathF.Max(s.Scale, 0.01f);
            float aspect  = s.PixelWidth > 0 && s.PixelHeight > 0
                ? (float)s.PixelHeight / s.PixelWidth
                : 3.0f / 4.0f;
            float heightM = widthM * aspect;

            yield return new AtlasQuadDto
            {
                Id      = s.Id,
                PosX    = s.X, PosY = s.Y, PosZ = s.Z,
                QuatX   = qx, QuatY = qy, QuatZ = qz, QuatW = qw,
                SizeW   = widthM, SizeH = heightM,
                Visible = s.Visible,
            };
        }
    }

    // Yaw (um Y) + Pitch (um X) in Grad → quaternion {qx, qy, qz, qw}.
    // Identische Formel wie der alte Layer (engine PoseFromXYZYP).
    private static (float qx, float qy, float qz, float qw) YawPitchToQuat(float yawDeg, float pitchDeg)
    {
        const float kDeg2Rad = MathF.PI / 180.0f;
        float y2 = yawDeg   * kDeg2Rad * 0.5f;
        float p2 = pitchDeg * kDeg2Rad * 0.5f;
        float cy = MathF.Cos(y2), sy = MathF.Sin(y2);
        float cx = MathF.Cos(p2), sx = MathF.Sin(p2);
        return (
            qx:  cy * sx,
            qy:  sy * cx,
            qz: -sy * sx,
            qw:  cy * cx);
    }

    private void UpdateLastFavoriteFlags()
    {
        var sorted = Layouts
            .OrderByDescending(l => l.IsDefault)
            .ThenByDescending(l => l.IsFavorite)
            .ThenBy(l => l.CarName)
            .ToList();

        var favorites = sorted.Where(l => !l.IsDefault && l.IsFavorite).ToList();
        var lastFav = favorites.LastOrDefault();

        foreach (var l in Layouts)
            l.IsLastFavorite = (l == lastFav);
    }

    // --- Layout Copy / Paste (alle 3 Sessions komplett) ----------------------

    [RelayCommand]
    private void CopyLayout(CarLayoutViewModel? layout)
    {
        if (layout == null) return;

        layout.CommitCurrentSession();

        _layoutClipboard = new CarLayoutModel
        {
            CarName = layout.CarName,
            CarClass = layout.CarClass,
            IsDefault = false,
            Sessions = CloneAllSessionsFrom(layout)
        };
        HasLayoutClipboard = true;
    }

    [RelayCommand]
    private void PasteLayout(CarLayoutViewModel? layout)
    {
        if (layout == null || _layoutClipboard == null) return;
        ReplaceAllSessions(layout, _layoutClipboard.Sessions);
        _autoSave.SaveImmediate(layout);
    }

    private static List<SessionConfigModel> CloneAllSessionsFrom(CarLayoutViewModel src)
    {
        src.CommitCurrentSession();

        var result = new List<SessionConfigModel>();
        foreach (SessionType st in System.Enum.GetValues<SessionType>())
        {
            var session = new SessionConfigModel { Session = st };
            foreach (var s in src.GetSessionSources(st))
                session.Sources.Add(s.ToModel());
            result.Add(session);
        }
        return result;
    }

    private static void ReplaceAllSessions(CarLayoutViewModel target,
                                            List<SessionConfigModel> newSessions)
    {
        foreach (SessionType st in System.Enum.GetValues<SessionType>())
        {
            var session = newSessions.FirstOrDefault(s => s.Session == st);
            var sources = session?.Sources.Select(SourceViewModel.FromModel).ToList()
                          ?? new List<SourceViewModel>();
            target.SetSessionSources(st, sources);
        }
        target.RefreshActiveSession();
    }

    // --- Overlay Copy / Paste / Remove ---------------------------------------

    /// <summary>Kopiert eine komplette Overlay (Source) ins App-Clipboard.</summary>
    [RelayCommand]
    private void CopyOverlay(SourceViewModel? src)
    {
        if (src == null) return;
        _overlayClipboard = src.ToModel();
        HasOverlayClipboard = true;
    }

    /// <summary>Fügt die kopierte Overlay als neuen Eintrag in die aktuelle Session ein.</summary>
    [RelayCommand]
    private void PasteOverlay()
    {
        var target = EditSources;
        if (target == null || _overlayClipboard == null) return;

        var newSrc = SourceViewModel.FromModel(_overlayClipboard);
        newSrc.Id = $"{newSrc.Id}_{System.Guid.NewGuid().ToString("N")[..8]}";
        newSrc.Name = $"{newSrc.Name} (copy)";
        target.Add(newSrc);
    }

    /// <summary>Entfernt eine Overlay nach Bestätigung durch den User.</summary>
    [RelayCommand]
    private void RemoveOverlay(SourceViewModel? src)
    {
        var target = EditSources;
        if (src == null || target == null) return;

        if (Views.ConfirmDialog.Show(
                Application.Current.MainWindow,
                "Remove overlay",
                $"Remove overlay \"{src.Name}\"?\nThis cannot be undone."))
        {
            target.Remove(src);
        }
    }

    // --- Position-only Copy / Paste -----------------------------------------

    /// <summary>Kopiert nur Position+Rotation+Scale+Opacity ins Position-Clipboard.</summary>
    [RelayCommand]
    private void CopyPosition(SourceViewModel? src)
    {
        if (src == null) return;
        _positionClipboard = PositionData.From(src);
        HasPositionClipboard = true;
    }

    /// <summary>Schreibt Position+Rotation+Scale+Opacity in eine Overlay zurück.</summary>
    [RelayCommand]
    private void PastePosition(SourceViewModel? src)
    {
        if (src == null || _positionClipboard == null) return;
        _positionClipboard.ApplyTo(src);
    }

    // --- Session-Übertragung -------------------------------------------------

    /// <summary>
    /// Kopiert die Overlays einer Quell-Session in die aktuell aktive Session
    /// des SelectedLayout (überschreibt die aktuelle Session komplett).
    /// </summary>
    [RelayCommand]
    private void CopyFromSession(SessionType source)
    {
        if (SelectedLayout == null) return;
        if (source == SelectedLayout.SelectedSession) return;

        SelectedLayout.CommitCurrentSession();

        // Quell-Sources holen, deep-copy via Model
        var sourceVMs = SelectedLayout.GetSessionSources(source);
        var copies = sourceVMs.Select(vm => SourceViewModel.FromModel(vm.ToModel())).ToList();

        // In die aktive Session schreiben
        SelectedLayout.SetSessionSources(SelectedLayout.SelectedSession, copies);
        SelectedLayout.RefreshActiveSession();
        _autoSave.SaveImmediate(SelectedLayout);
    }

    /// <summary>
    /// Wendet die aktuelle Session auf alle Sessions des SelectedLayout an.
    /// </summary>
    [RelayCommand]
    private void ApplyToAllSessions()
    {
        if (SelectedLayout == null) return;

        SelectedLayout.CommitCurrentSession();
        var current = SelectedLayout.GetSessionSources(SelectedLayout.SelectedSession);

        foreach (SessionType st in System.Enum.GetValues<SessionType>())
        {
            if (st == SelectedLayout.SelectedSession) continue;
            var copies = current.Select(vm => SourceViewModel.FromModel(vm.ToModel())).ToList();
            SelectedLayout.SetSessionSources(st, copies);
        }
        _autoSave.SaveImmediate(SelectedLayout);
    }

    /// <summary>Leert die aktuelle Session (alle Overlays entfernen) — mit Confirm.</summary>
    [RelayCommand]
    private void ClearSession()
    {
        if (SelectedLayout == null || SelectedLayout.Sources.Count == 0) return;

        var sessionName = SelectedLayout.SelectedSession.ToString();
        if (!Views.ConfirmDialog.Show(
                Application.Current.MainWindow,
                "Clear overlays",
                $"Remove all overlays from {sessionName} session of \"{SelectedLayout.CarName}\"?\nThis cannot be undone."))
            return;

        SelectedLayout.Sources.Clear();
        // Sources.Clear() triggert Sources_CollectionChanged → wird dort gesaved
    }

    // --- Source-spezifisches Place-in-VR (Start/Save/Cancel/Cycle) ----------

    /// <summary>Aktive Source-ID im Source-spezifischen Place-Mode, oder null.</summary>
    [ObservableProperty] private string? _activePlaceSourceId;

    /// <summary>Snapshot der Pre-Place-Werte (für Cancel). Per ID — damit Cycle alle
    /// während der Session berührten Sources korrekt zurückrollen kann.
    /// VM-Referenz mit drin: FindLiveSourceById braucht aktiven OverlayContext, der
    /// im Trockentest ohne iRacing fehlt — Save/Cancel müssen aber auch dort die
    /// IsPlacing-Flags räumen können.</summary>
    private sealed record PlaceSnapshot(
        SourceViewModel Vm,
        float X, float Y, float Z, float Yaw, float Pitch, float Scale, float Opacity);
    private readonly Dictionary<string, PlaceSnapshot> _placeSnapshots = new();

    /// <summary>
    /// Startet den Place-in-VR-Modus für GENAU diese Source (Source-spezifisch,
    /// Engine ignoriert Controller-Ray-Picker). Pre-Place-Werte werden gesnapshottet
    /// — Cancel kann sie wiederherstellen, Save lässt die live geschriebenen Werte stehen.
    /// </summary>
    [RelayCommand]
    private void StartPlace(SourceViewModel? src)
    {
        if (src == null) return;
        // Vorherige Place-Session zuerst abschließen (Cancel auf alle bisher berührten Sources).
        if (_placeSnapshots.Count > 0) CancelPlace();

        _placeSnapshots[src.Id] = new PlaceSnapshot(
            src, src.X, src.Y, src.Z, src.Yaw, src.Pitch, src.Scale, src.Opacity);
        ActivePlaceSourceId = src.Id;
        src.IsPlacing = true;
        EngineLink.Instance.PushPlaceMode(true, src.Id);
    }

    /// <summary>
    /// Speichert die aktuelle Place-Position. Sie wurde während des Drags pro Frame
    /// bereits in die SourceVM geschrieben (siehe <see cref="OnPlaceUpdate"/>) +
    /// autoSave-geplant; hier reicht es die Engine auszuschalten.
    /// </summary>
    [RelayCommand]
    private void SavePlace()
    {
        if (_placeSnapshots.Count == 0) return;
        // IsPlacing-Flag auf allen während dieser Session berührten Sources zurücksetzen.
        foreach (var snap in _placeSnapshots.Values)
            snap.Vm.IsPlacing = false;
        _placeSnapshots.Clear();
        ActivePlaceSourceId = null;
        EngineLink.Instance.PushPlaceMode(false);
        // _autoSave.Flush() + Resync laufen schon in OnPlaceModeChanged(false).
    }

    /// <summary>
    /// Bricht den Place-Mode ab und stellt die Pre-Place-Werte wieder her.
    /// </summary>
    [RelayCommand]
    private void CancelPlace()
    {
        if (_placeSnapshots.Count == 0) return;

        // Suppress Engine-Push während Werte zurückgerollt werden — der finale
        // Resync läuft im OnPlaceModeChanged(false) gleich nach PushPlaceMode.
        var prev = _suppressEnginePush;
        _suppressEnginePush = true;
        try
        {
            foreach (var snap in _placeSnapshots.Values)
            {
                var src = snap.Vm;
                src.X = snap.X; src.Y = snap.Y; src.Z = snap.Z;
                src.Yaw = snap.Yaw; src.Pitch = snap.Pitch;
                src.Scale = snap.Scale; src.Opacity = snap.Opacity;
                src.IsPlacing = false;
            }
        }
        finally { _suppressEnginePush = prev; }

        _placeSnapshots.Clear();
        ActivePlaceSourceId = null;
        EngineLink.Instance.PushPlaceMode(false);
        // OnPlaceModeChanged(false) flush+resync greift — Engine erhält die alten Werte.
    }

    /// <summary>
    /// Zykliert im aktiven Place-Mode auf die nächste sichtbare Source im Layout
    /// (Engine wählt). Wenn kein Place-Mode aktiv ist, passiert nichts.
    /// </summary>
    [RelayCommand]
    private void CyclePlaceTarget()
    {
        if (!EngineLink.Instance.IsPlaceModeOn) return;
        EngineLink.Instance.PushPlaceCycle();
        // Die Engine sendet danach placeUpdate für die neu gewählte Source —
        // FindLiveSourceById/OnPlaceUpdate übernehmen den restlichen Sync.
        // Snapshot bleibt auf der zuvor gestarteten Source (Cancel kehrt dorthin
        // zurück); IsPlacing-Flag auf der Karte bleibt deshalb auch dort.
    }

    /// <summary>
    /// Setzt Position/Rotation/Scale/Opacity einer Overlay auf Default-Werte zurück.
    /// </summary>
    [RelayCommand]
    private void ResetPosition(SourceViewModel? src)
    {
        if (src == null) return;

        if (!Views.ConfirmDialog.Show(
                Application.Current.MainWindow,
                "Reset position",
                $"Reset position values of overlay \"{src.Name}\" to defaults?"))
            return;

        src.X = 0.0f;
        src.Y = 0.0f;
        src.Z = -0.8f;
        src.Yaw = 0.0f;
        src.Pitch = 0.0f;
        src.Scale = 0.10f;
        src.Opacity = 1.0f;
    }

    /// <summary>
    /// Löscht ein komplettes Layout aus der Liste (mit Confirm) und entfernt
    /// die JSON-Datei. Default kann nicht gelöscht werden.
    /// </summary>
    [RelayCommand]
    private void DeleteLayout(CarLayoutViewModel? layout)
    {
        if (layout == null || layout.IsDefault) return;

        if (!Views.ConfirmDialog.Show(
                Application.Current.MainWindow,
                "Delete layout",
                $"Delete layout for \"{layout.CarName}\"?\nAll session data will be lost. This cannot be undone."))
            return;

        // Wenn das gelöschte Layout das aktive ist, Override aufheben
        if (ActiveLayout == layout)
            ActiveLayout = null;

        // Wenn das selektierte gelöscht wird, auf Default zurückfallen
        if (SelectedLayout == layout)
            SelectedLayout = Layouts.FirstOrDefault(l => l.IsDefault);

        Layouts.Remove(layout);
        UpdateLastFavoriteFlags();

        // JSON-Datei vom Disk entfernen
        ConfigStore.Delete(layout.ToModel());
    }

    // --- Subscriptions: bei welchen Property-Changes wird gesaved? ---------

    private void SubscribeToLayout(CarLayoutViewModel layout)
    {
        // CarLayout-Level: IsFavorite ändert sich → sofort speichern
        layout.PropertyChanged += Layout_PropertyChanged;

        // Sources-Collection beobachten (Add/Remove)
        layout.Sources.CollectionChanged += Sources_CollectionChanged;

        // Bestehende Sources subscriben
        foreach (var src in layout.Sources)
            src.PropertyChanged += Source_PropertyChanged;
    }

    private void Layout_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CarLayoutViewModel layout) return;

        // Nur "echte" persistente Properties speichern.
        // SelectedSession ist UI-State, NICHT speichern.
        // Sources-Collection-Changes laufen separat über Sources_CollectionChanged.
        if (e.PropertyName == nameof(CarLayoutViewModel.IsFavorite))
        {
            _autoSave.SaveImmediate(layout);
        }
        else if (e.PropertyName == nameof(CarLayoutViewModel.SelectedSession) && layout == ActiveLayout)
        {
            // Active Layout switched session → andere Source-Liste an Engine pushen.
            PushCurrentLayoutToEngine();
        }
        else if (e.PropertyName == nameof(CarLayoutViewModel.EditingAllSessions)
                 && layout == SelectedLayout
                 && layout.EditingAllSessions && EditingSpotter)
        {
            // Spotter und All-Sessions schließen sich aus — Klick auf All-Sessions
            // bei aktivem Spotter schaltet Spotter ab (Gegenrichtung zum Hook in
            // OnEditingSpotterChanged).
            EditingSpotter = false;
        }
    }

    private void Sources_CollectionChanged(object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Subscribe / Unsubscribe — IMMER (auch während Refresh), damit
        // wir bei den neuen Items die PropertyChanges nicht verpassen.
        if (e.NewItems != null)
        {
            foreach (SourceViewModel src in e.NewItems)
                src.PropertyChanged += Source_PropertyChanged;
        }
        if (e.OldItems != null)
        {
            foreach (SourceViewModel src in e.OldItems)
                src.PropertyChanged -= Source_PropertyChanged;
        }

        // Owner-Layout finden
        if (sender is not System.Collections.ObjectModel.ObservableCollection<SourceViewModel> coll)
            return;

        var owner = Layouts.FirstOrDefault(l => ReferenceEquals(l.Sources, coll));
        if (owner == null) return;

        // Während Session-Refresh NICHT speichern — das ist nur UI-Reload
        if (owner.IsRefreshing) return;

        _autoSave.SaveImmediate(owner);

        // Wenn das aktive Layout, Engine + browser-host-Children syncen.
        if (owner == ActiveLayout) PushCurrentLayoutToEngine();
    }

    // --- Place-in-VR (Häppchen 4): Engine → SourceVM → JSON + UI ----------

    private void OnPlaceUpdate(PlaceUpdate pu)
    {
        if (string.IsNullOrEmpty(pu.Id)) return;
        var src = FindLiveSourceById(pu.Id);
        if (src == null) return;

        // Cycle-Edge-Case: Engine kann nach placeCycle auf eine andere Source
        // wechseln. Erstes Update für eine Source → Pre-Place-Werte sichern
        // (Cancel kann sie zurückrollen), Active-State und IsPlacing-Flag
        // auf die neue Source umschwenken.
        if (_placeSnapshots.Count > 0 && !_placeSnapshots.ContainsKey(pu.Id))
        {
            _placeSnapshots[pu.Id] = new PlaceSnapshot(
                src, src.X, src.Y, src.Z, src.Yaw, src.Pitch, src.Scale, src.Opacity);
        }
        if (ActivePlaceSourceId != null && ActivePlaceSourceId != pu.Id)
        {
            var prevSrc = FindLiveSourceById(ActivePlaceSourceId);
            if (prevSrc != null) prevSrc.IsPlacing = false;
            ActivePlaceSourceId = pu.Id;
            src.IsPlacing = true;
        }

        // Engine ist während Place-Modus autoritativ — Werte 1:1 in die VM.
        // Echo-Push lokal unterdrücken (zusätzlich zu OnPlaceModeChanged), damit
        // ein verspäteter Update nicht zurückbouncen kann.
        bool prev = _suppressEnginePush;
        _suppressEnginePush = true;
        try
        {
            src.X = pu.X;
            src.Y = pu.Y;
            src.Z = pu.Z;
            src.Yaw = pu.Yaw;
            src.Pitch = pu.Pitch;
            src.Scale = pu.Scale;
            src.Opacity = pu.Opacity;
        }
        finally { _suppressEnginePush = prev; }

        // BeeHive_VR (2.6.2026): Layer hält die neue Pose nur für die Dauer
        // des Grabs als lokales Overlay; Electron muss die Werte mitkriegen,
        // sonst snappt der Quad beim Trigger-Release auf den letzten
        // FrameSlot-Stand zurück (Bug 'a' aus Phase B). Push fühlt sich
        // doppelt an, ist aber bewusst — der Echo-Loop wird vom Layer-Override
        // gefangen weil m_dragPos vom Controller, nicht vom FrameSlot getrieben
        // wird.
        EngineLink.Instance.PushAtlasLayout(BuildAtlasQuads(GetSourcesForCurrentContext()));

        // Persistenz: SpotterLayout speichert sich selbst (eigener Hook). Car-
        // Layouts: am OWNER schedulen (≠ SelectedLayout möglich, dann fasst der
        // Source_PropertyChanged-Default-Path nicht).
        if (_overlayContext != OverlayContext.Spotter && ActiveLayout != null)
            _autoSave.ScheduleSave(ActiveLayout);
    }

    // Helper für OnPlaceUpdate — spiegelt die Logik in PushCurrentLayoutToEngine,
    // gibt aber die Source-Liste statt sie zu pushen.
    private IEnumerable<SourceModel> GetSourcesForCurrentContext()
    {
        switch (_overlayContext)
        {
            case OverlayContext.None:
                return Array.Empty<SourceModel>();
            case OverlayContext.Spotter:
                return SpotterLayout.Sources.Select(vm => vm.ToModel()).ToList();
            default:
                var layout = ActiveLayout;
                if (layout == null) return Array.Empty<SourceModel>();
                var pushSession = _overlayContext switch
                {
                    OverlayContext.Qualify => SessionType.Qualify,
                    OverlayContext.Race => SessionType.Race,
                    OverlayContext.TestDrive => SessionType.TestDrive,
                    _ => SessionType.Practice,
                };
                return layout.GetSessionSources(pushSession)
                             .Select(vm => vm.ToModel()).ToList();
        }
    }

    private SourceViewModel? FindLiveSourceById(string id)
    {
        if (_overlayContext == OverlayContext.Spotter)
            return SpotterLayout.Sources.FirstOrDefault(s => s.Id == id);
        if (_overlayContext == OverlayContext.None || ActiveLayout == null)
            return null;
        var sess = _overlayContext switch
        {
            OverlayContext.Qualify => SessionType.Qualify,
            OverlayContext.Race => SessionType.Race,
            OverlayContext.TestDrive => SessionType.TestDrive,
            _ => SessionType.Practice,
        };
        return ActiveLayout.GetSessionSources(sess).FirstOrDefault(s => s.Id == id);
    }

    private void OnPlaceModeChanged(bool on)
    {
        if (on)
        {
            _suppressEnginePush = true; // Engine autoritativ, kein Rück-Push
        }
        else
        {
            _suppressEnginePush = false;
            _autoSave.Flush();           // finalen Stand committen
            PushCurrentLayoutToEngine(); // einmal sauber resyncen
        }
    }

    private void Source_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Source-Änderung → wir speichern das aktuell SELECTED Layout (Variante c).
        if (SelectedLayout == null || e.PropertyName == null) return;

        // Während Session-Refresh NICHT speichern
        if (SelectedLayout.IsRefreshing) return;

        // UI-State (IsExpanded, IsRenaming) nicht speichern
        if (e.PropertyName == nameof(SourceViewModel.IsExpanded) ||
            e.PropertyName == nameof(SourceViewModel.IsRenaming))
            return;

        if (DebouncedSourceProps.Contains(e.PropertyName))
        {
            _autoSave.ScheduleSave(SelectedLayout);
            // Slider-Werte → Engine sofort pushen wenn aktives Layout (keine Debounce für VR-Live-Update).
            if (!_suppressEnginePush && SelectedLayout == ActiveLayout) PushCurrentLayoutToEngine();
        }
        else if (ImmediateSourceProps.Contains(e.PropertyName))
        {
            // Bei Target-Änderung an Browser-Source: URL nach width/height-Params durchsuchen
            // und PixelWidth/PixelHeight automatisch vorbefüllen. Macht das Setup für
            // generische Overlays ohne manuelle Eingabe.
            if (sender is SourceViewModel src2 && e.PropertyName == nameof(SourceViewModel.Target) &&
                src2.Type == SourceType.Browser)
            {
                TryAutoSizeFromUrl(src2);
            }
            if (!_suppressEnginePush)
            {
                _autoSave.SaveImmediate(SelectedLayout);
                if (SelectedLayout == ActiveLayout) PushCurrentLayoutToEngine();
            }
        }
        // Andere Properties (z.B. Id) ignorieren wir
    }

    /// <summary>
    /// Wird vom BrowserHostSizeReporter ausgelöst, wenn browser-host die CSS-Content-Größe
    /// einer Source gemessen hat. Setzt PixelWidth/Height am passenden Source nur dann, wenn
    /// der User noch nichts eingegeben hat (beide 0). User-eingegebene Werte werden respektiert.
    /// </summary>
    private void ApplyReportedSize(string sourceId, int w, int h)
    {
        // Alle Sessions aller Layouts durchsuchen — Source kann in einer nicht-aktiven Session
        // sein, browser-host wurde aber für sie gespawnt sobald sie zur aktiven Session gehört.
        foreach (var layout in Layouts)
        {
            foreach (var session in System.Enum.GetValues<SessionType>())
            {
                foreach (var src in layout.GetSessionSources(session))
                {
                    if (src.Id != sourceId) continue;
                    if (src.PixelWidth > 0 || src.PixelHeight > 0)
                    {
                        return; // user hat schon Werte
                    }
                    _suppressEnginePush = true;
                    try
                    {
                        src.PixelWidth = w;
                        src.PixelHeight = h;
                    }
                    finally
                    {
                        _suppressEnginePush = false;
                    }
                    _autoSave.SaveImmediate(layout);
                    if (layout == ActiveLayout) PushCurrentLayoutToEngine();
                    Logger.Info($"AutoSize from browser-host: \"{src.Name}\" (layout=\"{layout.CarName}\" {session}) → {w}x{h}");
                    return;
                }
            }
        }
        Logger.Warn($"AutoSize: no source found with id={sourceId} for size {w}x{h}");
    }

    /// <summary>
    /// Engine-Reverse-Channel-Status auf die Sources des AKTIVEN Layouts mappen
    /// (nur das rendert die Engine). Sources im Status → matched/Maße setzen,
    /// fehlende → nicht gefunden. Andere Layouts behalten neutralen Status (null).
    /// Reiner UI-State, nicht persistiert (nicht in der Save-Allowlist).
    /// </summary>
    private void ApplyEngineStatus(System.Collections.Generic.IReadOnlyList<EngineSourceStatus> statuses)
    {
        var layout = ActiveLayout;
        if (layout == null) return;

        var byId = new System.Collections.Generic.Dictionary<string, EngineSourceStatus>();
        foreach (var st in statuses) byId[st.Id] = st;

        foreach (var src in layout.Sources)
        {
            if (byId.TryGetValue(src.Id, out var st))
            {
                src.IsMatched = st.Matched;
                src.CaptureWidth = (int)st.Width;
                src.CaptureHeight = (int)st.Height;
            }
            else
            {
                src.IsMatched = false;
                src.CaptureWidth = 0;
                src.CaptureHeight = 0;
            }
        }
    }

    private bool _suppressEnginePush;

    /// <summary>
    /// Versucht aus der URL einer Browser-Source die Pixel-Größe auszulesen.
    /// Erkennt Query-Params: ?w=…&h=… oder ?width=…&height=…
    /// Schreibt nur wenn entsprechende Werte gefunden — überschreibt vorhandene Werte
    /// (User hat URL geändert → neue Quelle, neue Größe).
    /// </summary>
    private static void TryAutoSizeFromUrl(SourceViewModel src)
    {
        if (string.IsNullOrWhiteSpace(src.Target)) return;
        if (!Uri.TryCreate(src.Target, UriKind.Absolute, out var uri)) return;
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query)) return;

        int w = 0, h = 0;
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part.Substring(0, eq).ToLowerInvariant();
            var val = Uri.UnescapeDataString(part.Substring(eq + 1));
            if (!int.TryParse(val, out var v) || v <= 0) continue;
            if (key == "w" || key == "width") w = v;
            else if (key == "h" || key == "height") h = v;
        }
        if (w > 0)
        {
            src.PixelWidth = w;
            Logger.Info($"AutoSize: \"{src.Name}\" url has w={w} → PixelWidth set");
        }
        if (h > 0)
        {
            src.PixelHeight = h;
            Logger.Info($"AutoSize: \"{src.Name}\" url has h={h} → PixelHeight set");
        }
    }

    /// <summary>
    /// Snapshot der reinen Position-Werte einer Overlay (ohne Name/Type/Target).
    /// Wird im Position-Clipboard gehalten.
    /// </summary>
    public sealed class PositionData
    {
        public float X, Y, Z, Yaw, Pitch, Scale, Opacity;

        public static PositionData From(SourceViewModel s) => new()
        {
            X = s.X,
            Y = s.Y,
            Z = s.Z,
            Yaw = s.Yaw,
            Pitch = s.Pitch,
            Scale = s.Scale,
            Opacity = s.Opacity
        };

        public void ApplyTo(SourceViewModel s)
        {
            s.X = X; s.Y = Y; s.Z = Z;
            s.Yaw = Yaw; s.Pitch = Pitch;
            s.Scale = Scale; s.Opacity = Opacity;
        }
    }
}