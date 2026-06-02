using System.Windows;
using System.Windows.Input;

namespace BeeHiveVR.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Zeigt einen Confirm-Dialog im Dark-Theme.
    /// Returns true wenn der User OK gedrückt hat, false bei Cancel/Schließen.
    /// </summary>
    public static bool Show(Window owner, string title, string message,
                            string okText = "OK", string cancelText = "Cancel")
    {
        var dlg = new ConfirmDialog
        {
            Owner = owner
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.OkButton.Content = okText;
        dlg.CancelButton.Content = cancelText;

        return dlg.ShowDialog() == true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}