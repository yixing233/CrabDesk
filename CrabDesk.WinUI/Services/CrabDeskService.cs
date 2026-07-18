using CrabDesk.Runtime;
using CrabDesk.Core;

namespace CrabDesk.WinUI.Services;

public sealed class CrabDeskService : ICrabDeskService
{
    private readonly CrabDeskRuntime _runtime;

    public CrabDeskService(CrabDeskRuntime runtime)
    {
        _runtime = runtime;
    }

    public event EventHandler? Changed
    {
        add => _runtime.Changed += value;
        remove => _runtime.Changed -= value;
    }

    public CrabDeskState State => _runtime.State;
    public bool IsPaused => _runtime.IsPaused;
    public bool DesktopConnected => _runtime.DesktopConnected;
    public bool IsCheckingForUpdates => _runtime.IsCheckingForUpdates;
    public bool IsDownloadingUpdate => _runtime.IsDownloadingUpdate;
    public string CurrentVersion => _runtime.CurrentVersion;
    public string ConfigDirectory => _runtime.ConfigDirectory;
    public UpdateCheckResult LastUpdateCheck => _runtime.LastUpdateCheck;
    public bool CanUndoOrganization => _runtime.CanUndoOrganization;
    public string BackupDirectory => _runtime.BackupDirectory;
    public IReadOnlyList<DesktopBox> Boxes => _runtime.State.Boxes;
    public HotkeyRegistrationStatus GetHotkeyStatus(HotkeyAction action) => _runtime.GetHotkeyStatus(action);
    public void SetPaused(bool paused) => _runtime.SetPaused(paused);
    public Task ReconnectDesktopAsync() => _runtime.ReconnectDesktopAsync();
    public Task<bool> RepairDesktopIconsAsync() => _runtime.RepairDesktopIconsAsync();
    public void SetStartWithWindows(bool enabled) => _runtime.SetStartWithWindows(enabled);
    public Task SetShowSystemItemsAsync(bool enabled) => _runtime.SetShowSystemItemsAsync(enabled);
    public void SetConfirmDeleteBox(bool enabled) => _runtime.SetConfirmDeleteBox(enabled);
    public void SetLaunchToTray(bool enabled) => _runtime.SetLaunchToTray(enabled);
    public void SetToggleIconsOnDesktopDoubleClick(bool enabled) => _runtime.SetToggleIconsOnDesktopDoubleClick(enabled);
    public void SetExpandBoxOnHover(bool enabled) => _runtime.SetExpandBoxOnHover(enabled);
    public void SetRefreshAfterRename(bool enabled) => _runtime.SetRefreshAfterRename(enabled);
    public void SetAnimationEnabled(bool enabled) => _runtime.SetAnimationEnabled(enabled);
    public void SetThemeMode(ApplicationThemeMode mode) => _runtime.SetThemeMode(mode);
    public void SetWindowBackdrop(string backdrop) => _runtime.SetWindowBackdrop(backdrop);
    public void SetCheckUpdatesOnStartup(bool enabled) => _runtime.SetCheckUpdatesOnStartup(enabled);
    public void SetUpdateChannel(UpdateChannel channel) => _runtime.SetUpdateChannel(channel);
    public Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual = true) => _runtime.CheckForUpdatesAsync(manual);
    public Task<UpdateDownloadResult> DownloadUpdateAsync(IProgress<UpdateDownloadProgress>? progress = null) =>
        _runtime.DownloadUpdateAsync(progress);
    public void LaunchUpdateInstaller(string installerPath) => _runtime.LaunchUpdateInstaller(installerPath);
    public void OpenLatestReleasePage() => _runtime.OpenLatestReleasePage();
    public void OpenConfigDirectory() => _runtime.OpenConfigDirectory();
    public int ClearThumbnailCache() => _runtime.ClearThumbnailCache();
    public string GetDesktopHostDiagnosticsText() => _runtime.GetDesktopHostDiagnosticsText();
    public Task<LayoutResetResult> ResetLayoutAsync() => _runtime.ResetLayoutAsync();
    public DesktopBox AddBox(string title = "新盒子") => _runtime.AddBox(title);
    public Task<DesktopBox> AddMappedFolderBoxAsync(string path, bool isReadOnly = false) => _runtime.AddMappedFolderBoxAsync(path, isReadOnly);
    public Task UpdateMappedFolderAsync(DesktopBox box, string path) => _runtime.UpdateMappedFolderAsync(box, path);
    public void SetMappedFolderReadOnly(DesktopBox box, bool isReadOnly) => _runtime.SetMappedFolderReadOnly(box, isReadOnly);
    public void DeleteBox(DesktopBox box) => _runtime.DeleteBox(box);
    public void BoxChanged(DesktopBox box, bool rebuild = false) => _runtime.BoxChanged(box, rebuild);
    public void SetHotkey(HotkeyAction action, bool enabled, HotkeyModifiers modifiers, HotkeyKey key) => _runtime.SetHotkey(action, enabled, modifiers, key);
    public Task<LayoutBackupInfo> CreateBackupAsync() => _runtime.CreateBackupAsync();
    public Task<IReadOnlyList<LayoutBackupInfo>> GetBackupsAsync() => _runtime.GetBackupsAsync();
    public Task ExportBackupAsync(string path) => _runtime.ExportBackupAsync(path);
    public Task RestoreBackupAsync(string path) => _runtime.RestoreBackupAsync(path);
    public Task DeleteBackupAsync(string path) => _runtime.DeleteBackupAsync(path);
    public void SetDailyBackup(bool enabled) => _runtime.SetDailyBackup(enabled);
    public Task SetBackupRetentionDaysAsync(int days) => _runtime.SetBackupRetentionDaysAsync(days);
    public void SetOrganizationEnabled(bool enabled) => _runtime.SetOrganizationEnabled(enabled);
    public void SetRunRulesOnStartup(bool enabled) => _runtime.SetRunRulesOnStartup(enabled);
    public void SetRunRulesOnDesktopChanges(bool enabled) => _runtime.SetRunRulesOnDesktopChanges(enabled);
    public void SetReassignExistingItems(bool enabled) => _runtime.SetReassignExistingItems(enabled);
    public OrganizationApplyResult ApplyOrganizationRules(bool notify = true) => _runtime.ApplyOrganizationRules(notify);
    public void UndoLastOrganization() => _runtime.UndoLastOrganization();
    public void InstallDefaultOrganizationRules() => _runtime.InstallDefaultOrganizationRules();
    public void SetOrganizationRuleEnabled(Guid ruleId, bool enabled) => _runtime.SetOrganizationRuleEnabled(ruleId, enabled);
    public void SaveOrganizationRule(OrganizationRule rule) => _runtime.SaveOrganizationRule(rule);
    public OrganizationRule? DuplicateOrganizationRule(Guid ruleId) => _runtime.DuplicateOrganizationRule(ruleId);
    public void DeleteOrganizationRule(Guid ruleId) => _runtime.DeleteOrganizationRule(ruleId);
    public void MoveOrganizationRule(Guid ruleId, int direction) => _runtime.MoveOrganizationRule(ruleId, direction);
    public IReadOnlyList<OrganizationRuleConflict> GetOrganizationRuleConflicts() => _runtime.GetOrganizationRuleConflicts();
    public void SetCornerRadius(double value) => _runtime.SetCornerRadius(value);
    public void SetShowBoxBorder(bool enabled) => _runtime.SetShowBoxBorder(enabled);
    public void SetShowResizeGrip(bool enabled) => _runtime.SetShowResizeGrip(enabled);
    public void SetHoverFeedback(bool enabled) => _runtime.SetHoverFeedback(enabled);
    public void SetIconSpacing(double horizontal, double vertical) => _runtime.SetIconSpacing(horizontal, vertical);
    public void SetSelectionColor(string value) => _runtime.SetSelectionColor(value);
    public void SetBoxTitleAlignment(Guid? boxId, BoxTitleAlignment alignment) => _runtime.SetBoxTitleAlignment(boxId, alignment);
    public void SetBoxMaterial(Guid? boxId, BoxMaterialKind material) => _runtime.SetBoxMaterial(boxId, material);
    public void SetBoxBackground(Guid? boxId, string value) => _runtime.SetBoxBackground(boxId, value);
    public void SetBoxAccent(Guid? boxId, string value) => _runtime.SetBoxAccent(boxId, value);
    public void SetBoxOpacity(Guid? boxId, double value) => _runtime.SetBoxOpacity(boxId, value);
    public void SetBoxTitleBarHeight(Guid? boxId, double value) => _runtime.SetBoxTitleBarHeight(boxId, value);
    public void SetBoxTitleColor(Guid? boxId, string value) => _runtime.SetBoxTitleColor(boxId, value);
    public void SetBoxTitleFontFamily(Guid? boxId, string value) => _runtime.SetBoxTitleFontFamily(boxId, value);
    public void SetBoxTitleFontSize(Guid? boxId, double value) => _runtime.SetBoxTitleFontSize(boxId, value);
    public void SetBoxTitleFontBold(Guid? boxId, bool enabled) => _runtime.SetBoxTitleFontBold(boxId, enabled);
    public void SetShowCollapseButton(Guid? boxId, bool enabled) => _runtime.SetShowCollapseButton(boxId, enabled);
    public void SetBoxIconSize(Guid? boxId, double value) => _runtime.SetBoxIconSize(boxId, value);
    public void SetBoxLabelFontFamily(Guid? boxId, string value) => _runtime.SetBoxLabelFontFamily(boxId, value);
    public void SetBoxLabelFontSize(Guid? boxId, double value) => _runtime.SetBoxLabelFontSize(boxId, value);
    public void SetBoxShowItemLabels(Guid? boxId, bool enabled) => _runtime.SetBoxShowItemLabels(boxId, enabled);
    public void SetBoxViewMode(Guid? boxId, BoxViewMode mode) => _runtime.SetBoxViewMode(boxId, mode);
    public void SetBoxSortMode(Guid? boxId, BoxSortMode mode) => _runtime.SetBoxSortMode(boxId, mode);
    public void ResetAppearance() => _runtime.ResetAppearance();
}
