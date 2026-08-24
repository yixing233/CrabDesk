using CrabDesk.Core;
using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CrabDesk.WinUI.Views;

public sealed partial class BackupPage : Page
{
    private BackupViewModel ViewModel => (BackupViewModel)DataContext;

    public BackupPage()
    {
        InitializeComponent();
        DataContext = App.GetService<BackupViewModel>();
    }

    private async void BackupItem_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: LayoutBackupInfo backup })
        {
            return;
        }

        ViewModel.SelectedBackup = backup;
        await ViewModel.PreviewCommand.ExecuteAsync(null);
        eventArgs.Handled = true;
    }
}
