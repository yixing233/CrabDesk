using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class AiClassificationPage : Page
{
    private AiClassificationViewModel ViewModel => (AiClassificationViewModel)DataContext;

    public AiClassificationPage() : this(App.GetService<AiClassificationViewModel>())
    {
    }

    internal AiClassificationPage(AiClassificationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ApiKeyBox.Password = viewModel.ApiKey;
        WebSearchApiKeyBox.Password = viewModel.WebSearchApiKey;
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is AiClassificationViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.ApiKey = passwordBox.Password;
        }
    }

    private void WebSearchApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is AiClassificationViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.WebSearchApiKey = passwordBox.Password;
        }
    }
}
