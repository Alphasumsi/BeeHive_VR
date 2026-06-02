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
        // UI-State nicht persistieren (gleiche Logik wie bei den Car-Layouts).
        if (e.PropertyName is nameof(SourceViewModel.IsExpanded)
                            or nameof(SourceViewModel.IsRenaming)
                            or nameof(SourceViewModel.IsDragging)
                            or nameof(SourceViewModel.IsMatched)
                            or nameof(SourceViewModel.CaptureWidth)
                            or nameof(SourceViewModel.CaptureHeight))
            return;
        Persist();
    }
}
