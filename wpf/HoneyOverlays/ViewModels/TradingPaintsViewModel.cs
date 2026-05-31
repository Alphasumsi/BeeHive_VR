using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoneyOverlays.Services;

namespace HoneyOverlays.ViewModels;

/// <summary>
/// ViewModel für die TPPage (Trading Paints).
/// Persistenz: jede Property-Änderung -> SettingsStore.Current + Save() (gleiches
/// Pattern wie <see cref="SettingsViewModel"/>).
/// </summary>
public partial class TradingPaintsViewModel : ObservableObject
{
    private bool _suppressAutoSave = true;

    // --- Bound properties -------------------------------------------------
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _maxDownloadKbpsText = "1024";
    [ObservableProperty] private string _autoCleanupDaysText = "30";
    [ObservableProperty] private bool _cleanupOnStartup;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Default-Pfad — fürs Watermark/Reset-Button.</summary>
    public string DefaultFolder => TradingPaintsService.DefaultFolder;

    /// <summary>Effektiver Pfad (Folder oder Default) — für die Anzeige.</summary>
    public string EffectiveFolder => string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder;

    public TradingPaintsViewModel()
    {
        var s = SettingsStore.Current;
        Enabled = s.TradingPaintsEnabled;
        Folder = s.TradingPaintsFolder;
        MaxDownloadKbpsText = s.TradingPaintsMaxDownloadKbps.ToString(CultureInfo.InvariantCulture);
        AutoCleanupDaysText = s.TradingPaintsAutoCleanupDays.ToString(CultureInfo.InvariantCulture);
        CleanupOnStartup = s.TradingPaintsCleanupOnStartup;
        _suppressAutoSave = false;
    }

    // --- Auto-Save hooks --------------------------------------------------

    partial void OnEnabledChanged(bool value)
    {
        if (_suppressAutoSave) return;
        SettingsStore.Current.TradingPaintsEnabled = value;
        SettingsStore.Save();
        if (value) TradingPaintsService.Instance.Kick();
        StatusText = value ? "Downloader enabled" : "Downloader disabled";
    }

    partial void OnFolderChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveFolder));
        if (_suppressAutoSave) return;
        SettingsStore.Current.TradingPaintsFolder = value ?? "";
        SettingsStore.Save();
    }

    partial void OnMaxDownloadKbpsTextChanged(string value)
    {
        if (_suppressAutoSave) return;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps) && kbps >= 0)
        {
            SettingsStore.Current.TradingPaintsMaxDownloadKbps = kbps;
            SettingsStore.Save();
        }
    }

    partial void OnAutoCleanupDaysTextChanged(string value)
    {
        if (_suppressAutoSave) return;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            SettingsStore.Current.TradingPaintsAutoCleanupDays = days;
            SettingsStore.Save();
        }
    }

    partial void OnCleanupOnStartupChanged(bool value)
    {
        if (_suppressAutoSave) return;
        SettingsStore.Current.TradingPaintsCleanupOnStartup = value;
        SettingsStore.Save();
    }

    // --- Commands ---------------------------------------------------------

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Trading Paints folder",
            InitialDirectory = EffectiveFolder
        };
        if (dlg.ShowDialog() == true)
        {
            Folder = dlg.FolderName;
        }
    }

    [RelayCommand]
    private void ResetFolderToDefault()
    {
        Folder = "";
        StatusText = "Folder reset to default";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var path = EffectiveFolder;
            if (!System.IO.Directory.Exists(path))
                System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            StatusText = $"Open folder failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CleanupNow()
    {
        if (!int.TryParse(AutoCleanupDaysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days <= 0)
        {
            StatusText = "Cleanup: please enter a positive number of days";
            return;
        }
        var r = TradingPaintsService.Instance.Cleanup(days);
        StatusText =
            $"Cleanup: {r.FilesDeleted} files removed " +
            $"({r.BytesDeleted / 1024} KB, {r.FoldersDeleted} folders, {r.Errors} errors)";
    }
}
