using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BeeHiveVR.Models;

namespace BeeHiveVR.ViewModels;

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

    /// <summary>
    /// EditingAllSessions ist ein UI-Marker für „nachfolgende Add/Remove
    /// gelten für alle 4 Sessions" (Bulk-Mode). Es ist KEIN Sources-Mirror-
    /// Mode mehr — Sessions bleiben pro-Session individuell, der Klick
    /// vereinheitlicht nichts. Bulk-Operationen werden explicit über
    /// <see cref="AddSourceToAllSessions"/> geroutet.
    /// </summary>
    partial void OnEditingAllSessionsChanged(bool value)
    {
        // No-op: keine Spiegel-Anlage, kein deep-clone-Detach.
        // Existierende Session-Inhalte bleiben unverändert.
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

    /// <summary>Speichert die aktuellen Sources in der aktiven Session.
    /// Greift nur auf SelectedSession — EditingAllSessions ist kein
    /// Mirror-Mode mehr (Bulk-Ops laufen über
    /// <see cref="AddSourceToAllSessions"/>).</summary>
    public void CommitCurrentSession()
    {
        _sessionsData[SelectedSession] = Sources.ToList();
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
        // Vor dem Lesen: aktive Session's UI-Edits committen, damit
        // ihre Liste den aktuellen Stand hat.
        if (st == SelectedSession) CommitCurrentSession();
        return _sessionsData[st];
    }

    /// <summary>Setzt die Sources einer beliebigen Session (überschreibt).</summary>
    public void SetSessionSources(SessionType st, List<SourceViewModel> sources)
    {
        _sessionsData[st] = sources;
    }

    /// <summary>
    /// Bulk-Add: fügt eine Source zu allen Sessions hinzu, ohne die
    /// existing Session-Listen zu überschreiben. Umgeht bewusst
    /// <see cref="CommitCurrentSession"/>, das im EditingAllSessions-Modus
    /// Sources auf alle Sessions spiegelt und damit individuelle Inhalte
    /// killen würde. Wird vom „All Sessions"-Pille-Add benutzt.
    /// </summary>
    public void AddSourceToAllSessions(SourceViewModel sv)
    {
        foreach (SessionType st in System.Enum.GetValues<SessionType>())
            _sessionsData[st].Add(sv);
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