using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR.Views;

/// <summary>
/// Layout-Sidebar (linke Spalte): Liste aller Layouts mit Default-Trennlinie,
/// Favoriten-Stern und ... -Kontextmenü pro Layout.
/// DataContext = MainViewModel (geerbt vom Window).
/// </summary>
public partial class LayoutSidebar : UserControl
{
    public LayoutSidebar()
    {
        InitializeComponent();
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    // -------- Favoriten-Toggle in der Layout-Liste ------------------------
    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // verhindert dass auch das umschließende Layout-Item-Click feuert
        if (sender is Button btn && btn.DataContext is CarLayoutViewModel layout)
        {
            layout.IsFavorite = !layout.IsFavorite;
            VM?.RefreshLayoutSort();
        }
    }

    // -------- Layout ... Menü beim Linksklick öffnen ----------------------
    private void LayoutMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // nicht das umschließende Layout-Item-Click feuern lassen
        if (sender is Button btn && btn.ContextMenu != null && btn.Tag is CarLayoutViewModel layout)
        {
            btn.ContextMenu.PlacementTarget = btn;

            // Default-Layout: einige Aktionen ausblenden/disablen
            bool isDefault = layout.IsDefault;

            foreach (var item in btn.ContextMenu.Items.OfType<MenuItem>())
            {
                switch (item.Header as string)
                {
                    case "Paste config":
                        item.IsEnabled = VM?.HasLayoutClipboard ?? false;
                        break;
                    case "Set as active":
                        // Default als Override anzupinnen macht keinen Sinn
                        item.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
                        break;
                    case "Delete layout":
                        // Default kann nicht gelöscht werden
                        item.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
                        break;
                }
            }

            btn.ContextMenu.IsOpen = true;
        }
    }

    // -------- Layout-Menü Click-Handler -----------------------------------
    // Liest den Layout-DataContext aus dem PlacementTarget des MenuItems
    // und ruft das passende Command am MainViewModel auf.

    private CarLayoutViewModel? GetLayoutFromMenu(object sender)
    {
        if (sender is MenuItem mi &&
            mi.Parent is ContextMenu cm &&
            cm.PlacementTarget is Button btn &&
            btn.Tag is CarLayoutViewModel layout)
        {
            return layout;
        }
        return null;
    }

    private void LayoutMenu_SetActive_Click(object sender, RoutedEventArgs e)
    {
        var layout = GetLayoutFromMenu(sender);
        if (layout != null) VM?.SetAsActiveCommand.Execute(layout);
    }

    /// <summary>
    /// Pin-Button neben dem Layout-Namen: macht das Layout zum aktiven.
    /// Wenn schon aktiv, klick deaktiviert.
    /// </summary>
    private void LayoutPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CarLayoutViewModel layout) return;
        if (ReferenceEquals(VM?.ActiveLayout, layout))
        {
            VM?.ClearActiveCommand.Execute(null);
        }
        else
        {
            VM?.SetAsActiveCommand.Execute(layout);
        }
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
