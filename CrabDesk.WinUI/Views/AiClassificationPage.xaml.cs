using System.Collections.Specialized;
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
        viewModel.Conversation.CollectionChanged += OnConversationChanged;
    }

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        var items = ViewModel.Conversation;
        if (eventArgs.Action == NotifyCollectionChangedAction.Add && items.Count > 0)
        {
            ConversationList.ScrollIntoView(items[^1]);
        }
    }

    private async void SettingsButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new ContentDialog
        {
            Title = "AI 设置",
            Content = new AiSettingsPanel(ViewModel),
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        await dialog.ShowAsync();
    }

    private void MessageTextBox_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        // Keep the newest streamed tokens in view for the message that is still
        // streaming; finished turns stay put so the user can read or copy them.
        if (sender is not TextBox textBox || textBox.DataContext is not AiConversationMessageViewModel message || !message.IsRunning)
        {
            return;
        }

        textBox.Select(textBox.Text.Length, 0);
    }
}
