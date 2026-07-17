using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CrabDesk.Core;

namespace CrabDesk.WinUI.Services;

public interface IDialogService
{
    void RegisterXamlRoot(XamlRoot root);
    Task<bool> ConfirmAsync(string title, string message, string primaryText);
    Task ShowMessageAsync(string title, string message);
    Task<string?> PromptAsync(string title, string label, string initialValue = "");
    Task<OrganizationRule?> EditOrganizationRuleAsync(
        OrganizationRule? rule,
        IReadOnlyList<DesktopBox> boxes);
}

public sealed class DialogService : IDialogService
{
    private XamlRoot? _xamlRoot;

    public void RegisterXamlRoot(XamlRoot root) => _xamlRoot = root;

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = CreateDialog(title, message);
        dialog.PrimaryButtonText = primaryText;
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message);
        dialog.CloseButtonText = "关闭";
        await dialog.ShowAsync();
    }

    public async Task<string?> PromptAsync(string title, string label, string initialValue = "")
    {
        var input = new TextBox
        {
            Header = label,
            Text = initialValue,
            MinWidth = 360,
            SelectionStart = 0,
            SelectionLength = initialValue.Length
        };
        var dialog = CreateDialog(title, input);
        dialog.PrimaryButtonText = "保存";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text)
            ? input.Text.Trim()
            : null;
    }

    public async Task<OrganizationRule?> EditOrganizationRuleAsync(
        OrganizationRule? rule,
        IReadOnlyList<DesktopBox> boxes)
    {
        var source = rule is null ? new OrganizationRule() : CloneRule(rule);
        var title = new TextBox { Header = "规则名称", Text = source.Title };
        var pattern = new TextBox { Header = "名称模式", Text = source.NamePattern, PlaceholderText = "例如 *.pdf" };
        var extensions = new TextBox
        {
            Header = "扩展名",
            Text = string.Join(", ", source.Extensions.Select(value => value.TrimStart('.'))),
            PlaceholderText = "pdf, docx, xlsx"
        };
        var enabled = new ToggleSwitch { Header = "启用规则", IsOn = source.Enabled };
        var file = new CheckBox { Content = "文件", IsChecked = source.ItemKinds.Contains(DesktopItemKind.File) };
        var folder = new CheckBox { Content = "文件夹", IsChecked = source.ItemKinds.Contains(DesktopItemKind.Folder) };
        var shortcut = new CheckBox { Content = "快捷方式", IsChecked = source.ItemKinds.Contains(DesktopItemKind.Shortcut) };
        var shell = new CheckBox { Content = "系统项目", IsChecked = source.ItemKinds.Contains(DesktopItemKind.Shell) };
        var action = new ComboBox
        {
            Header = "操作",
            ItemsSource = Enum.GetValues<OrganizationRuleAction>(),
            SelectedItem = source.Action,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var target = new ComboBox
        {
            Header = "目标盒子",
            ItemsSource = boxes.Where(box => !box.IsMappedFolder).ToArray(),
            DisplayMemberPath = nameof(DesktopBox.Title),
            SelectedItem = boxes.FirstOrDefault(box => box.Id == source.TargetBoxId) ?? boxes.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 400 };
        panel.Children.Add(title);
        panel.Children.Add(enabled);
        panel.Children.Add(pattern);
        panel.Children.Add(extensions);
        panel.Children.Add(new TextBlock { Text = "项目类型" });
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        kinds.Children.Add(file);
        kinds.Children.Add(folder);
        kinds.Children.Add(shortcut);
        kinds.Children.Add(shell);
        panel.Children.Add(kinds);
        panel.Children.Add(action);
        panel.Children.Add(target);

        var dialog = CreateDialog(rule is null ? "新建整理规则" : "编辑整理规则", panel);
        dialog.PrimaryButtonText = "保存";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title.Text))
        {
            return null;
        }

        var selectedAction = action.SelectedItem is OrganizationRuleAction value
            ? value
            : OrganizationRuleAction.AssignToBox;
        return new OrganizationRule
        {
            Id = source.Id,
            Title = title.Text.Trim(),
            Enabled = enabled.IsOn,
            Priority = source.Priority,
            NamePattern = string.IsNullOrWhiteSpace(pattern.Text) ? "*" : pattern.Text.Trim(),
            Extensions = extensions.Text.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            ItemKinds = new[]
            {
                (file.IsChecked == true, DesktopItemKind.File),
                (folder.IsChecked == true, DesktopItemKind.Folder),
                (shortcut.IsChecked == true, DesktopItemKind.Shortcut),
                (shell.IsChecked == true, DesktopItemKind.Shell)
            }.Where(pair => pair.Item1).Select(pair => pair.Item2).ToList(),
            Action = selectedAction,
            TargetBoxId = selectedAction == OrganizationRuleAction.AssignToBox
                ? (target.SelectedItem as DesktopBox)?.Id
                : null
        };
    }

    private ContentDialog CreateDialog(string title, string message) =>
        CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

    private ContentDialog CreateDialog(string title, object content)
    {
        if (_xamlRoot is null)
        {
            throw new InvalidOperationException("The dialog XamlRoot has not been registered.");
        }
        return new ContentDialog
        {
            Title = title,
            Content = content,
            XamlRoot = _xamlRoot
        };
    }

    private static OrganizationRule CloneRule(OrganizationRule source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Enabled = source.Enabled,
        Priority = source.Priority,
        ItemKinds = source.ItemKinds.ToList(),
        NamePattern = source.NamePattern,
        Extensions = source.Extensions.ToList(),
        Action = source.Action,
        TargetBoxId = source.TargetBoxId
    };
}
