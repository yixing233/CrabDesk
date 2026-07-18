using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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

    private void Rules_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs eventArgs)
    {
        if (ViewModel.EditRuleCommand.CanExecute(null)) ViewModel.EditRuleCommand.Execute(null);
    }
}
