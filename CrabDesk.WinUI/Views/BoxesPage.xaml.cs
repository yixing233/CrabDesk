using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class BoxesPage : Page
{
    public BoxesPage()
    {
        InitializeComponent();
        DataContext = App.GetService<BoxesViewModel>();
    }
}
