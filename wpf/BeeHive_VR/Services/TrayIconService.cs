using System;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace BeeHiveVR.Services;

/// <summary>
/// Tray-Icon im Notification-Area via Hardcodet.NotifyIcon.Wpf (rein WPF, kein WinForms).
/// Doppelklick = Window-Show, Rechtsklick = Menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private static TrayIconService? _instance;
    public static TrayIconService Instance => _instance ??= new TrayIconService();

    private TaskbarIcon? _icon;

    private TrayIconService() { }

    public bool IsVisible => _icon?.Visibility == Visibility.Visible;

    public void Show()
    {
        if (_icon != null) { _icon.Visibility = Visibility.Visible; return; }

        var iconSrc = LoadIconSource();
        _icon = new TaskbarIcon
        {
            ToolTipText = AppEdition.ProductName,
            IconSource = iconSrc,
        };
        _icon.TrayMouseDoubleClick += (_, _) => RestoreMainWindow();

        var menu = new System.Windows.Controls.ContextMenu();
        var showItem = new System.Windows.Controls.MenuItem { Header = "Show" };
        showItem.Click += (_, _) => RestoreMainWindow();
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current?.Shutdown();
        menu.Items.Add(exitItem);
        _icon.ContextMenu = menu;

        if (iconSrc == null)
            Logger.Warn("TrayIcon: shown WITHOUT icon (pack-URI load failed) — " +
                        "icon may be invisible. Check Assets\\bee_icon_256.ico is registered as Resource.");
        else
            Logger.Info("TrayIcon: shown (if you don't see it, check Windows tray overflow → " +
                        "'Show hidden icons' / pin it via Taskbar Settings → Other system tray icons)");
    }

    public void Hide()
    {
        if (_icon != null) _icon.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_icon != null)
        {
            _icon.Visibility = Visibility.Collapsed;
            _icon.Dispose();
            _icon = null;
        }
    }

    private static System.Windows.Media.ImageSource? LoadIconSource()
    {
        try
        {
            var uri = new Uri(AppEdition.IconPackUri, UriKind.Absolute);
            return new System.Windows.Media.Imaging.BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreMainWindow()
    {
        var mw = Application.Current?.MainWindow;
        if (mw == null) return;
        mw.Show();
        mw.WindowState = WindowState.Normal;
        mw.ShowInTaskbar = true;
        mw.Activate();
    }
}
