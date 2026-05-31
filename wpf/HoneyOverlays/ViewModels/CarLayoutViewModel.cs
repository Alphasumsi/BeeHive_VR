using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoneyOverlays.Models;

namespace HoneyOverlays.ViewModels;

/// <summary>
/// ViewModel für ein Auto-Layout (oder das Default-Layout).
/// Hält die Session-Konfigurationen und die aktuell gewählte Session.
/// </summary>
public partial class CarLayoutViewModel : ObservableObject
{
    [ObservableProperty] private string _carName = "";
    [ObservableProperty] private string _carClass = "";
    [ObservableProperty] private bool _isDefault = false;
    [ObservableProperty] private bool _isFavorite = false;
    [ObservableProperty] private bool _isLastFavorite = false;

    /// <summary>Quellen für die aktuell gewählte Session (UI bindet hier).</summary>
    public ObservableCollection<SourceViewModel> Sources { get; } = new();

    [ObservableProperty] private SessionType _selectedSession = SessionType.Practice;

    /// <summary>
    /// Spiegel-Modus: edits in Sources werden in alle drei Sessions
    /// (Practice/Qualify/Race) gespiegelt — gemeinsame VM-Instanzen, daher
    /// propagieren Property-Edits automatisch. Add/Remove/Move werden via
    /// CommitCurrentSession in alle Sessions synchronisiert. SelectedSession
    /// bleibt davon unberührt (= „welche Session ist gerade sichtbar").
    /// </summary>
    [ObservableProperty] private bool _editingAllSessions;

    // Komplette Session-Daten (eine Liste pro Session-Typ)
    private readonly Dictionary<SessionType, List<SourceViewModel>> _sessionsData = new()
    {
        { SessionType.Practice,  new() },
        { SessionType.Qualify,   new() },
        { SessionType.Race,      new() },
        { SessionType.TestDrive, new() },
    };

    /// <summary>
    /// Wird aufgerufen wenn der User auf Practice/Qualify/Race klickt.
    /// VORHER die noch sichtbaren Sources in die alte Session zurückschreiben,
    /// dann die neue Session laden.
    /// </summary>
    partial void OnSelectedSessionChanging(SessionType value)
    {
        // value = NEU, SelectedSession = noch ALT — also alte (und ggf. alle
        // bei EditingAllSessions) committen.
        CommitCurrentSession();
    }

    /// <summary>Wechselt Sources zwischen geteilten Instanzen (an) und unabhängigen
    /// Klonen pro Session (aus). Spotter setzt das von außen hart zurück.</summary>
    partial void OnEditingAllSessionsChanged(bool value)
    {
        IsRefreshing = true; // Auto-Save soll diesen Listen-Swap ignorieren
        try
        {
            if (value)
            {
                // Aktive Sources auf alle Sessions verteilen — eigene Listenkopie
                // pro Slot (sodass spätere Listen-Operationen sich nicht überlagern),
                // aber dieselben SourceViewModel-Instanzen (Property-Edits propagieren).
                var snap = Sources.ToList();
                foreach (SessionType st in System.Enum.GetValues<SessionType>())
                    _sessionsData[st] = snap.ToList();
            }
            else
            {
                // Sessions wieder unabhängig machen: Nicht-Active Sessions
                // deep-clonen (frische VM-Instanzen mit denselben Werten).
                foreach (SessionType st in System.Enum.GetValues<SessionType>())
                {
                    if (st == SelectedSession) continue;
                    _sessionsData[st] = _sessionsData[st]
                        .Select(vm => SourceViewModel.FromModel(vm.ToModel()))
                        .ToList();
                }
            }
        }
        finally { IsRefreshing = false; }
    }

    partial void OnSelectedSessionChanged(SessionType value)
    {
        // Klick auf eine konkrete Session beendet den Spiegel-Modus
        // (Spec: nach All-Sessions soll nur die geklickte aktiv sein).
        if (EditingAllSessions) EditingAllSessions = false;
        RefreshActiveSession();
    }

    /// <summary>
    /// Während IsRefreshing == true sollen Listener (z.B. AutoSave) ignorieren
    /// dass Sources gerade verändert wird — die Änderungen sind nur UI-Refresh,
    /// keine User-Edits.
    /// </summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>Speichert die aktuellen Sources in der aktiven Session. Bei
    /// <see cref="EditingAllSessions"/> wird der Snapshot in alle drei
    /// Sessions geschrieben (jede Session eigene Listenkopie, aber dieselben
    /// VM-Instanzen — so propagieren Property-Edits automatisch).</summary>
    public void CommitCurrentSession()
    {
        var snap = Sources.ToList();
        if (EditingAllSessions)
        {
            foreach (SessionType st in System.Enum.GetValues<SessionType>())
                _sessionsData[st] = snap.ToList();
        }
        else
        {
            _sessionsData[SelectedSession] = snap;
        }
    }

    /// <summary>Lädt die Sources der aktiven Session in die UI-Collection.</summary>
    public void RefreshActiveSession()
    {
        IsRefreshing = true;
        try
        {
            Sources.Clear();
            foreach (var s in _sessionsData[SelectedSession])
                Sources.Add(s);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Liefert die Sources einer beliebigen Session (intern).</summary>
    public IReadOnlyList<SourceViewModel> GetSessionSources(SessionType st)
    {
        // Bei EditingAllSessions immer erst committen, damit auch nicht-aktive
        // Sessions den aktuellen Stand zurückgeben (z.B. wenn iRacing Race
        // pusht während der User Practice editiert).
        if (EditingAllSessions || st == SelectedSession) CommitCurrentSession();
        return _sessionsData[st];
    }

    /// <summary>Setzt die Sources einer beliebigen Session (überschreibt).</summary>
    public void SetSessionSources(SessionType st, List<SourceViewModel> sources)
    {
        _sessionsData[st] = sources;
    }

    [RelayCommand]
    private void ToggleFavorite() => IsFavorite = !IsFavorite;

    /// <summary>Erzeugt ein VM aus einem Model.</summary>
    public static CarLayoutViewModel FromModel(CarLayoutModel m)
    {
        var vm = new CarLayoutViewModel
        {
            CarName = m.CarName,
            CarClass = m.CarClass,
            IsDefault = m.IsDefault,
            IsFavorite = m.IsFavorite
        };

        foreach (SessionType st in System.Enum.GetValues<SessionType>())
        {
            var session = m.Sessions.FirstOrDefault(s => s.Session == st);
            if (session != null)
            {
                vm._sessionsData[st] =
                    session.Sources.Select(SourceViewModel.FromModel).ToList();
            }
        }

        // Initial die Practice-Sources sichtbar machen
        foreach (var s in vm._sessionsData[SessionType.Practice])
            vm.Sources.Add(s);

        return vm;
    }
    /// <summary>Schreibt das VM zurück in ein Model (für JSON-Save).</summary>
    public CarLayoutModel ToModel()
    {
        // Aktive Session vorher in den internen Storage zurückschreiben
        CommitCurrentSession();

        var model = new CarLayoutModel
        {
            CarName = CarName,
            CarClass = CarClass,
            IsDefault = IsDefault,
            IsFavorite = IsFavorite,
            Sessions = new List<SessionConfigModel>()
        };

        foreach (SessionType st in System.Enum.GetValues<SessionType>())
        {
            var session = new SessionConfigModel { Session = st };
            foreach (var s in GetSessionSources(st))
                session.Sources.Add(s.ToModel());
            model.Sessions.Add(session);
        }

        return model;
    }
}