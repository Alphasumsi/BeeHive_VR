using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace BeeHiveVR.Views;

/// <summary>
/// Schritt 2b des Add-overlay-Flows (Window-Variante).
/// User gibt Name ein und wählt aus der Liste der offenen Top-Level-Fenster.
/// Standard zeigt nur "App-Windows" (was die Taskbar/Task-Manager-Apps-Tab zeigen würde);
/// Toggle "Extended" zeigt alle EnumWindows-Treffer inkl. Tool-/System-Fenster.
/// </summary>
public partial class AddOverlayWindowDialog : Window
{
    public string OverlayName { get; private set; } = "";

    /// <summary>Fenstertitel des gewählten Fensters (kommt in SourceModel.Target).</summary>
    public string OverlayTarget { get; private set; } = "";

    /// <summary>Pixel-Maße des gewählten Fensters zum Anlege-Zeitpunkt (für initiale Scale).</summary>
    public int SelectedWindowPixelWidth { get; private set; }
    public int SelectedWindowPixelHeight { get; private set; }

    public AddOverlayWindowDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshWindowList();
            NameBox.Focus();
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();
    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Validate();

    private void Extended_Changed(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void Validate()
    {
        AddButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text)
                           && WindowCombo.SelectedItem is WindowEntry;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCombo.SelectedItem is not WindowEntry entry) return;
        OverlayName = NameBox.Text.Trim();
        OverlayTarget = entry.Title;
        SelectedWindowPixelWidth = entry.PixelWidth;
        SelectedWindowPixelHeight = entry.PixelHeight;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ====================================================================
    // P/Invoke: sichtbare Top-Level-Fenster aufzählen
    // ====================================================================

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const int GWL_EXSTYLE = -20;
    private const uint GW_OWNER = 4;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// Heuristik wie Windows die Taskbar/Apps-Tab füllt:
    /// - WS_EX_APPWINDOW gesetzt → ist App-Window
    /// - WS_EX_TOOLWINDOW gesetzt → niemals App-Window
    /// - sonst: nur App-Window wenn kein Owner-Fenster vorhanden
    /// </summary>
    private static bool IsAppWindow(IntPtr hWnd)
    {
        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_APPWINDOW) != 0) return true;
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
        return GetWindow(hWnd, GW_OWNER) == IntPtr.Zero;
    }

    private void RefreshWindowList()
    {
        var ownHandle = new WindowInteropHelper(this).Handle;
        if (ownHandle == IntPtr.Zero)
            ownHandle = new WindowInteropHelper(Application.Current.MainWindow).Handle;

        bool extended = ExtendedToggle?.IsChecked == true;

        var found = new List<WindowEntry>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (hWnd == ownHandle) return true;

            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            // Standard: nur App-Windows (Task-Manager-Apps-Konvention)
            if (!extended && !IsAppWindow(hWnd)) return true;

            // Prozessname für Anzeige
            string procName = "";
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName + ".exe";
            }
            catch
            {
                procName = "?";
            }

            int pxW = 0, pxH = 0;
            if (GetWindowRect(hWnd, out var rc))
            {
                pxW = rc.Right - rc.Left;
                pxH = rc.Bottom - rc.Top;
            }

            found.Add(new WindowEntry { Title = title, ProcessName = procName,
                                        PixelWidth = pxW, PixelHeight = pxH });
            return true;
        }, IntPtr.Zero);

        // Eigenes Fenster bewusst NICHT rausfiltern — App im VR als Window-Source
        // beobachten zu können ist gewollt; WGC-Selbst-Capture ist unkritisch.

        found.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

        WindowCombo.ItemsSource = found;
        if (found.Count > 0) WindowCombo.SelectedIndex = 0;
        Validate();
    }

    /// <summary>Eintrag in der ComboBox. ToString() steuert die Anzeige.</summary>
    public sealed class WindowEntry
    {
        public string Title { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }

        public override string ToString() => $"{Title}  —  {ProcessName}";
    }
}
