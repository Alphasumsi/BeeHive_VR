using System.Windows.Controls;
using HoneyOverlays.Services;

namespace HoneyOverlays.Views;

/// <summary>
/// Platzhalter-Page für später. Aktuell nur Bild + Version.
/// </summary>
public partial class MenuPage : UserControl
{
    /// <summary>App-Version für die Anzeige oben rechts.</summary>
    public string AppVersion => AppInfo.VersionDisplay;

    public MenuPage()
    {
        InitializeComponent();
    }
}