using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.Views;

public sealed partial class OrganizationPage : Page
{
    private OrganizationViewModel ViewModel => (OrganizationViewModel)DataContext;

    public OrganizationPage()
    {
        InitializeComponent();
        DataContext = App.GetService<OrganizationViewModel>();
    }

    private void RuleEnabled_OnToggled(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is ToggleSwitch { DataContext: OrganizationRuleListItem item } toggle)
        {
            ViewModel.SetRuleEnabled(item, toggle.IsOn);
        }
    }

    private void RuleSelected_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is CheckBox { DataContext: OrganizationRuleListItem item } box)
        {
            ViewModel.SetRuleChecked(item, box.IsChecked == true);
        }
    }

    private void RuleRow_OnTapped(object sender, TappedRoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: OrganizationRuleListItem item })
        {
            return;
        }

        if (IsInsideInteractiveControl(eventArgs.OriginalSource))
        {
            return;
        }

        ViewModel.ToggleRuleChecked(item);
    }

    private static bool IsInsideInteractiveControl(object originalSource)
    {
        for (var current = originalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is CheckBox || current is ToggleSwitch || current is Button)
            {
                return true;
            }
        }
        return false;
    }

}

