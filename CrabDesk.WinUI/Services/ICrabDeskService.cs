using CrabDesk.Core;

namespace CrabDesk.WinUI.Services;

public interface ICrabDeskService
{
    event EventHandler? Changed;
    CrabDeskState State { get; }
    bool IsPaused { get; }
    bool DesktopConnected { get; }
    bool IsCheckingForUpdates { get; }
    bool IsDownloadingUpdate { get; }
    string CurrentVersion { get; }
    string ConfigDirectory { get; }
    UpdateCheckResult LastUpdateCheck { get; }
    bool CanUndoOrganization { get; }
    string BackupDirectory { get; }
    IReadOnlyList<DesktopBox> Boxes { get; }
    HotkeyRegistrationStatus GetHotkeyStatus(HotkeyAction action);
    void SetPaused(bool paused);
    Task ReconnectDesktopAsync();
    void SetStartWithWindows(bool enabled);
    Task SetShowSystemItemsAsync(bool enabled);
    void SetConfirmDeleteBox(bool enabled);
    void SetLaunchToTray(bool enabled);
    void SetToggleIconsOnDesktopDoubleClick(bool enabled);
    void SetExpandBoxOnHover(bool enabled);
    void SetRefreshAfterRename(bool enabled);
    void SetAnimationEnabled(bool enabled);
    void SetThemeMode(ApplicationThemeMode mode);
    void SetCheckUpdatesOnStartup(bool enabled);
    void SetUpdateChannel(UpdateChannel channel);
    Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual = true);
    Task<UpdateDownloadResult> DownloadUpdateAsync(IProgress<UpdateDownloadProgress>? progress = null);
    void LaunchUpdateInstaller(string installerPath);
    void OpenLatestReleasePage();
    void OpenConfigDirectory();
    int ClearThumbnailCache();
    string GetDesktopHostDiagnosticsText();
    Task<LayoutResetResult> ResetLayoutAsync();
    DesktopBox AddBox(string title = "新盒子");
    Task<DesktopBox> AddMappedFolderBoxAsync(string path, bool isReadOnly = false);
    Task UpdateMappedFolderAsync(DesktopBox box, string path);
    void SetMappedFolderReadOnly(DesktopBox box, bool isReadOnly);
    void DeleteBox(DesktopBox box);
    void BoxChanged(DesktopBox box, bool rebuild = false);
    void SetHotkey(HotkeyAction action, bool enabled, HotkeyModifiers modifiers, HotkeyKey key);
    Task<LayoutBackupInfo> CreateBackupAsync();
    Task<IReadOnlyList<LayoutBackupInfo>> GetBackupsAsync();
    Task ExportBackupAsync(string path);
    Task RestoreBackupAsync(string path);
    Task DeleteBackupAsync(string path);
    void SetDailyBackup(bool enabled);
    Task SetBackupRetentionDaysAsync(int days);
    void SetOrganizationEnabled(bool enabled);
    void SetRunRulesOnStartup(bool enabled);
    void SetRunRulesOnDesktopChanges(bool enabled);
    void SetReassignExistingItems(bool enabled);
    OrganizationApplyResult ApplyOrganizationRules(bool notify = true);
    void UndoLastOrganization();
    void InstallDefaultOrganizationRules();
    void SetOrganizationRuleEnabled(Guid ruleId, bool enabled);
    void SaveOrganizationRule(OrganizationRule rule);
    OrganizationRule? DuplicateOrganizationRule(Guid ruleId);
    void DeleteOrganizationRule(Guid ruleId);
    void MoveOrganizationRule(Guid ruleId, int direction);
    IReadOnlyList<OrganizationRuleConflict> GetOrganizationRuleConflicts();
    void SetCornerRadius(double value);
    void SetShowBoxBorder(bool enabled);
    void SetShowResizeGrip(bool enabled);
    void SetHoverFeedback(bool enabled);
    void SetIconSpacing(double horizontal, double vertical);
    void SetSelectionColor(string value);
    void SetBoxTitleAlignment(Guid? boxId, BoxTitleAlignment alignment);
    void SetBoxBackground(Guid? boxId, string value);
    void SetBoxAccent(Guid? boxId, string value);
    void SetBoxOpacity(Guid? boxId, double value);
    void SetBoxTitleBarHeight(Guid? boxId, double value);
    void SetBoxTitleColor(Guid? boxId, string value);
    void SetBoxTitleFontFamily(Guid? boxId, string value);
    void SetBoxTitleFontSize(Guid? boxId, double value);
    void SetBoxTitleFontBold(Guid? boxId, bool enabled);
    void SetShowCollapseButton(Guid? boxId, bool enabled);
    void SetBoxIconSize(Guid? boxId, double value);
    void SetBoxLabelFontFamily(Guid? boxId, string value);
    void SetBoxLabelFontSize(Guid? boxId, double value);
    void SetBoxShowItemLabels(Guid? boxId, bool enabled);
    void SetBoxViewMode(Guid? boxId, BoxViewMode mode);
    void SetBoxSortMode(Guid? boxId, BoxSortMode mode);
    void ResetAppearance();
}
