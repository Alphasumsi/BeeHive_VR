using System.Windows;
using System.Windows.Controls;
using HoneyOverlays.ViewModels;

namespace HoneyOverlays.Views
{
    /// <summary>
    /// Interaktionslogik für TPPage.xaml. DataContext wird am inneren Root
    /// gesetzt, damit die UserControl ihren geerbten MainViewModel-DataContext
    /// behält (Visibility-Binding gegen ActiveSection).
    /// </summary>
    public partial class TPPage : UserControl
    {
        public TPPage()
        {
            InitializeComponent();

            var vm = new TradingPaintsViewModel();
            if (Content is FrameworkElement root)
                root.DataContext = vm;
        }
    }
}
