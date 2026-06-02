using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BeeHiveVR.Views;

/// <summary>
/// Schritt 2a des Add-overlay-Flows (Browser-Variante).
/// User gibt Name und URL ein.
/// </summary>
public partial class AddOverlayBrowserDialog : Window
{
    /// <summary>Vom User eingegebener Name (nur gültig wenn DialogResult=true).</summary>
    public string OverlayName { get; private set; } = "";

    /// <summary>Vom User eingegebene URL — kann leer sein (nur gültig wenn DialogResult=true).</summary>
    public string OverlayUrl { get; private set; } = "";

    public AddOverlayBrowserDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    /// <summary>Add-Button nur freischalten wenn Name UND URL nicht leer sind.</summary>
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();
    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        AddButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text)
                           && !string.IsNullOrWhiteSpace(UrlBox.Text);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        OverlayName = NameBox.Text.Trim();
        OverlayUrl = UrlBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}