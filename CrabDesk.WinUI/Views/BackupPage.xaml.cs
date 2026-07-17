using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class BackupPage : Page
{
    public BackupPage()
    {
        InitializeComponent();
        DataContext = App.GetService<BackupViewModel>();
    }
}
