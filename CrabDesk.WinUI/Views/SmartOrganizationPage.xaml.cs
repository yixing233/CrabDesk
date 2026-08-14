using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class SmartOrganizationPage : Page
{
    public SmartOrganizationPage()
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
        var pageType = string.Equals(tag, "ai", StringComparison.Ordinal)
            ? typeof(AiClassificationPage)
            : typeof(OrganizationPage);
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
