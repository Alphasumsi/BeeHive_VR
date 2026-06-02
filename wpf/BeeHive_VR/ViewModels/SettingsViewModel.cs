using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BeeHiveVR;
using BeeHiveVR.Services;

namespace BeeHiveVR.ViewModels;

/// <summary>
/// ViewModel für die Settings-Page. Persistenz pro Property-Änderung
/// nach %LOCALAPPDATA%\BeeHiveVR\settings.json über <see cref="SettingsStore"/>.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    // --- Sub-Tab-Navigation (UI-State, nicht persistent) ----------------
    /// <summary>Welcher Sub-Tab in der Settings-Page aktiv ist.</summary>
    [ObservableProperty] private string _activeSubSection = "General";

    /// <summary>Wechselt den aktiven Sub-Tab — vom Sub-Sidebar-Klick aufgerufen.</summary>
    [RelayCommand]
    private void SelectSubSection(string section)
    {
        if (!string.IsNullOrEmpty(section))
            ActiveSubSection = section;
    }

    // --- General ---------------------------------------------------------
    [ObservableProperty] private bool _autoCreateLayoutOnNewCar = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _startInTray;
    [ObservableProperty] private bool _rememberWindowPositionAndScale;
    [ObservableProperty] private string _startPage = "Layout";

    /// <summary>Optionen für das Start-Page-Dropdown.</summary>
    public string[] StartPageOptions { get; } = new[] { "Menu", "Layout" };

    // --- Keybinds --------------------------------------------------------
    /// <summary>Eine Zeile pro bindbarer Aktion (aus dem Katalog).</summary>
    public System.Collections.Generic.List<KeybindRowViewModel> Keybinds { get; }
        = BuildKeybindRows();

    private static System.Collections.Generic.List<KeybindRowViewModel> BuildKeybindRows()
    {
        var list = new System.Collections.Generic.List<KeybindRowViewModel>();
        foreach (var info in KeybindCatalog.All)
            list.Add(new KeybindRowViewModel(info));
        return list;
    }

    /// <summary>Pfad zur Log-Datei (read-only).</summary>
    public string LogFilePath => Logger.LogFilePath;

    /// <summary>App-Version für die Anzeige oben rechts.</summary>
    public string AppVersion => AppInfo.VersionDisplay;

    /// <summary>Build-Datum für die Dev-Section.</summary>
    public string BuildDate => AppInfo.BuildDate;

    /// <summary>Working directory für die Dev-Section.</summary>
    public string WorkingDirectory => AppInfo.WorkingDirectory;

    // --- Engine ----------------------------------------------------------
    [ObservableProperty] private string _browserHostExecutable = "";

    // --- Icon-Nav Sichtbarkeit (Appearance) ------------------------------
    [ObservableProperty] private bool _showTradingPaints = true;
    [ObservableProperty] private bool _showHtmlOverlays;
    [ObservableProperty] private bool _showAutostart;
    [ObservableProperty] private bool _showButtonbox;
    [ObservableProperty] private bool _showDashies;

    // --- UI --------------------------------------------------------------
    [ObservableProperty] private double _uiScale = 1.0;

    // --- Dev / Preview ---------------------------------------------------
    [ObservableProperty] private bool _useLegacyLayoutBar;

    private const double UiScaleMin = 0.75;
    private const double UiScaleMax = 1.50;
    private const double UiScaleStep = 0.05;

    /// <summary>UiScale in Prozent für die Anzeige zwischen den Buttons.</summary>
    public string UiScalePercent => $"{(int)System.Math.Round(UiScale * 100)} %";

    [RelayCommand]
    private void UiScaleDecrease()
        => UiScale = System.Math.Max(UiScaleMin, System.Math.Round((UiScale - UiScaleStep) * 100) / 100);

    [RelayCommand]
    private void UiScaleIncrease()
        => UiScale = System.Math.Min(UiScaleMax, System.Math.Round((UiScale + UiScaleStep) * 100) / 100);

    partial void OnUiScaleChanged(double value)
    {
        OnPropertyChanged(nameof(UiScalePercent));
        // Aufs RootGrid des MainWindow durchschlagen
        if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
        {
            mw.RootScale.ScaleX = value;
            mw.RootScale.ScaleY = value;
        }
    }

    /// <summary>
    /// Während die Settings beim Konstruktor aus dem Store reinkopiert werden,
    /// sollen die Auto-Save-Hooks nicht feuern (sonst saven wir nutzlos beim Start).
    /// </summary>
    private bool _suppressAutoSave = true;

    public SettingsViewModel()
    {
        var s = SettingsStore.Current;
        AutoCreateLayoutOnNewCar = s.AutoCreateLayoutOnNewCar;
        StartWithWindows = s.StartWithWindows;
        StartMinimized = s.StartMinimized;
        StartInTray = s.StartInTray;
        RememberWindowPositionAndScale = s.RememberWindowPositionAndScale;
        StartPage = s.StartPage;
        BrowserHostExecutable = s.BrowserHostExecutable;
        ShowTradingPaints = s.ShowTradingPaints;
        ShowHtmlOverlays = s.ShowHtmlOverlays;
        ShowAutostart = s.ShowAutostart;
        ShowButtonbox = s.ShowButtonbox;
        ShowDashies = s.ShowDashies;
        UseLegacyLayoutBar = s.UseLegacyLayoutBar;

        // UI-Scale nur übernehmen wenn "Remember"-Toggle an ist — sonst Default 1.0.
        // (Beim App-Start wird RootScale direkt in App.OnStartup gesetzt; hier nur
        //  damit der Appearance-Slider den richtigen Wert zeigt.)
        UiScale = s.RememberWindowPositionAndScale
            ? System.Math.Clamp(s.UiScale <= 0 ? 1.0 : s.UiScale, UiScaleMin, UiScaleMax)
            : 1.0;

        // Ab jetzt: jede Property-Änderung speichert + greift Routine
        _suppressAutoSave = false;
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        AutoCreateLayoutOnNewCar = true;
        StartWithWindows = false;
        StartMinimized = false;
        StartInTray = false;
        RememberWindowPositionAndScale = false;
        StartPage = "Layout";
        BrowserHostExecutable = "";
        ShowTradingPaints = true;
        ShowHtmlOverlays = false;
        ShowAutostart = false;
        ShowButtonbox = false;
        ShowDashies = false;
        UseLegacyLayoutBar = false;
        // Auto-Save-Hooks feuern für jeden Setter — abschließend nochmal explizit speichern.
        PersistAll();
    }

    /// <summary>Schreibt alle aktuellen Werte ins Store + persistiert.</summary>
    private void PersistAll()
    {
        var s = SettingsStore.Current;
        s.AutoCreateLayoutOnNewCar = AutoCreateLayoutOnNewCar;
        s.StartWithWindows = StartWithWindows;
        s.StartMinimized = StartMinimized;
        s.StartInTray = StartInTray;
        s.RememberWindowPositionAndScale = RememberWindowPositionAndScale;
        s.StartPage = StartPage;
        s.BrowserHostExecutable = BrowserHostExecutable;
        s.ShowTradingPaints = ShowTradingPaints;
        s.ShowHtmlOverlays = ShowHtmlOverlays;
        s.ShowAutostart = ShowAutostart;
        s.ShowButtonbox = ShowButtonbox;
        s.ShowDashies = ShowDashies;
        s.UseLegacyLayoutBar = UseLegacyLayoutBar;
        SettingsStore.Save();
    }

    // ---- Auto-Save + Side-Effects ------------------------------------------

    partial void OnAutoCreateLayoutOnNewCarChanged(bool value)
    {
        if (_suppressAutoSave) return;
        // Live an die MainViewModel spiegeln — Sidebar versteckt/zeigt den Default-Eintrag.
        if (System.Windows.Application.Current?.MainWindow is MainWindow mw
            && mw.DataContext is MainViewModel mvm)
        {
            mvm.AutoCreateLayoutOnNewCar = value;
        }
        AutoSave();
    }
    partial void OnStartPageChanged(string value) => AutoSave();
    partial void OnBrowserHostExecutableChanged(string value) => AutoSave();

    // Dev-Toggle: live an die MainViewModel spiegeln (gleiches Muster wie SyncNav).
    partial void OnUseLegacyLayoutBarChanged(bool value)
    {
        if (_suppressAutoSave) return;
        if (System.Windows.Application.Current?.MainWindow is MainWindow mw
            && mw.DataContext is MainViewModel mvm)
        {
            mvm.UseLegacyLayoutBar = value;
        }
        AutoSave();
    }

    // Icon-Nav-Sichtbarkeit: persistieren + live an die MainViewModel spiegeln
    // (die Nav-Buttons binden dort, gleiche Mechanik wie UiScale → MainWindow).
    partial void OnShowTradingPaintsChanged(bool value) { SyncNav(); AutoSave(); }
    partial void OnShowHtmlOverlaysChanged(bool value) { SyncNav(); AutoSave(); }
    partial void OnShowAutostartChanged(bool value) { SyncNav(); AutoSave(); }
    partial void OnShowButtonboxChanged(bool value) { SyncNav(); AutoSave(); }
    partial void OnShowDashiesChanged(bool value) { SyncNav(); AutoSave(); }

    private void SyncNav()
    {
        if (_suppressAutoSave) return; // Konstruktor-Load: MainVM liest selbst aus dem Store
        if (System.Windows.Application.Current?.MainWindow is MainWindow mw
            && mw.DataContext is MainViewModel mvm)
        {
            mvm.ShowTradingPaints = ShowTradingPaints;
            mvm.ShowHtmlOverlays = ShowHtmlOverlays;
            mvm.ShowAutostart = ShowAutostart;
            mvm.ShowButtonbox = ShowButtonbox;
            mvm.ShowDashies = ShowDashies;
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressAutoSave) return;
        // Routine: Registry-Eintrag setzen/entfernen
        if (value) StartupHelper.Enable();
        else StartupHelper.Disable();
        AutoSave();
    }

    partial void OnStartMinimizedChanged(bool value) => AutoSave();
    partial void OnRememberWindowPositionAndScaleChanged(bool value) => AutoSave();

    partial void OnStartInTrayChanged(bool value)
    {
        if (_suppressAutoSave) return;
        // Tray sofort sichtbar machen oder verstecken
        if (value) TrayIconService.Instance.Show();
        else TrayIconService.Instance.Hide();
        AutoSave();
    }

    private void AutoSave()
    {
        if (_suppressAutoSave) return;
        PersistAll();
    }
}