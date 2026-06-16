using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR;

public partial class MainWindow : Window
{
    private bool _loaded;
    // VR-Self-Capture: Drosselt die Bounds-Pushes an Atlas (Drag feuert hunderte
    // LocationChanged-Events pro Sekunde — Atlas-Renderer macht aber nur einen
    // Style-Mutation-Roundtrip pro Push). 100 ms = 10 Hz, in VR kaum als Lag
    // wahrnehmbar.
    private System.Windows.Threading.DispatcherTimer? _wpfBoundsThrottle;
    private bool _wpfBoundsDirty;

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
        LocationChanged += MainWindow_GeometryChanged;
        SizeChanged += MainWindow_GeometryChanged;
    }

    // -------- VR-Self-Capture: Window-Bounds an Atlas pushen -----------------
    // Hintergrund: Atlas erfasst das WPF-Fenster als Custom Source. Im normalen
    // Window-Capture-Mode werden ContextMenu/ComboBox-Dropdown-HWNDs nicht
    // mit-erfasst (separate Top-Level-Popups). Atlas wechselt für die WPF-Self-
    // Source auf Display-Capture + CSS-Crop — dafür braucht er die aktuellen
    // Outer-Bounds (DPI-aware physical pixels) plus Monitor-Index.

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private void MainWindow_GeometryChanged(object? sender, System.EventArgs e)
        => RequestWpfBoundsPush();

    /// <summary>Wird auch von außen (PushCurrentStateToEngine bei Pipe-
    /// Connect) gerufen, damit Atlas direkt nach Reconnect die Bounds kennt.</summary>
    public void PushWpfBoundsNow() => PushWpfBoundsCore();

    private void RequestWpfBoundsPush()
    {
        // Throttle 100 ms — Drag-Events feuern dauerhaft, wir aggregieren.
        _wpfBoundsDirty = true;
        if (_wpfBoundsThrottle == null)
        {
            _wpfBoundsThrottle = new System.Windows.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(100),
            };
            _wpfBoundsThrottle.Tick += (_, _) =>
            {
                if (!_wpfBoundsDirty) { _wpfBoundsThrottle!.Stop(); return; }
                _wpfBoundsDirty = false;
                PushWpfBoundsCore();
            };
        }
        if (!_wpfBoundsThrottle.IsEnabled) _wpfBoundsThrottle.Start();
    }

    private void PushWpfBoundsCore()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == System.IntPtr.Zero) return;

            bool minimized = WindowState == WindowState.Minimized;
            int left = 0, top = 0, width = 0, height = 0;
            if (!minimized && GetWindowRect(hwnd, out var r))
            {
                left = r.Left; top = r.Top;
                width = r.Right - r.Left; height = r.Bottom - r.Top;
            }

            // Monitor-Index ermittelt Atlas selbst via electron.screen
            // (intersect Bounds mit jedem Display) — WPF schickt nur die
            // absoluten Outer-Bounds. monitorIndex Feld bleibt für Future-
            // Use, derzeit 0.
            BeeHiveVR.Services.EngineLink.Instance.PushWpfWindowBounds(
                left, top, width, height, 0, minimized);
        }
        catch (System.Exception ex)
        {
            BeeHiveVR.Services.Logger.Warn($"PushWpfBoundsCore failed: {ex.Message}");
        }
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
        RequestWpfBoundsPush();
    }

    private bool _connHookInstalled;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureLoaded();
        // VR-Self-Capture: initialer Bounds-Push + bei jedem Pipe-Reconnect
        // re-pushen (Atlas-Restart darf den State nicht verlieren). Hook nur
        // einmal registrieren (Loaded kann mehrfach feuern bei Hide+Show).
        PushWpfBoundsNow();
        if (!_connHookInstalled)
        {
            _connHookInstalled = true;
            BeeHiveVR.Services.EngineLink.Instance.ConnectionChanged += (_, connected) =>
            {
                if (connected) Dispatcher.BeginInvoke(new System.Action(PushWpfBoundsNow));
            };
        }
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

    // -------- Icon-Nav-Sektionen ------------------------------------------
    // Top-Group läuft generisch über NavItem_Click + Tag="{Binding Section}"
    // (siehe NavItems ItemsControl in MainWindow.xaml). Bottom-Group ist
    // hartcodiert und hat eigene Click-Handler.
    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string section)
            SetSection(section);
    }
    private void NavDebug_Click(object sender, RoutedEventArgs e) => SetSection("Debug");
    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        VM?.RegisterSettingsClickForDevMode();
        SetSection("Settings");
    }
    private void NavHelp_Click(object sender, RoutedEventArgs e) => SetSection("Help");

    private void SetSection(string section)
    {
        if (VM != null) VM.ActiveSection = section;
    }

    private void LayoutPage_Loaded(object sender, RoutedEventArgs e)
    {

    }
}