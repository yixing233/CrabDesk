using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public partial class OrganizationViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private OrganizationRule? _selectedRule;
    [ObservableProperty] private string _resultText = string.Empty;

    public OrganizationViewModel(ICrabDeskService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        _service.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<OrganizationRule> Rules { get; } = [];
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
        ResultText = $"已分配 {result.Assigned} 项，保留 {result.Unassigned} 项，忽略 {result.Ignored} 项";
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
        var rule = await _dialogs.EditOrganizationRuleAsync(SelectedRule, _service.Boxes);
        if (rule is not null) _service.SaveOrganizationRule(rule);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateRule()
    {
        if (SelectedRule is not null) SelectedRule = _service.DuplicateOrganizationRule(SelectedRule.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null || !await _dialogs.ConfirmAsync("删除规则", $"删除“{SelectedRule.Title}”？", "删除")) return;
        _service.DeleteOrganizationRule(SelectedRule.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))] private void MoveUp() { if (SelectedRule is not null) _service.MoveOrganizationRule(SelectedRule.Id, -1); }
    [RelayCommand(CanExecute = nameof(HasSelection))] private void MoveDown() { if (SelectedRule is not null) _service.MoveOrganizationRule(SelectedRule.Id, 1); }

    public void SetRuleEnabled(OrganizationRule rule, bool enabled) => _service.SetOrganizationRuleEnabled(rule.Id, enabled);

    partial void OnSelectedRuleChanged(OrganizationRule? value)
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
        foreach (var rule in _service.State.OrganizationRules.OrderBy(rule => rule.Priority)) Rules.Add(rule);
        SelectedRule = Rules.FirstOrDefault(rule => rule.Id == selectedId) ?? Rules.FirstOrDefault();
        OnPropertyChanged(string.Empty);
    }
}
