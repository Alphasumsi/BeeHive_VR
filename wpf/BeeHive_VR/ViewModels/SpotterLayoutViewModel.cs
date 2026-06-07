using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using BeeHiveVR.Models;
using BeeHiveVR.Services;

namespace BeeHiveVR.ViewModels;

/// <summary>
/// Globales, car-unabhängiges Spotter-Overlay-Set (kein Sessions-Konzept).
/// Greift wenn man nicht selbst fährt (Replay / Spectator / Teamkollege fährt).
/// Persistiert in <see cref="SpotterStore"/>. Feuert <see cref="OverlaysChanged"/>
/// damit MainViewModel bei Bedarf neu an die Engine pusht.
/// </summary>
public partial class SpotterLayoutViewModel : ObservableObject
{
    public ObservableCollection<SourceViewModel> Sources { get; } = new();

    /// <summary>Feuert nach jeder (auch persistierten) Änderung am Set.</summary>
    public event EventHandler? OverlaysChanged;

    private bool _loading;

    public SpotterLayoutViewModel()
    {
        Sources.CollectionChanged += OnSourcesChanged;
    }

    public void Load()
    {
        _loading = true;
        try
        {
            foreach (var src in Sources) src.PropertyChanged -= OnSourcePropertyChanged;
            Sources.Clear();
            foreach (var m in SpotterStore.Load())
                Sources.Add(SourceViewModel.FromModel(m));
        }
        finally
        {
            _loading = false;
        }
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Persist()
    {
        if (_loading) return;
        SpotterStore.Save(Sources.Select(s => s.ToModel()).ToList());
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (SourceViewModel s in e.NewItems) s.PropertyChanged += OnSourcePropertyChanged;
        if (e.OldItems != null)
            foreach (SourceViewModel s in e.OldItems) s.PropertyChanged -= OnSourcePropertyChanged;
        Persist();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Akkordeon: nur eine Karte gleichzeitig aufgeklappt. Re-Entry safe
        // — die rekursiven Calls landen mit IsExpanded=false und matchen
        // den inneren Block nicht.
        if (e.PropertyName == nameof(SourceViewModel.IsExpanded)
            && sender is SourceViewModel sv && sv.IsExpanded)
        {
            foreach (var other in Sources)
            {
                if (!ReferenceEquals(other, sv) && other.IsExpanded)
                    other.IsExpanded = false;
            }
        }

        // UI-State nicht persistieren (gleiche Logik wie bei den Car-Layouts).
        if (e.PropertyName is nameof(SourceViewModel.IsExpanded)
                            or nameof(SourceViewModel.IsRenaming)
                            or nameof(SourceViewModel.IsDragging))
            return;
        Persist();
    }
}
