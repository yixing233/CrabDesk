using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public sealed class OrganizationRuleListItem
{
    public OrganizationRuleListItem(OrganizationRule rule, IReadOnlyList<DesktopBox> boxes)
    {
        Rule = rule;
        CriteriaText = BuildCriteria(rule);
        DestinationText = BuildDestination(rule, boxes);
    }

    public OrganizationRule Rule { get; }
    public Guid Id => Rule.Id;
    public string Title => Rule.Title;
    public bool Enabled => Rule.Enabled;
    public string CriteriaText { get; }
    public string DestinationText { get; }

    private static string BuildCriteria(OrganizationRule rule)
    {
        if (BuiltInOrganizationRules.IsFallback(rule))
        {
            return "未匹配到前面规则的项目";
        }

        var parts = new List<string>();
        var pattern = string.IsNullOrWhiteSpace(rule.NamePattern) ? "*" : rule.NamePattern.Trim();
        if (pattern != "*")
        {
            parts.Add($"名称 {pattern}");
        }

        if (rule.Extensions.Count > 0)
        {
            parts.Add("扩展名 " + string.Join(" · ", rule.Extensions));
        }
        else if (rule.ItemKinds.Count == 1)
        {
            parts.Add(rule.ItemKinds[0] switch
            {
                DesktopItemKind.File => "所有文件",
                DesktopItemKind.Folder => "所有文件夹",
                DesktopItemKind.Shortcut => "所有快捷方式",
                DesktopItemKind.Shell => "所有系统项目",
                _ => "所有项目"
            });
        }
        else if (rule.ItemKinds.Count > 1 && rule.ItemKinds.Count < Enum.GetValues<DesktopItemKind>().Length)
        {
            parts.Add(string.Join("、", rule.ItemKinds.Select(KindName)));
        }

        return parts.Count == 0 ? "所有项目" : string.Join("；", parts);
    }

    private static string BuildDestination(OrganizationRule rule, IReadOnlyList<DesktopBox> boxes)
    {
        if (rule.Action == OrganizationRuleAction.KeepUnassigned)
        {
            return "保留在桌面";
        }
        if (rule.Action == OrganizationRuleAction.Ignore)
        {
            return "忽略，不整理";
        }
        if (rule.TargetBoxId is { } target)
        {
            var box = boxes.FirstOrDefault(candidate => candidate.Id == target);
            return box is null ? "目标盒子已不存在" : $"放入「{box.Title}」";
        }
        return $"整理时创建「{rule.Title}」";
    }

    private static string KindName(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.File => "文件",
        DesktopItemKind.Folder => "文件夹",
        DesktopItemKind.Shortcut => "快捷方式",
        DesktopItemKind.Shell => "系统项目",
        _ => "项目"
    };
}

public partial class OrganizationViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private OrganizationRuleListItem? _selectedRule;
    [ObservableProperty] private string _resultText = string.Empty;

    public OrganizationViewModel(ICrabDeskService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        _service.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<OrganizationRuleListItem> Rules { get; } = [];
    public bool OrganizationEnabled
    {
        get => _service.State.Organization.Enabled;
        set { if (value != OrganizationEnabled) { _service.SetOrganizationEnabled(value); OnPropertyChanged(); } }
    }
    public bool RunOnStartup
    {
        get => _service.State.Organization.RunOnStartup;
        set { if (value != RunOnStartup) { _service.SetRunRulesOnStartup(value); OnPropertyChanged(); } }
    }
    public bool RunRealtime
    {
        get => _service.State.Organization.RunOnDesktopChanges;
        set { if (value != RunRealtime) { _service.SetRunRulesOnDesktopChanges(value); OnPropertyChanged(); } }
    }
    public bool ReassignExisting
    {
        get => _service.State.Organization.ReassignExistingItems;
        set { if (value != ReassignExisting) { _service.SetReassignExistingItems(value); OnPropertyChanged(); } }
    }
    public bool CanUndo => _service.CanUndoOrganization;
    public string ConflictText
    {
        get
        {
            var count = _service.GetOrganizationRuleConflicts().Count;
            return count == 0 ? "未发现规则冲突" : $"发现 {count} 组规则冲突";
        }
    }

    [RelayCommand]
    private void Apply()
    {
        var result = _service.ApplyOrganizationRules();
        ResultText = $"已分配 {result.Assigned} 项，保留 {result.Unassigned} 项，忽略 {result.Ignored} 项" +
            (result.InvalidTargets > 0 ? $"，{result.InvalidTargets} 项缺少目标盒子" : string.Empty);
    }

    [RelayCommand] private void Undo() => _service.UndoLastOrganization();
    [RelayCommand] private void InstallDefaults() => _service.InstallDefaultOrganizationRules();

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var rule = await _dialogs.EditOrganizationRuleAsync(null, _service.Boxes);
        if (rule is not null) _service.SaveOrganizationRule(rule);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditRuleAsync()
    {
        if (SelectedRule is null) return;
        var rule = await _dialogs.EditOrganizationRuleAsync(SelectedRule.Rule, _service.Boxes);
        if (rule is not null) _service.SaveOrganizationRule(rule);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateRule()
    {
        if (SelectedRule is null) return;
        var copy = _service.DuplicateOrganizationRule(SelectedRule.Id);
        if (copy is not null)
        {
            SelectedRule = Rules.FirstOrDefault(item => item.Id == copy.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null || !await _dialogs.ConfirmAsync("删除规则", $"删除“{SelectedRule.Title}”？", "删除")) return;
        _service.DeleteOrganizationRule(SelectedRule.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))] private void MoveUp() { if (SelectedRule is not null) _service.MoveOrganizationRule(SelectedRule.Id, -1); }
    [RelayCommand(CanExecute = nameof(HasSelection))] private void MoveDown() { if (SelectedRule is not null) _service.MoveOrganizationRule(SelectedRule.Id, 1); }

    public void SetRuleEnabled(OrganizationRuleListItem item, bool enabled) =>
        _service.SetOrganizationRuleEnabled(item.Id, enabled);

    partial void OnSelectedRuleChanged(OrganizationRuleListItem? value)
    {
        EditRuleCommand.NotifyCanExecuteChanged();
        DuplicateRuleCommand.NotifyCanExecuteChanged();
        DeleteRuleCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => SelectedRule is not null;

    private void Refresh()
    {
        var selectedId = SelectedRule?.Id;
        Rules.Clear();
        foreach (var rule in _service.State.OrganizationRules.OrderBy(rule => rule.Priority))
        {
            Rules.Add(new OrganizationRuleListItem(rule, _service.Boxes));
        }
        SelectedRule = Rules.FirstOrDefault(item => item.Id == selectedId) ?? Rules.FirstOrDefault();
        OnPropertyChanged(string.Empty);
    }
}
