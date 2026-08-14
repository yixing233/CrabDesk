using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CrabDesk.WinUI.Views;

public sealed partial class AppearancePage : Page
{
    public AppearancePage()
    {
        InitializeComponent();
        DataContext = App.GetService<AppearanceViewModel>();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => PageScrollViewer.Focus(FocusState.Programmatic));
    }

    private void OnColorFlyoutOpening(object? sender, object e)
    {
        if (sender is Flyout flyout)
        {
            flyout.XamlRoot = XamlRoot;
        }
    }
}
