using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BeeHiveVR.Models;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR.Views;

/// <summary>
/// Hauptbereich der Layout-Sektion: Sessions, Overlay-Cards mit Slidern,
/// Bottom-Bar (Add/Paste overlay).
/// DataContext = MainViewModel (geerbt vom Window). Der innere ScrollViewer
/// schaltet auf SelectedLayout (CarLayoutViewModel) um.
/// </summary>
public partial class LayoutPage : UserControl
{
    public LayoutPage()
    {
        InitializeComponent();
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    // -------- Source/Overlay ... Menü beim Linksklick öffnen --------------
    private SourceViewModel? GetOverlayFromMenu(object sender)
    {
        if (sender is MenuItem mi &&
            mi.Parent is ContextMenu cm &&
            cm.PlacementTarget is Button btn &&
            btn.Tag is SourceViewModel src)
        {
            return src;
        }
        return null;
    }

    private void OverlayMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;

            // Dynamisch IsEnabled für "Paste position" abhängig vom Clipboard
            foreach (var item in btn.ContextMenu.Items.OfType<MenuItem>())
            {
                if ((item.Header as string) == "Paste position")
                    item.IsEnabled = VM?.HasPositionClipboard ?? false;
            }

            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OverlayMenu_CopyOverlay_Click(object sender, RoutedEventArgs e)
    {
        var src = GetOverlayFromMenu(sender);
        if (src != null) VM?.CopyOverlayCommand.Execute(src);
    }

    private void OverlayMenu_CopyPosition_Click(object sender, RoutedEventArgs e)
    {
        var src = GetOverlayFromMenu(sender);
        if (src != null) VM?.CopyPositionCommand.Execute(src);
    }

    private void OverlayMenu_PastePosition_Click(object sender, RoutedEventArgs e)
    {
        var src = GetOverlayFromMenu(sender);
        if (src != null) VM?.PastePositionCommand.Execute(src);
    }

    private void OverlayMenu_Remove_Click(object sender, RoutedEventArgs e)
    {
        var src = GetOverlayFromMenu(sender);
        if (src != null) VM?.RemoveOverlayCommand.Execute(src);
    }

    // -------- Nudge-Buttons (Slider-Feinschritt) --------------------------

    /// <summary>
    /// −/+ Buttons neben den Slidern. Tag = "&lt;Feld&gt;:&lt;-|+&gt;" (z.B. "Yaw:-").
    /// Schritt: SHIFT = fein (÷10), CTRL = grob (×10), sonst normal. SHIFT
    /// schaltet zusätzlich eine Dezimalstelle Anzeigegenauigkeit dazu, damit
    /// die Mini-Schritte nicht wegrunden.
    /// </summary>
    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not SourceViewModel src
            || btn.Tag is not string tag)
            return;

        var parts = tag.Split(':');
        if (parts.Length != 2) return;
        string field = parts[0];
        bool minus = parts[1] == "-";
        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        // Priorität: SHIFT > CTRL > Default.
        float pick(float fine, float normal, float coarse)
            => shift ? fine : ctrl ? coarse : normal;
        int dec(int normal) => shift ? normal + 1 : normal;

        switch (field)
        {
            case "X":
                src.X = Nudge(src.X, pick(0.001f, 0.01f, 0.10f), minus, -2f, 2f, dec(2));
                break;
            case "Y":
                src.Y = Nudge(src.Y, pick(0.001f, 0.01f, 0.10f), minus, -2f, 2f, dec(2));
                break;
            case "Z":
                src.Z = Nudge(src.Z, pick(0.001f, 0.01f, 0.10f), minus, -3f, 0f, dec(2));
                break;
            case "Yaw":
                // 5.6.2026: Range auf ±90° (war ±180°) — über 90° dreht der Quad
                // sich mit der Rückseite zum User. Layer clampt im Tilt-Drag analog.
                src.Yaw = Nudge(src.Yaw, pick(0.1f, 1f, 5f), minus, -90f, 90f, dec(0));
                break;
            case "Pitch":
                src.Pitch = Nudge(src.Pitch, pick(0.1f, 1f, 5f), minus, -90f, 90f, dec(0));
                break;
            case "Scale":
                // Scale ist roh in Metern; Slider/Textbox zeigen ×100 (1–50 ⇒ 0.01–0.50 m).
                // Anzeige-Schritt: normal 1, Ctrl 5, Shift 0.1  ⇒  roh 0.01/0.05/0.001.
                src.Scale = Nudge(src.Scale, pick(0.001f, 0.01f, 0.05f), minus, 0.01f, 0.50f, dec(2));
                break;
            case "Opacity":
                // Anzeige in % (0–100); Schritt normal 1 %, Ctrl 10 %, Shift 0.1 %.
                src.Opacity = Nudge(src.Opacity, pick(0.001f, 0.01f, 0.10f), minus, 0f, 1f, dec(2));
                break;
            case "DashieBgOpacity":
                // Hintergrund-Box im Dashie-Widget, gleiche Schrittweite wie Quad-Opacity.
                src.DashieBgOpacity = Nudge(src.DashieBgOpacity, pick(0.001f, 0.01f, 0.10f), minus, 0f, 1f, dec(2));
                break;
        }
    }

    private static float Nudge(float current, float step, bool minus,
                               float min, float max, int decimals)
    {
        if (minus) step = -step;
        float v = System.Math.Clamp(current + step, min, max);
        return (float)System.Math.Round(v, decimals);
    }

    // -------- Source-Reihenfolge (Z-Order) per Drag (wie Dashies) ---------
    // Reihenfolge in EditSources = Z-Order (oben = VR-Vordergrund).
    // ObservableCollection.Move löst Sources_CollectionChanged aus →
    // Auto-Save + Engine-Push laufen automatisch mit.

    private Point _ovDragStart;
    private SourceViewModel? _ovDragItem;
    private ItemsControl? _ovDragList;

    private void Overlays_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ovDragStart = e.GetPosition(null);
        _ovDragList = sender as ItemsControl;
        // Drag startet NUR am Griff (Tag="drag").
        _ovDragItem = (e.OriginalSource is FrameworkElement { Tag: "drag" })
            ? FindOverlay(e.OriginalSource)
            : null;
    }

    private void Overlays_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _ovDragItem == null || _ovDragList == null)
            return;

        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _ovDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(pos.Y - _ovDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _ovDragItem;
        _ovDragItem = null;

        // Amber-Hervorhebung der gezogenen Karte (wie Dashies). DoDragDrop blockiert
        // bis Drop/Abbruch → im finally zurücksetzen.
        item.IsDragging = true;
        try { DragDrop.DoDragDrop(_ovDragList, item, DragDropEffects.Move); }
        finally { item.IsDragging = false; }
    }

    private void Overlays_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SourceViewModel)) is not SourceViewModel dragged)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        var coll = VM?.EditSources;
        if (coll == null) return;

        var target = FindOverlay(e.OriginalSource);
        if (target == null || ReferenceEquals(target, dragged)) return;

        int oldIndex = coll.IndexOf(dragged);
        int newIndex = coll.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;
        coll.Move(oldIndex, newIndex);  // löst Auto-Save + Engine-Push aus
    }

    private void Overlays_Drop(object sender, DragEventArgs e)
    {
        // Reihenfolge wurde bereits live in DragOver übernommen.
    }

    private static SourceViewModel? FindOverlay(object? originalSource)
    {
        var d = originalSource as DependencyObject;
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: SourceViewModel s }) return s;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // -------- Bottom-Bar (Add / Paste) ------------------------------------

    private void PasteOverlay_Click(object sender, RoutedEventArgs e)
    {
        VM?.PasteOverlayCommand.Execute(null);
    }

    /// <summary>
    /// Add overlay-Flow: Schritt 1 (Browser/Window-Auswahl) → Schritt 2
    /// (jeweils passender Eingabe-Dialog) → neue Overlay an SelectedLayout
    /// anhängen (in der aktuell aktiven Session).
    /// </summary>
    /// <summary>Session-Pill geklickt → Spotter-Editiermodus verlassen.</summary>
    private void SessionPill_Click(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.EditingSpotter = false;
    }

    private void AddOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (VM?.EditSources == null) return;

        // Schritt 1: Browser oder Window?
        var typeDlg = new AddOverlayTypeDialog
        {
            Owner = Window.GetWindow(this)
        };
        if (typeDlg.ShowDialog() != true) return;

        // Schritt 2: passender Eingabe-Dialog
        SourceViewModel? newSrc = typeDlg.SelectedType switch
        {
            SourceType.Browser => CreateBrowserOverlay(),
            SourceType.Window => CreateWindowOverlay(),
            _ => null
        };

        if (newSrc == null) return;

        // Anhängen an das gerade bearbeitete Set (Auto-Session oder Spotter)
        VM.EditSources?.Add(newSrc);
    }

    /// <summary>Schritt 2a — Browser-Eingabe-Dialog.</summary>
    private SourceViewModel? CreateBrowserOverlay()
    {
        var dlg = new AddOverlayBrowserDialog
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true) return null;

        return new SourceViewModel
        {
            Id = NewId(),
            Name = dlg.OverlayName,
            Type = SourceType.Browser,
            Target = dlg.OverlayUrl,
            // Position/Opacity wie Reset; Scale-Basiswert für Browser bewusst
            // höher (roh 0.25 = Anzeige 25), da Browser-Overlays i.d.R. größer
            // platziert werden als Window-Captures. (ResetPosition setzt
            // generisch 0.10 zurück — bewusst nicht typabhängig.)
            Visible = true,
            X = 0.0f,
            Y = 0.0f,
            Z = -0.8f,
            Yaw = 0.0f,
            Pitch = 0.0f,
            Scale = 0.25f,
            Opacity = 1.0f
        };
    }

    /// <summary>Schritt 2b — Window-Eingabe-Dialog.</summary>
    private SourceViewModel? CreateWindowOverlay()
    {
        var dlg = new AddOverlayWindowDialog
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true) return null;

        // Initiale Scale = echte Pixel-Breite des Fensters × 1 mm/Pixel (gekappt 5–200 cm).
        // 1920px-Fenster → 1.92 m, 400px-Werkzeug → 40 cm. Tatsächliche Größe als Standard.
        float scale = 0.30f;
        if (dlg.SelectedWindowPixelWidth > 0)
        {
            scale = System.Math.Clamp(dlg.SelectedWindowPixelWidth * 0.001f, 0.05f, 2.0f);
        }

        return new SourceViewModel
        {
            Id = NewId(),
            Name = dlg.OverlayName,
            Type = SourceType.Window,
            Target = dlg.OverlayTarget,
            Visible = true,
            X = 0.0f,
            Y = 0.0f,
            Z = -0.8f,
            Yaw = 0.0f,
            Pitch = 0.0f,
            Scale = scale,
            Opacity = 1.0f,
            // C6: Atlas-Region in echten Fenster-Pixeln packen. C3b-Shelf-Packer
            // nutzt PixelWidth/Height, sonst fällt MainViewModel auf 512×384
            // Default zurück und das Quad-Aspect stimmt nicht. Klamp gegen
            // Riesen-Fenster (>2048 sprengt den Atlas-Wrap).
            PixelWidth  = dlg.SelectedWindowPixelWidth  > 0
                ? System.Math.Min(dlg.SelectedWindowPixelWidth,  2048) : 0,
            PixelHeight = dlg.SelectedWindowPixelHeight > 0
                ? System.Math.Min(dlg.SelectedWindowPixelHeight, 2048) : 0,
        };
    }

    /// <summary>Erzeugt eine kurze, eindeutige ID für eine neue Overlay.</summary>
    private static string NewId() => $"src_{System.Guid.NewGuid():N}".Substring(0, 12);

    // -------- Session-Menü Click-Handler ----------------------------------

    /// <summary>
    /// Liefert die zwei "anderen" Sessions (alle außer der aktuell aktiven).
    /// Reihenfolge: Practice, Qualify, Race — minus die aktive.
    /// </summary>
    private (SessionType A, SessionType B) GetOtherSessions()
    {
        var current = VM?.SelectedLayout?.SelectedSession ?? SessionType.Practice;
        var others = System.Enum.GetValues<SessionType>().Where(s => s != current).ToArray();
        // „Copy from"-Menü hat 2 Slots; bei 4 Sessions fällt also die letzte raus.
        return (others[0], others[1]);
    }

    private void SessionMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.ContextMenu != null)
        {
            // Beschriftung der zwei "Copy from X" Items dynamisch setzen
            var (a, b) = GetOtherSessions();

            if (btn.ContextMenu.Items.Count >= 2)
            {
                if (btn.ContextMenu.Items[0] is MenuItem itemA)
                    itemA.Header = $"Copy from {a}";
                if (btn.ContextMenu.Items[1] is MenuItem itemB)
                    itemB.Header = $"Copy from {b}";
            }

            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void SessionMenu_FromA_Click(object sender, RoutedEventArgs e)
    {
        var (a, _) = GetOtherSessions();
        VM?.CopyFromSessionCommand.Execute(a);
    }

    private void SessionMenu_FromB_Click(object sender, RoutedEventArgs e)
    {
        var (_, b) = GetOtherSessions();
        VM?.CopyFromSessionCommand.Execute(b);
    }

    private void SessionMenu_ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        VM?.ApplyToAllSessionsCommand.Execute(null);
    }

    private void SessionMenu_Clear_Click(object sender, RoutedEventArgs e)
    {
        VM?.ClearSessionCommand.Execute(null);
    }

    // -------- Overlay-Name editieren (Doppelklick → Inline-TextBox) -------

    private void OverlayName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 &&
            sender is TextBlock tb &&
            tb.DataContext is SourceViewModel src)
        {
            src.BeginRename();
            e.Handled = true;
        }
    }

    /// <summary>Wird aufgerufen wenn die TextBox sichtbar wird → fokussieren + Text markieren.</summary>
    private void OverlayName_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tbx)
        {
            tbx.Focus();
            tbx.SelectAll();
        }
    }

    private void OverlayName_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tbx &&
            tbx.DataContext is SourceViewModel src)
        {
            if (e.Key == Key.Enter)
            {
                src.CommitRename();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                src.CancelRename();
                e.Handled = true;
            }
        }
    }

    private void OverlayName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tbx &&
            tbx.DataContext is SourceViewModel src &&
            src.IsRenaming)  // nur wenn noch im Edit-Modus (Enter/Esc setzen das schon zurück)
        {
            src.CommitRename();
        }
    }
}