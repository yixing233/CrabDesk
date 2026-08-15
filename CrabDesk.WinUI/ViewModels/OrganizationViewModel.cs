using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public sealed class OrganizationRuleListItem : INotifyPropertyChanged
{
    private bool _isSelected;

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

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

public sealed class OrganizationPreviewItem
{
    public OrganizationPreviewItem(string itemName, string ruleTitle)
    {
        ItemName = itemName;
        RuleTitle = ruleTitle;
    }

    public string ItemName { get; }
    public string RuleTitle { get; }
    public string MatchedByText => $"由「{RuleTitle}」匹配";
}

public sealed class OrganizationPreviewSection
{
    public OrganizationPreviewSection(string title, IReadOnlyList<OrganizationPreviewItem> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public int Count => Items.Count;
    public IReadOnlyList<OrganizationPreviewItem> Items { get; }
}

public partial class OrganizationViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private OrganizationRuleListItem? _selectedRule;
    [ObservableProperty] private string _resultText = string.Empty;
    [ObservableProperty] private bool _isPreviewVisible;
    [ObservableProperty] private string _previewSummaryText = string.Empty;

    public IReadOnlyList<OrganizationRuleListItem> SelectedRules { get; private set; } = [];

    public void UpdateSelection(IEnumerable<OrganizationRuleListItem> selected)
    {
        SelectedRules = selected.ToArray();
        var selectedIds = SelectedRules.Select(item => item.Id).ToHashSet();
        foreach (var item in Rules)
        {
            item.IsSelected = selectedIds.Contains(item.Id);
        }
        DeleteRulesCommand.NotifyCanExecuteChanged();
    }

    public void SetRuleChecked(OrganizationRuleListItem item, bool isChecked)
    {
        if (isChecked)
        {
            SelectedRule = item;
        }
        else if (SelectedRule == item)
        {
            SelectedRule = SelectedRules.FirstOrDefault(candidate => candidate.Id != item.Id);
        }

        UpdateSelection(isChecked
            ? SelectedRules.Append(item).Distinct()
            : SelectedRules.Where(candidate => candidate.Id != item.Id));
    }

    public void ToggleRuleChecked(OrganizationRuleListItem item) =>
        SetRuleChecked(item, !SelectedRules.Contains(item));

    public OrganizationViewModel(ICrabDeskService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        _service.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<OrganizationRuleListItem> Rules { get; } = [];
    public ObservableCollection<OrganizationPreviewSection> PreviewSections { get; } = [];
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
    private async Task ApplyAsync()
    {
        var decisions = _service.PreviewOrganizationRules();
        if (decisions.Count == 0)
        {
            ResultText = "没有匹配到需要整理的项目。";
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "整理预览",
                BuildOrganizationPreview(decisions),
                "确认整理"))
        {
            ResultText = "已取消整理。";
            return;
        }

        ApplyDecisions();
    }

    [RelayCommand]
    private void Preview()
    {
        var decisions = _service.PreviewOrganizationRules();
        if (decisions.Count == 0)
        {
            ResultText = "没有匹配到需要整理的项目。";
            IsPreviewVisible = false;
            return;
        }

        BuildPreview(decisions);
        ResultText = string.Empty;
        IsPreviewVisible = true;
    }

    [RelayCommand]
    private void ClosePreview() => IsPreviewVisible = false;

    [RelayCommand]
    private void ApplyPreview()
    {
        ApplyDecisions();
        IsPreviewVisible = false;
    }

    private void ApplyDecisions()
    {
        var result = _service.ApplyOrganizationRules();
        ResultText = $"已分配 {result.Assigned} 项，保留 {result.Unassigned} 项，忽略 {result.Ignored} 项" +
            (result.InvalidTargets > 0 ? $"，{result.InvalidTargets} 项缺少目标盒子" : string.Empty);
    }

    private void BuildPreview(IReadOnlyList<OrganizationDecision> decisions)
    {
        PreviewSections.Clear();
        var boxes = _service.Boxes
            .Where(box => !box.IsMappedFolder)
            .ToDictionary(box => box.Id, box => box.Title);
        var boxOrder = _service.Boxes
            .Where(box => !box.IsMappedFolder)
            .Select((box, index) => (box.Id, Index: index))
            .ToDictionary(entry => entry.Id, entry => entry.Index);

        var assignable = decisions
            .Where(decision =>
                decision.Action == OrganizationRuleAction.AssignToBox &&
                decision.TargetBoxId is { } target &&
                boxes.ContainsKey(target))
            .GroupBy(decision => decision.TargetBoxId!.Value)
            .OrderBy(group => boxOrder.GetValueOrDefault(group.Key, int.MaxValue))
            .ToArray();
        foreach (var group in assignable)
        {
            PreviewSections.Add(new OrganizationPreviewSection(
                $"放入「{boxes[group.Key]}」",
                group.Select(decision => new OrganizationPreviewItem(decision.ItemName, decision.RuleTitle)).ToArray()));
        }

        var keepUnassigned = decisions
            .Where(decision => decision.Action == OrganizationRuleAction.KeepUnassigned)
            .ToArray();
        if (keepUnassigned.Length > 0)
        {
            PreviewSections.Add(new OrganizationPreviewSection(
                "保留在桌面",
                keepUnassigned.Select(decision => new OrganizationPreviewItem(decision.ItemName, decision.RuleTitle)).ToArray()));
        }

        var ignored = decisions
            .Where(decision => decision.Action == OrganizationRuleAction.Ignore)
            .ToArray();
        if (ignored.Length > 0)
        {
            PreviewSections.Add(new OrganizationPreviewSection(
                "忽略，不整理",
                ignored.Select(decision => new OrganizationPreviewItem(decision.ItemName, decision.RuleTitle)).ToArray()));
        }

        var invalid = decisions
            .Where(decision =>
                decision.Action == OrganizationRuleAction.AssignToBox &&
                (decision.TargetBoxId is not { } target || !boxes.ContainsKey(target)))
            .ToArray();
        if (invalid.Length > 0)
        {
            PreviewSections.Add(new OrganizationPreviewSection(
                "目标盒子不可用",
                invalid.Select(decision => new OrganizationPreviewItem(decision.ItemName, decision.RuleTitle)).ToArray()));
        }

        var parts = new List<string> { $"共 {decisions.Count} 项" };
        if (assignable.Sum(group => group.Count()) > 0)
        {
            parts.Add($"分配 {assignable.Sum(group => group.Count())} 项");
        }
        if (keepUnassigned.Length > 0)
        {
            parts.Add($"保留 {keepUnassigned.Length} 项");
        }
        if (ignored.Length > 0)
        {
            parts.Add($"忽略 {ignored.Length} 项");
        }
        if (invalid.Length > 0)
        {
            parts.Add($"目标不可用 {invalid.Length} 项");
        }
        PreviewSummaryText = string.Join(" · ", parts);
    }

    private string BuildOrganizationPreview(IReadOnlyList<OrganizationDecision> decisions)
    {
        var boxes = _service.Boxes
            .Where(box => !box.IsMappedFolder)
            .ToDictionary(box => box.Id, box => box.Title);
        var assignable = decisions.Where(decision =>
            decision.Action == OrganizationRuleAction.AssignToBox &&
            decision.TargetBoxId is { } target &&
            boxes.ContainsKey(target)).ToArray();
        var invalid = decisions.Where(decision =>
            decision.Action == OrganizationRuleAction.AssignToBox &&
            (decision.TargetBoxId is not { } target || !boxes.ContainsKey(target))).ToArray();
        var keepUnassigned = decisions.Where(decision =>
            decision.Action == OrganizationRuleAction.KeepUnassigned).ToArray();
        var ignored = decisions.Where(decision =>
            decision.Action == OrganizationRuleAction.Ignore).ToArray();

        var lines = new List<string>
        {
            $"本次将评估 {decisions.Count} 个项目。"
        };
        if (assignable.Length > 0)
        {
            lines.Add($"分配到盒子: {assignable.Length} 项");
            foreach (var group in assignable.GroupBy(decision => decision.TargetBoxId!.Value))
            {
                lines.Add($"  {boxes[group.Key]}: {group.Count()} 项");
            }
        }
        if (keepUnassigned.Length > 0)
        {
            lines.Add($"保留未分配: {keepUnassigned.Length} 项");
        }
        if (ignored.Length > 0)
        {
            lines.Add($"忽略: {ignored.Length} 项");
        }
        if (invalid.Length > 0)
        {
            lines.Add($"目标盒子不可用: {invalid.Length} 项");
        }

        lines.Add(string.Empty);
        lines.Add("项目预览:");
        foreach (var decision in decisions.Take(8))
        {
            lines.Add($"- {decision.ItemName} -> {DescribeDecision(decision, boxes)}");
        }
        if (decisions.Count > 8)
        {
            lines.Add($"另有 {decisions.Count - 8} 项。");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeDecision(
        OrganizationDecision decision,
        IReadOnlyDictionary<Guid, string> boxes) => decision.Action switch
    {
        OrganizationRuleAction.AssignToBox when decision.TargetBoxId is { } target && boxes.TryGetValue(target, out var title) =>
            $"放入“{title}”",
        OrganizationRuleAction.AssignToBox => "目标盒子不可用",
        OrganizationRuleAction.KeepUnassigned => "保留在桌面",
        OrganizationRuleAction.Ignore => "忽略",
        _ => "不处理"
    };

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
            var item = Rules.FirstOrDefault(candidate => candidate.Id == copy.Id);
            if (item is not null)
            {
                SetRuleChecked(item, true);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null || !await _dialogs.ConfirmAsync("删除规则", $"删除“{SelectedRule.Title}”？", "删除")) return;
        _service.DeleteOrganizationRule(SelectedRule.Id);
    }

    [RelayCommand(CanExecute = nameof(HasMultiSelection))]
    private async Task DeleteRulesAsync()
    {
        var selected = SelectedRules.ToArray();
        if (selected.Length == 0 || !await _dialogs.ConfirmAsync(
                "删除规则",
                $"删除选中的 {selected.Length} 条规则？",
                "删除"))
        {
            return;
        }
        foreach (var item in selected)
        {
            _service.DeleteOrganizationRule(item.Id);
        }
    }

    private bool HasMultiSelection() => SelectedRules.Count > 0;

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
        // Rule or box changes invalidate an open preview; close it so the
        // user cannot act on stale decisions.
        IsPreviewVisible = false;
        PreviewSections.Clear();
        var selectedIds = SelectedRules.Select(item => item.Id).ToHashSet();
        Rules.Clear();
        foreach (var rule in _service.State.OrganizationRules.OrderBy(rule => rule.Priority))
        {
            Rules.Add(new OrganizationRuleListItem(rule, _service.Boxes));
        }
        SelectedRule = Rules.FirstOrDefault(item => selectedIds.Contains(item.Id));
        UpdateSelection(Rules.Where(item => selectedIds.Contains(item.Id)));
        OnPropertyChanged(string.Empty);
    }
}
