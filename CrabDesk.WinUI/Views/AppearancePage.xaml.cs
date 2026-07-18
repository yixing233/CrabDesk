using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class AppearancePage : Page
{
    public AppearancePage()
    {
        InitializeComponent();
        DataContext = App.GetService<AppearanceViewModel>();
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => PageScrollViewer.Focus(Microsoft.UI.Xaml.FocusState.Programmatic));
    }
}
