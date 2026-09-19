using System.Collections.Specialized;
using CrabDesk.WinUI.Controls;
using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

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

    /// <summary>
    /// Pushes every keystroke into the filter. The Text binding alone only syncs on
    /// focus loss for AutoSuggestBox, so the grid would otherwise lag behind typing.
    /// </summary>
    private void WorkspaceSearchBox_OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.WorkspaceFilter = sender.Text;
    }

    /// <summary>
    /// Opens the card's classification menu. MenuFlyout has no ItemsSource, so the
    /// entries are built here from the view model's choices each time it opens; that
    /// also keeps the checked entry in step with the item's current decision.
    /// </summary>
    private void ClassificationChip_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement chip ||
            chip.DataContext is not AiWorkbenchItemViewModel item ||
            !ViewModel.ChooseWorkspaceItemLabelCommand.CanExecute(null))
        {
            return;
        }

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        AiWorkbenchLabelChoiceKind? previousKind = null;
        foreach (var choice in ViewModel.GetLabelChoices(item))
        {
            if (previousKind is not null && previousKind != choice.Kind)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            flyout.Items.Add(CreateLabelChoiceItem(choice));
            previousKind = choice.Kind;
        }

        flyout.ShowAt(chip);
    }

    private MenuFlyoutItem CreateLabelChoiceItem(AiWorkbenchLabelChoice choice)
    {
        // Categories and "不归类" are one exclusive choice; restoring the suggestion is an action.
        MenuFlyoutItem entry = choice.Kind == AiWorkbenchLabelChoiceKind.RestoreAiSuggestion
            ? new MenuFlyoutItem { Icon = new LucideIcon { Icon = LucideIconName.Sparkles } }
            : new RadioMenuFlyoutItem { GroupName = "classification", IsChecked = choice.IsCurrent };
        entry.Text = choice.Text;
        entry.Command = ViewModel.ChooseWorkspaceItemLabelCommand;
        entry.CommandParameter = choice;
        return entry;
    }
}
