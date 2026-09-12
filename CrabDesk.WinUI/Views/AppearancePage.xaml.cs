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

    private void AppearanceSubSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selectedItem = sender.SelectedItem;
        if (selectedItem == null) return;

        var selectedIndex = sender.Items.IndexOf(selectedItem);

        PanelBoxSurface.Visibility = selectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelTitleBar.Visibility = selectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelIconsAndLabels.Visibility = selectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelDesktopIconNames.Visibility = selectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnColorFlyoutOpening(object? sender, object e)
    {
        if (sender is Flyout flyout)
        {
            flyout.XamlRoot = XamlRoot;
        }
    }
}
