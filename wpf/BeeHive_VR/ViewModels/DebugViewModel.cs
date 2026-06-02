using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BeeHiveVR.Services;

namespace BeeHiveVR.ViewModels;

/// <summary>
/// ViewModel für die Debug-Console-Page.
/// Gefilterte Sicht auf den globalen Logger.Entries-Buffer.
/// </summary>
public partial class DebugViewModel : ObservableObject
{
    /// <summary>Gefilterte Sicht — UI bindet hier.</summary>
    public ICollectionView EntriesView { get; }

    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarn = true;
    [ObservableProperty] private bool _showError = true;

    /// <summary>Auto-Scroll zum neuesten Eintrag (vom Code-Behind benutzt).</summary>
    [ObservableProperty] private bool _autoScroll = true;

    public DebugViewModel()
    {
        EntriesView = CollectionViewSource.GetDefaultView(Logger.Entries);
        EntriesView.Filter = FilterEntry;
    }

    private bool FilterEntry(object item)
    {
        if (item is not LogEntry entry) return false;
        return entry.Level switch
        {
            LogLevel.Info => ShowInfo,
            LogLevel.Warn => ShowWarn,
            LogLevel.Error => ShowError,
            _ => true
        };
    }

    // Bei jeder Filter-Änderung neu filtern
    partial void OnShowInfoChanged(bool value) => EntriesView.Refresh();
    partial void OnShowWarnChanged(bool value) => EntriesView.Refresh();
    partial void OnShowErrorChanged(bool value) => EntriesView.Refresh();

    [RelayCommand]
    private void Clear()
    {
        Logger.Entries.Clear();
    }
}