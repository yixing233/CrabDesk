using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<DiagnosticsViewModel>();
    }
}
