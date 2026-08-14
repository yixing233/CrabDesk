using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class DesktopBoxesPage : Page
{
    public DesktopBoxesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => NavigateToSelected(PageSelectorBar.SelectedItem as SelectorBarItem);
    }

    private void PageSelectorBar_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        NavigateToSelected(sender.SelectedItem as SelectorBarItem);
    }

    private void NavigateToSelected(SelectorBarItem? item)
    {
        if (item?.Tag is not string tag)
        {
            return;
        }
        var pageType = string.Equals(tag, "appearance", StringComparison.Ordinal)
            ? typeof(AppearancePage)
            : typeof(BoxesPage);
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
