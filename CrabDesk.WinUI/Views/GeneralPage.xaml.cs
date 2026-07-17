using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class GeneralPage : Page
{
    public GeneralPage()
    {
        InitializeComponent();
        DataContext = App.GetService<GeneralViewModel>();
    }
}
