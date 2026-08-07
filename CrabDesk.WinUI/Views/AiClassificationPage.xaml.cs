using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class AiClassificationPage : Page
{
    private AiClassificationViewModel ViewModel => (AiClassificationViewModel)DataContext;

    public AiClassificationPage()
    {
        InitializeComponent();
        DataContext = App.GetService<AiClassificationViewModel>();
        ApiKeyBox.Password = ViewModel.ApiKey;
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is AiClassificationViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.ApiKey = passwordBox.Password;
        }
    }
}
