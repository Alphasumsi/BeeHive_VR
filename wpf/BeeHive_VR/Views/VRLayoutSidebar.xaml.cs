using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR.Views
{
    /// <summary>
    /// VR-Layouts-Sidebar (Card-Style, Hover-Reveal, Sub-Session-Tag).
    /// DataContext = MainViewModel (geerbt).
    /// Interaktion:
    ///   - Single-Click   → Select (Edit-Cursor)
    ///   - Doppel-Click   → Set as active
    ///   - Rechts-Click   → Context-Menü (Border.ContextMenu öffnet automatisch)
    /// </summary>
    public partial class VRLayoutSidebar : UserControl
    {
        public VRLayoutSidebar()
        {
            InitializeComponent();
        }

        private MainViewModel? VM => DataContext as MainViewModel;

        // ---- Row-Maus: Single = Select, Double = Activate ------------------
        private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CarLayoutViewModel layout)
                return;

            if (e.ClickCount >= 2)
            {
                VM?.SetAsActiveCommand.Execute(layout);
                e.Handled = true;
                return;
            }
            VM?.SelectLayoutCommand.Execute(layout);
        }

        // ---- Rechtsklick: ContextMenu vom Border öffnet automatisch.
        // Wir selektieren zusätzlich, damit der Edit-Cursor auf das Item zeigt
        // bevor das Menü aufgeht — verhindert Verwechslung.
        private void Row_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CarLayoutViewModel layout)
            {
                VM?.SelectLayoutCommand.Execute(layout);
            }
        }

        // ---- Pin: Set/Clear as active (V1-Logik 1:1) ------------------------
        private void LayoutPin_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // nicht in Row_MouseLeftButtonDown bubblen
            if (sender is not Button btn || btn.Tag is not CarLayoutViewModel layout) return;
            if (object.ReferenceEquals(VM?.ActiveLayout, layout))
                VM?.ClearActiveCommand.Execute(null);
            else
                VM?.SetAsActiveCommand.Execute(layout);
        }

        // ---- Favoriten-Stern ------------------------------------------------
        private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // nicht in Row_MouseLeftButtonDown bubblen
            if (sender is Button btn && btn.DataContext is CarLayoutViewModel layout)
            {
                layout.IsFavorite = !layout.IsFavorite;
                VM?.RefreshLayoutSort();
            }
        }

        // ---- 3-Punkt-Menü: öffnet ContextMenu des umschließenden RowRoot ---
        private void LayoutMenu_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button btn) return;
            // Finde das nächste Border mit gesetztem ContextMenu (RowRoot).
            DependencyObject? cur = btn;
            while (cur != null)
            {
                if (cur is Border b && b.ContextMenu != null)
                {
                    // Default-Layout: Set as active / Delete ausblenden, Paste nur wenn Clipboard da
                    if (btn.DataContext is CarLayoutViewModel layout)
                    {
                        bool isDefault = layout.IsDefault;
                        foreach (var item in b.ContextMenu.Items.OfType<MenuItem>())
                        {
                            switch (item.Header as string)
                            {
                                case "Delete layout":
                                    item.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
                                    break;
                                case "Paste config":
                                    item.IsEnabled = VM?.HasLayoutClipboard ?? false;
                                    break;
                            }
                        }
                    }
                    b.ContextMenu.PlacementTarget = btn;
                    b.ContextMenu.IsOpen = true;
                    return;
                }
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
        }

        // ---- Context-Menü-Handler ------------------------------------------
        private static CarLayoutViewModel? GetLayoutFromMenu(object sender)
        {
            if (sender is MenuItem mi &&
                mi.Parent is ContextMenu cm &&
                cm.PlacementTarget is FrameworkElement target &&
                target.DataContext is CarLayoutViewModel layout)
            {
                return layout;
            }
            return null;
        }

        private void LayoutMenu_Copy_Click(object sender, RoutedEventArgs e)
        {
            var layout = GetLayoutFromMenu(sender);
            if (layout != null) VM?.CopyLayoutCommand.Execute(layout);
        }

        private void LayoutMenu_Paste_Click(object sender, RoutedEventArgs e)
        {
            var layout = GetLayoutFromMenu(sender);
            if (layout != null) VM?.PasteLayoutCommand.Execute(layout);
        }

        private void LayoutMenu_Delete_Click(object sender, RoutedEventArgs e)
        {
            var layout = GetLayoutFromMenu(sender);
            if (layout != null) VM?.DeleteLayoutCommand.Execute(layout);
        }
    }
}
