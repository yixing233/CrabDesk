using System.Windows;
using CrabDesk.Core;

namespace CrabDesk.App;

public partial class OrganizationRuleEditorDialog : Window
{
    private readonly OrganizationRule _source;

    public OrganizationRuleEditorDialog(
        OrganizationRule? rule,
        IReadOnlyList<DesktopBox> boxes,
        bool isDarkTheme)
    {
        _source = rule is null ? new OrganizationRule() : Clone(rule);
        InitializeComponent();
        TitleTextBox.Text = _source.Title;
        NamePatternTextBox.Text = _source.NamePattern;
        ExtensionsTextBox.Text = string.Join(", ", _source.Extensions.Select(extension => extension.TrimStart('.')));
        FileKindCheckBox.IsChecked = _source.ItemKinds.Contains(DesktopItemKind.File);
        FolderKindCheckBox.IsChecked = _source.ItemKinds.Contains(DesktopItemKind.Folder);
        ShortcutKindCheckBox.IsChecked = _source.ItemKinds.Contains(DesktopItemKind.Shortcut);
        ShellKindCheckBox.IsChecked = _source.ItemKinds.Contains(DesktopItemKind.Shell);
        AssignActionRadioButton.IsChecked = _source.Action == OrganizationRuleAction.AssignToBox;
        KeepActionRadioButton.IsChecked = _source.Action == OrganizationRuleAction.KeepUnassigned;
        IgnoreActionRadioButton.IsChecked = _source.Action == OrganizationRuleAction.Ignore;
        EnabledCheckBox.IsChecked = _source.Enabled;
        TargetBoxesList.ItemsSource = boxes;
        TargetBoxesList.SelectedItem = boxes.FirstOrDefault(box => box.Id == _source.TargetBoxId)
            ?? boxes.FirstOrDefault();
        UpdateTargetState();
        SourceInitialized += (_, _) => ApplicationTheme.ApplyWindowChrome(this, isDarkTheme);
    }

    public OrganizationRule? EditedRule { get; private set; }

    private void ActionRadioButton_OnChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (IsInitialized)
        {
            UpdateTargetState();
        }
    }

    private void UpdateTargetState()
    {
        TargetBoxesList.IsEnabled = AssignActionRadioButton.IsChecked == true;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(TitleTextBox.Text))
        {
            MessageBox.Show("请输入规则名称。", "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            TitleTextBox.Focus();
            return;
        }

        var action = KeepActionRadioButton.IsChecked == true
            ? OrganizationRuleAction.KeepUnassigned
            : IgnoreActionRadioButton.IsChecked == true
                ? OrganizationRuleAction.Ignore
                : OrganizationRuleAction.AssignToBox;
        if (action == OrganizationRuleAction.AssignToBox && TargetBoxesList.SelectedItem is not DesktopBox)
        {
            MessageBox.Show("请选择目标盒子。", "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kinds = new List<DesktopItemKind>();
        if (FileKindCheckBox.IsChecked == true) kinds.Add(DesktopItemKind.File);
        if (FolderKindCheckBox.IsChecked == true) kinds.Add(DesktopItemKind.Folder);
        if (ShortcutKindCheckBox.IsChecked == true) kinds.Add(DesktopItemKind.Shortcut);
        if (ShellKindCheckBox.IsChecked == true) kinds.Add(DesktopItemKind.Shell);
        var extensions = ExtensionsTextBox.Text
            .Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        EditedRule = new OrganizationRule
        {
            Id = _source.Id,
            Title = TitleTextBox.Text.Trim(),
            Enabled = EnabledCheckBox.IsChecked == true,
            Priority = _source.Priority,
            ItemKinds = kinds,
            NamePattern = string.IsNullOrWhiteSpace(NamePatternTextBox.Text) ? "*" : NamePatternTextBox.Text.Trim(),
            Extensions = extensions,
            Action = action,
            TargetBoxId = action == OrganizationRuleAction.AssignToBox
                ? ((DesktopBox)TargetBoxesList.SelectedItem).Id
                : null
        };
        DialogResult = true;
    }

    private static OrganizationRule Clone(OrganizationRule source) => new()
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
