using System.Windows;
using System.Windows.Controls;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();

        var vm = new SettingsViewModel();
        if (Content is FrameworkElement root)
            root.DataContext = vm;
    }

    private SettingsViewModel? VM =>
        (Content as FrameworkElement)?.DataContext as SettingsViewModel;

    /// <summary>
    /// Sub-Sidebar-Klick: ActiveSubSection setzen (für die Markierung links)
    /// und zur entsprechenden Section scrollen — Section oben anschlagen lassen.
    /// </summary>
    private void SubNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string section)
            return;

        // Markierung links setzen
        VM?.SelectSubSectionCommand.Execute(section);

        // Zur Section scrollen — oben am ScrollViewer anschlagen lassen
        if (FindName($"Section_{section}") is FrameworkElement target
            && SectionsScrollViewer is ScrollViewer sv
            && SectionsHost is FrameworkElement host)
        {
            // Y-Position der Section relativ zum StackPanel-Host
            var transform = target.TransformToAncestor(host);
            var pos = transform.Transform(new Point(0, 0));
            sv.ScrollToVerticalOffset(pos.Y);
        }
    }

    /// <summary>
    /// Beim Scrollen die Section markieren, deren Oberkante zuletzt
    /// die Top-Linie des ScrollViewers passiert hat.
    /// </summary>
    private void SectionsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (SectionsHost is not FrameworkElement host) return;

        // Spacer-Höhe an Viewport anpassen, damit jede Section oben anschlagen kann
        if (e.ViewportHeightChange != 0 || BottomSpacer.Height == 0)
            BottomSpacer.Height = SectionsScrollViewer.ViewportHeight;

        var offset = SectionsScrollViewer.VerticalOffset;
        string[] names = { "General", "Startup", "Appearance", "Paths", "Keybinds", "Updates", "About", "Developer" };
        string current = "General";

        foreach (var name in names)
        {
            if (FindName($"Section_{name}") is FrameworkElement section)
            {
                var y = section.TransformToAncestor(host).Transform(new Point(0, 0)).Y;
                // Section gilt als "oben angeschlagen" wenn ihre Oberkante <= Scroll-Offset + 20px Toleranz
                if (y <= offset + 20) current = name;
                else break;
            }
        }

        VM?.SelectSubSectionCommand.Execute(current);
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
            => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "https://github.com/Alphasumsi/VR-Overlay", UseShellExecute = true });

    private void OpenConfigsFolder_Click(object sender, RoutedEventArgs e)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = BeeHiveVR.Services.ConfigPaths.ConfigsFolder, UseShellExecute = true });

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.GetDirectoryName(BeeHiveVR.Services.Logger.LogFilePath);
        if (!string.IsNullOrEmpty(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = folder, UseShellExecute = true });
    }

    private void BrowseBrowserHost_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select browser-host.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        var current = VM?.BrowserHostExecutable;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var dir = System.IO.Path.GetDirectoryName(current);
            if (System.IO.Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() == true && VM is not null)
            VM.BrowserHostExecutable = dlg.FileName;
    }

    private void BrowseAtlas_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select BeeHive_VR_Atlas.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        var current = VM?.AtlasExecutable;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var dir = System.IO.Path.GetDirectoryName(current);
            if (System.IO.Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() == true && VM is not null)
            VM.AtlasExecutable = dlg.FileName;
    }

    // ---- Developer-Section --------------------------------------------------

    private void DevReloadConfigs_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow?.DataContext is MainViewModel mvm)
        {
            mvm.LoadFromDisk();
            BeeHiveVR.Services.Logger.Info("Configs reloaded from disk (Dev)");
        }
    }

    private void DevOpenSettingsFile_Click(object sender, RoutedEventArgs e)
    {
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var folder = System.IO.Path.Combine(appData, BeeHiveVR.Services.Logger.AppDataFolderName);
        var path = System.IO.Path.Combine(folder, "settings.json");
        // Falls die Datei noch nicht existiert (frischer Start, noch nichts gespeichert) leer anlegen
        System.IO.Directory.CreateDirectory(folder);
        if (!System.IO.File.Exists(path))
            System.IO.File.WriteAllText(path, "{}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = path, UseShellExecute = true });
    }

    private void DevResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (ConfirmDialog.Show(owner, "Reset settings",
                "All settings will be reset. Layout JSON files are not affected.",
                "Reset", "Cancel"))
        {
            VM?.ResetToDefaultsCommand.Execute(null);
            BeeHiveVR.Services.Logger.Info("Settings reset to defaults (Dev)");
        }
    }

    private void CheckBox_Checked(object sender, RoutedEventArgs e)
    {

    }

    // -------- Appearance Nav-Order Drag-Reorder --------------------------
    // Pattern identisch zu LayoutPage.Overlays_* — DragOver verschiebt live,
    // Drop ist No-op. Drag startet NUR am Griff (Tag="drag").
    private System.Windows.Point _navDragStart;
    private NavItemViewModel? _navDragItem;

    private void NavOrder_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _navDragStart = e.GetPosition(null);
        _navDragItem = (e.OriginalSource is FrameworkElement { Tag: "drag" })
            ? FindNavItem(e.OriginalSource)
            : null;
    }

    private void NavOrder_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _navDragItem == null)
            return;

        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _navDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(pos.Y - _navDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _navDragItem;
        _navDragItem = null;
        DragDrop.DoDragDrop(NavOrderList, item, DragDropEffects.Move);
    }

    private void NavOrder_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NavItemViewModel)) is not NavItemViewModel dragged)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        var coll = (System.Windows.Application.Current?.MainWindow as MainWindow)?
                   .DataContext is MainViewModel mvm ? mvm.NavItems : null;
        if (coll == null) return;

        var target = FindNavItem(e.OriginalSource);
        if (target == null || ReferenceEquals(target, dragged)) return;

        int oldIndex = coll.IndexOf(dragged);
        int newIndex = coll.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;
        coll.Move(oldIndex, newIndex);   // löst PersistNavOrder im MainViewModel aus
    }

    private void NavOrder_Drop(object sender, DragEventArgs e)
    {
        // Reihenfolge wurde bereits live in DragOver übernommen.
    }

    private static NavItemViewModel? FindNavItem(object? originalSource)
    {
        var d = originalSource as System.Windows.DependencyObject;
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: NavItemViewModel n }) return n;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}