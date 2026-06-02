using System.Windows;
using System.Windows.Input;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR;

public partial class MainWindow : Window
{
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();

        // Edition-abhängiges Icon (Voll vs. Lite) — Fenster-Icon + Titelleisten-Logo.
        var icon = new System.Windows.Media.Imaging.BitmapImage(
            new System.Uri(BeeHiveVR.Services.AppEdition.IconPackUri));
        Icon = icon;
        TitleBarLogo.Source = icon;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    /// <summary>
    /// LoadFromDisk genau einmal — egal ob über Loaded-Event oder von App.OnStartup
    /// explizit gerufen (wenn Window wegen StartInTray nie gezeigt wird).
    /// </summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        VM?.LoadFromDisk();
    }

    private void MainWindow_StateChanged(object? sender, System.EventArgs e)
    {
        // Wenn StartInTray aktiv ist: Minimize geht in den Tray statt Taskbar.
        var s = BeeHiveVR.Services.SettingsStore.Current;
        if (WindowState == WindowState.Minimized && s.StartInTray)
        {
            Hide();
            ShowInTaskbar = false;
            BeeHiveVR.Services.TrayIconService.Instance.Show();
        }
        else if (WindowState != WindowState.Minimized)
        {
            ShowInTaskbar = true;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureLoaded();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        VM?.FlushPendingSaves();
        SaveWindowStateIfEnabled();
    }

    /// <summary>
    /// "Remember Window Position and Scale": bei aktivem Toggle Geometrie +
    /// UI-Scale in die settings.json schreiben. RestoreBounds liefert die
    /// Normal-Größe auch wenn maximiert/minimiert geschlossen wird.
    /// </summary>
    private void SaveWindowStateIfEnabled()
    {
        var s = BeeHiveVR.Services.SettingsStore.Current;
        if (!s.RememberWindowPositionAndScale) return;

        Rect b = (WindowState == WindowState.Normal)
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (b.Width > 0 && b.Height > 0)
        {
            s.WindowLeft = b.Left;
            s.WindowTop = b.Top;
            s.WindowWidth = b.Width;
            s.WindowHeight = b.Height;
        }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.UiScale = RootScale.ScaleX;
        BeeHiveVR.Services.SettingsStore.Save();
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    // -------- Custom Window Chrome ----------------------------------------
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleOverlaysVisible_Click(object sender, RoutedEventArgs e)
        => VM?.ToggleOverlaysVisibleCommand.Execute(null);

    // -------- Icon-Nav-Sektionen (Platzhalter — Inhalte kommen später) ----
    private void NavMenu_Click(object sender, RoutedEventArgs e) => SetSection("Menu");
    private void NavLayout_Click(object sender, RoutedEventArgs e) => SetSection("Layout");
    private void NavDebug_Click(object sender, RoutedEventArgs e) => SetSection("Debug");
    private void NavTP_Click(object sender, RoutedEventArgs e) => SetSection("Trading Paints");
    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        VM?.RegisterSettingsClickForDevMode();
        SetSection("Settings");
    }
    private void NavSupport_Click(object sender, RoutedEventArgs e) => SetSection("Support");

    private void NavDashies_Click(object sender, RoutedEventArgs e) => SetSection("Dashies");

    // Platzhalter-Sektionen (Inhalt kommt später)
    private void NavHtml_Click(object sender, RoutedEventArgs e) => SetSection("irDashies");
    private void NavAutostart_Click(object sender, RoutedEventArgs e) => SetSection("Autostart");
    private void NavButtonbox_Click(object sender, RoutedEventArgs e) => SetSection("Buttonbox");

    private void SetSection(string section)
    {
        if (VM != null) VM.ActiveSection = section;
    }

    private void LayoutPage_Loaded(object sender, RoutedEventArgs e)
    {

    }
}