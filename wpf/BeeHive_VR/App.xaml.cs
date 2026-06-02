using System.Threading;
using System.Windows;
using BeeHiveVR.Services;

namespace BeeHiveVR
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Single-Instance: hält die App-weit eindeutige Mutex + Aktivierungs-Event.
        private static Mutex? _instanceMutex;
        private static EventWaitHandle? _activateEvent;
        private const string InstanceMutexName = AppEdition.InstanceMutexName;
        private const string ActivateEventName = AppEdition.ActivateEventName;

        protected override void OnStartup(StartupEventArgs e)
        {
            // ToolTip-Anzeigeverzögerung global auf 300 ms (Default ist 400 ms).
            // Muss vor dem ersten UI-Aufbau gesetzt werden.
            System.Windows.Controls.ToolTipService.InitialShowDelayProperty
                .OverrideMetadata(typeof(FrameworkElement),
                    new FrameworkPropertyMetadata(300));

            // Logger als allererstes initialisieren — alle nachfolgenden
            // Operationen können dann Logger.Info / Warn / Error nutzen.
            Logger.Initialize();

            // --- Single-Instance: nur EINE WPF-App gleichzeitig ---
            // MUSS vor dem browser-host-Orphan-Cleanup unten stehen: eine zweite Instanz
            // darf diese Bereinigung NICHT laufen lassen (würde die browser-host-Prozesse
            // der ersten Instanz killen). Zweite Instanz signalisiert die erste und beendet
            // sich SOFORT via Environment.Exit (kein OnExit → kein Service-Teardown).
            _instanceMutex = new Mutex(true, InstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
                    {
                        ev.Set();
                        ev.Dispose();
                    }
                }
                catch { /* best effort */ }
                Logger.Info("Second instance detected — activating existing window and exiting.");
                System.Environment.Exit(0);
                return;
            }
            // Erste Instanz: auf das Aktivierungs-Signal späterer Starts hören → Fenster nach vorn.
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                var activateThread = new Thread(() =>
                {
                    while (true)
                    {
                        _activateEvent.WaitOne();
                        Dispatcher.BeginInvoke(new System.Action(ActivateExistingWindow));
                    }
                })
                { IsBackground = true, Name = "SingleInstanceActivate" };
                activateThread.Start();
            }
            catch (System.Exception ex)
            {
                Logger.Warn($"Single-instance activate listener failed: {ex.Message}");
            }

            // Settings laden — VOR den Services damit Pfade & Toggles greifen.
            SettingsStore.Load();

            base.OnStartup(e);

            // iRacing-Service starten
            IRacingService.Instance.Start();

            // Named-Pipe-Server für die Engine-Verbindung
            EngineLink.Instance.Start();

            // Globaler Keybind-Input-Hook (Keyboard + HID, hintergrundfähig)
            RawInputService.Instance.Start();
            KeybindManager.Instance.Load(); // Bindings aus settings.json + Resolver aktiv

            // irdashies-Adapter: HTTP + WebSocket auf einem Port (Häppchen 1: Skelett)
            IrdashiesAdapterService.Instance.Start();

            // Trading-Paints-Downloader (hooked an IRacingService.SessionInfoUpdated)
            TradingPaintsService.Instance.Start();
            if (SettingsStore.Current.TradingPaintsCleanupOnStartup)
            {
                try
                {
                    var r = TradingPaintsService.Instance.Cleanup(
                        SettingsStore.Current.TradingPaintsAutoCleanupDays);
                    Logger.Info(
                        $"TradingPaints startup-cleanup: {r.FilesDeleted} files / " +
                        $"{r.BytesDeleted / 1024} KB / {r.FoldersDeleted} folders / {r.Errors} errors");
                }
                catch (System.Exception ex)
                {
                    Logger.Warn($"TradingPaints startup-cleanup failed: {ex.Message}");
                }
            }

            // MainWindow manuell erzeugen (kein StartupUri) damit StartInTray das Fenster
            // gar nicht erst sichtbar macht — kein schwarzer Flash.
            var window = new MainWindow();
            Current.MainWindow = window;
            window.EnsureLoaded(); // LoadFromDisk explizit (auch ohne Show)

            // "Remember Window Position and Scale": Geometrie + UI-Scale wiederherstellen.
            var st = SettingsStore.Current;
            if (st.RememberWindowPositionAndScale)
            {
                double scale = System.Math.Clamp(st.UiScale <= 0 ? 1.0 : st.UiScale, 0.75, 1.50);
                window.RootScale.ScaleX = scale;
                window.RootScale.ScaleY = scale;

                if (st.WindowWidth > 0 && st.WindowHeight > 0)
                {
                    double vsL = SystemParameters.VirtualScreenLeft;
                    double vsT = SystemParameters.VirtualScreenTop;
                    double vsR = vsL + SystemParameters.VirtualScreenWidth;
                    double vsB = vsT + SystemParameters.VirtualScreenHeight;
                    // Nur wiederherstellen wenn das Fenster mindestens teilweise
                    // auf einem (ggf. inzwischen entfernten) Monitor läge.
                    bool onScreen = st.WindowLeft < vsR && st.WindowLeft + st.WindowWidth > vsL
                                 && st.WindowTop < vsB && st.WindowTop + st.WindowHeight > vsT;
                    if (onScreen)
                    {
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Left = st.WindowLeft;
                        window.Top = st.WindowTop;
                        window.Width = st.WindowWidth;
                        window.Height = st.WindowHeight;
                    }
                }

                if (st.WindowMaximized)
                    window.WindowState = WindowState.Maximized;
            }

            // CLI-Args überstimmen die Settings (gewinnt: tray > minimized > normal).
            // Akzeptiert: "min"/"--minimized"/"/min", "tray"/"--tray", "normal"/"--normal".
            var (forceMin, forceTray, forceNormal) = ParseStartupArgs(e.Args);
            if (e.Args is { Length: > 0 })
                Logger.Info($"CLI args: [{string.Join(' ', e.Args)}] → " +
                            $"forceMin={forceMin} forceTray={forceTray} forceNormal={forceNormal}");

            // STARTUPINFO-Hint vom Launcher (z.B. AHK Run(..., , "Min") oder
            // Verknüpfung "Run: Minimized"). WPF konsumiert den Hint nicht
            // zuverlässig — wir lesen ihn selbst und behandeln SW_SHOWMINIMIZED &
            // Verwandte wie ein "min"-Arg.
            try
            {
                var hint = ReadStartupShowHint();
                if (hint is uint sw)
                {
                    Logger.Info($"STARTUPINFO.wShowWindow={sw} (launcher minimize/hide hint)");
                    // 2=SW_SHOWMINIMIZED 6=SW_MINIMIZE 7=SW_SHOWMINNOACTIVE 11=SW_FORCEMINIMIZE
                    if (sw is 2u or 6u or 7u or 11u) forceMin = true;
                    // 0=SW_HIDE — keiner will einen Geist-Prozess; auf "min" downgraden.
                    else if (sw == 0u) forceMin = true;
                }
            }
            catch (System.Exception ex)
            {
                Logger.Warn($"STARTUPINFO read failed: {ex.Message}");
            }

            bool wantTray = !forceNormal && (forceTray || (!forceMin && SettingsStore.Current.StartInTray));
            bool wantMin  = !forceNormal && !wantTray && (forceMin || SettingsStore.Current.StartMinimized);

            if (wantTray)
            {
                TrayIconService.Instance.Show();
                // Fenster bleibt versteckt — Tray-Icon ist die einzige Sichtbarkeit.
                // Falls Tray-Icon nicht da ist (im System-Tray-Overflow oder Load-Fail),
                // gibt TrayIconService einen Warn-Log aus.
            }
            else
            {
                if (wantMin)
                    window.WindowState = WindowState.Minimized;
                window.Show();
            }
        }

        /// <summary>Holt das vorhandene Fenster nach vorn (auch aus Tray/minimiert),
        /// ausgelöst wenn eine zweite Instanz gestartet wurde.</summary>
        private void ActivateExistingWindow()
        {
            var w = Current?.MainWindow;
            if (w == null) return;
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Show(); // im Tray-Modus war das Fenster nur versteckt
            w.Activate();
            w.Topmost = true;
            w.Topmost = false;
            w.Activate();
        }

        // ---- STARTUPINFO.wShowWindow lesen ------------------------------------
        // Liefert das wShowWindow-Feld nur wenn der Launcher STARTF_USESHOWWINDOW
        // gesetzt hat (sonst null = "kein Hint, Settings folgen").

        // String-Felder bewusst als IntPtr — der CLR-Marshaller würde sonst beim
        // Cleanup CoTaskMemFree auf OS-eigene Pointer rufen (Heap-Korruption oder
        // TypeLoadException beim JIT). Wir lesen nur dwFlags + wShowWindow.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public uint cb;
            public System.IntPtr lpReserved;
            public System.IntPtr lpDesktop;
            public System.IntPtr lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public System.IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern void GetStartupInfoW(out STARTUPINFO lpStartupInfo);

        private const uint STARTF_USESHOWWINDOW = 0x00000001;

        private static uint? ReadStartupShowHint()
        {
            GetStartupInfoW(out var si);
            if ((si.dwFlags & STARTF_USESHOWWINDOW) == 0) return null;
            return si.wShowWindow;
        }

        /// <summary>
        /// Liest CLI-Tokens (case-insensitive, mit/ohne <c>--</c>/<c>/</c>-Prefix).
        /// Unbekannte Tokens werden still ignoriert.
        /// </summary>
        private static (bool min, bool tray, bool normal) ParseStartupArgs(string[] args)
        {
            bool min = false, tray = false, normal = false;
            foreach (var raw in args ?? System.Array.Empty<string>())
            {
                var tok = raw.TrimStart('-', '/').ToLowerInvariant();
                switch (tok)
                {
                    case "min":
                    case "minimized":
                    case "start-minimized":
                        min = true; break;
                    case "tray":
                    case "start-in-tray":
                        tray = true; break;
                    case "normal":
                        normal = true; break;
                }
            }
            return (min, tray, normal);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            TrayIconService.Instance.Dispose();
            TradingPaintsService.Instance.Stop();
            // Preview vor dem Adapter beenden — die Vorschau lädt aus dem Adapter,
            // umgekehrte Reihenfolge würde Renderer-Errors loggen.
            DashiesPreviewService.Instance.Close();
            IrdashiesAdapterService.Instance.Stop();
            RawInputService.Instance.Stop();
            EngineLink.Instance.Stop();
            IRacingService.Instance.Stop();
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
            _activateEvent?.Dispose();
            base.OnExit(e);
        }
    }
}