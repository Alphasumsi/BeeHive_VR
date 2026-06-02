using System.Windows;
using System.Windows.Input;

namespace BeeHiveVR.Views;

/// <summary>
/// Schritt 1 des Add-overlay-Flows: User wählt Browser oder Window.
/// Modal, blockiert das Hauptfenster.
/// </summary>
public partial class AddOverlayTypeDialog : Window
{
    /// <summary>Welchen Typ der User gewählt hat (nur gültig wenn DialogResult=true).</summary>
    public Models.SourceType SelectedType { get; private set; }

    public AddOverlayTypeDialog()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        SelectedType = Models.SourceType.Browser;
        DialogResult = true;
        Close();
    }

    private void Window_Click(object sender, RoutedEventArgs e)
    {
        SelectedType = Models.SourceType.Window;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}