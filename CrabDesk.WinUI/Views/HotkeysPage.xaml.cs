using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class HotkeysPage : Page
{
    public HotkeysPage()
    {
        InitializeComponent();
        DataContext = App.GetService<HotkeysViewModel>();
    }
}
