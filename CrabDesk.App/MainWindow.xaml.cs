using System.Windows;
using System.Windows.Controls;
using CrabDesk.Core;

namespace CrabDesk.App;

internal sealed record BackupUiAutomationResult(
    bool BackupCreated,
    bool FailureWasReported,
    bool FailedRestorePreservedCurrentState,
    bool SuccessfulRestoreRecoveredBackup,
    bool ResetCreatedBackup,
    bool ResetBackupRestoredLayout,
    string Message);

internal sealed record ThemeCaptureEntry(
    ApplicationThemeMode Theme,
    int TabIndex,
    string Path,
    int PixelWidth,
    int PixelHeight);

internal sealed record ThemeVisualState(
    ApplicationThemeMode Theme,
    bool ResolvedDark,
    bool WindowChromeMatches,
    bool TrayThemeMatches);

internal sealed record ThemeSliderVisualState(
    ApplicationThemeMode Theme,
    string Name,
    double SliderHeight,
    double TrackHeight,
    double ThumbHeight,
    double ThumbTop,
    bool IsFullyVisible);

internal sealed record ThemeRuleTableVisualState(
    ApplicationThemeMode Theme,
    int ItemCount,
    double Width,
    double Height);

internal sealed record ThemeCaptureReport(
    IReadOnlyList<ThemeCaptureEntry> Captures,
    IReadOnlyList<ThemeVisualState> States,
    IReadOnlyList<ThemeSliderVisualState> SliderStates,
    IReadOnlyList<ThemeRuleTableVisualState> RuleTableStates);

internal sealed record OrganizationRuleListItem(
    OrganizationRule Rule,
    string ExtensionsDisplay,
    string TargetDisplay);

public partial class MainWindow : Window
{
    private sealed record HotkeyOption<T>(string Label, T Value);

    private readonly CrabDeskRuntime _runtime;
    private readonly System.Windows.Threading.DispatcherTimer _backupRetentionTimer;
    private Task _activeBackupOperation = Task.CompletedTask;
    private bool _backupAutomationMode;
    private Exception? _backupAutomationError;
    private bool _updating;

    public MainWindow(CrabDeskRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        var modifierOptions = new[]
        {
            new HotkeyOption<HotkeyModifiers>("Ctrl + Alt", HotkeyModifiers.Control | HotkeyModifiers.Alt),
            new HotkeyOption<HotkeyModifiers>("Ctrl + Shift", HotkeyModifiers.Control | HotkeyModifiers.Shift),
            new HotkeyOption<HotkeyModifiers>("Alt + Shift", HotkeyModifiers.Alt | HotkeyModifiers.Shift),
            new HotkeyOption<HotkeyModifiers>("Ctrl + Alt + Shift", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift),
            new HotkeyOption<HotkeyModifiers>("Win + Shift", HotkeyModifiers.Windows | HotkeyModifiers.Shift),
            new HotkeyOption<HotkeyModifiers>("Win + Alt", HotkeyModifiers.Windows | HotkeyModifiers.Alt)
        };
        var keyOptions = Enum.GetValues<HotkeyKey>()
            .Select(key => new HotkeyOption<HotkeyKey>(key.ToString(), key))
            .ToArray();
        ShowDesktopModifiersComboBox.ItemsSource = modifierOptions;
        OrganizeModifiersComboBox.ItemsSource = modifierOptions;
        ShowDesktopKeyComboBox.ItemsSource = keyOptions;
        OrganizeKeyComboBox.ItemsSource = keyOptions;
        _backupRetentionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _backupRetentionTimer.Tick += async (_, _) =>
        {
            _backupRetentionTimer.Stop();
            await _runtime.SetBackupRetentionDaysAsync((int)Math.Round(BackupRetentionSlider.Value));
        };
        ConfigPathText.Text = _runtime.ConfigDirectory;
        AppVersionText.Text = $"版本 {_runtime.CurrentVersion}";
        SidebarVersionText.Text = _runtime.CurrentVersion;
        _runtime.Changed += Runtime_OnChanged;
        Closed += (_, _) => _runtime.Changed -= Runtime_OnChanged;
        Loaded += async (_, _) => await RefreshBackupsAsync();
        SourceInitialized += (_, _) => ApplicationTheme.ApplyWindowChrome(this, _runtime.IsDarkTheme);
        RefreshView();
    }

    private DesktopBox? SelectedBox => BoxesList.SelectedItem as DesktopBox;
    private OrganizationRule? SelectedOrganizationRule =>
        (OrganizationRulesList.SelectedItem as OrganizationRuleListItem)?.Rule;

    private void Runtime_OnChanged(object? sender, EventArgs eventArgs)
    {
        Dispatcher.BeginInvoke(RefreshView);
    }

    private void RefreshView()
    {
        _updating = true;
        try
        {
            StatusText.Text = _runtime.IsPaused
                ? "已暂停"
                : _runtime.DesktopConnected ? "桌面已连接" : "等待 Explorer";
            PauseButton.Content = _runtime.IsPaused ? "恢复接管" : "暂停接管";
            StartupCheckBox.IsChecked = _runtime.State.Settings.StartWithWindows;
            ConfirmDeleteCheckBox.IsChecked = _runtime.State.Settings.ConfirmDeleteBox;
            LaunchToTrayCheckBox.IsChecked = _runtime.State.Settings.DesktopBehavior.LaunchToTray;
            ShowSystemItemsCheckBox.IsChecked = _runtime.State.Settings.ShowSystemItems;
            DesktopDoubleClickCheckBox.IsChecked = _runtime.State.Settings.DesktopBehavior.ToggleIconsOnDesktopDoubleClick;
            DesktopContextMenuCheckBox.IsChecked = _runtime.State.Settings.DesktopBehavior.ShowDesktopContextMenu;
            ExpandBoxOnHoverCheckBox.IsChecked = _runtime.State.Settings.DesktopBehavior.ExpandBoxOnHover;
            RefreshDesktopItemsCheckBox.IsChecked = _runtime.State.Settings.DesktopBehavior.RefreshAfterRename;
            AnimationEnabledCheckBox.IsChecked = _runtime.State.Settings.Appearance.AnimationEnabled;
            OrganizationEnabledCheckBox.IsChecked = _runtime.State.Organization.Enabled;
            RunRulesOnStartupCheckBox.IsChecked = _runtime.State.Organization.RunOnStartup;
            RunRulesRealtimeCheckBox.IsChecked = _runtime.State.Organization.RunOnDesktopChanges;
            ReassignExistingItemsCheckBox.IsChecked = _runtime.State.Organization.ReassignExistingItems;
            DailyBackupCheckBox.IsChecked = _runtime.State.Settings.Backup.DailyBackup;
            BackupRetentionSlider.Value = _runtime.State.Settings.Backup.RetentionDays;
            BackupRetentionValueText.Text = $"{_runtime.State.Settings.Backup.RetentionDays} 天";
            BackupDirectoryText.Text = _runtime.BackupDirectory;
            SystemThemeRadioButton.IsChecked = _runtime.State.Settings.ThemeMode == ApplicationThemeMode.System;
            LightThemeRadioButton.IsChecked = _runtime.State.Settings.ThemeMode == ApplicationThemeMode.Light;
            DarkThemeRadioButton.IsChecked = _runtime.State.Settings.ThemeMode == ApplicationThemeMode.Dark;
            CheckUpdatesOnStartupCheckBox.IsChecked = _runtime.State.Settings.Updates.CheckOnStartup;
            StableUpdateChannelRadioButton.IsChecked = _runtime.State.Settings.Updates.Channel == UpdateChannel.Stable;
            PreviewUpdateChannelRadioButton.IsChecked = _runtime.State.Settings.Updates.Channel == UpdateChannel.Preview;
            CheckForUpdatesButton.IsEnabled = !_runtime.IsCheckingForUpdates;
            StableUpdateChannelRadioButton.IsEnabled = !_runtime.IsCheckingForUpdates;
            PreviewUpdateChannelRadioButton.IsEnabled = !_runtime.IsCheckingForUpdates;
            RefreshUpdateStatus();
            var showDesktopHotkey = _runtime.State.Settings.Hotkeys.ShowDesktop;
            ShowDesktopHotkeyEnabledCheckBox.IsChecked = showDesktopHotkey.Enabled;
            ShowDesktopModifiersComboBox.SelectedValue = showDesktopHotkey.Modifiers;
            ShowDesktopKeyComboBox.SelectedValue = showDesktopHotkey.Key;
            UpdateHotkeyStatus(
                ShowDesktopHotkeyStatusText,
                _runtime.GetHotkeyStatus(HotkeyAction.ShowDesktop));
            var organizeHotkey = _runtime.State.Settings.Hotkeys.OrganizeDesktop;
            OrganizeHotkeyEnabledCheckBox.IsChecked = organizeHotkey.Enabled;
            OrganizeModifiersComboBox.SelectedValue = organizeHotkey.Modifiers;
            OrganizeKeyComboBox.SelectedValue = organizeHotkey.Key;
            UpdateHotkeyStatus(
                OrganizeHotkeyStatusText,
                _runtime.GetHotkeyStatus(HotkeyAction.OrganizeDesktop));
            RefreshDiagnostics();
            StatusIndicator.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[
                _runtime.DesktopConnected ? "SuccessBrush" : "MutedTextBrush"];
            ApplicationTheme.ApplyWindowChrome(this, _runtime.IsDarkTheme);

            var selectedId = SelectedBox?.Id;
            BoxesList.ItemsSource = null;
            BoxesList.ItemsSource = _runtime.State.Boxes;
            BoxesList.SelectedItem = _runtime.State.Boxes.FirstOrDefault(box => box.Id == selectedId)
                ?? _runtime.State.Boxes.FirstOrDefault();

            var appearanceSelectedId = (AppearanceBoxesList.SelectedItem as DesktopBox)?.Id;
            AppearanceBoxesList.ItemsSource = null;
            AppearanceBoxesList.ItemsSource = _runtime.State.Boxes;
            AppearanceBoxesList.SelectedItem = _runtime.State.Boxes.FirstOrDefault(box => box.Id == appearanceSelectedId)
                ?? _runtime.State.Boxes.FirstOrDefault();
            AppearanceBoxesList.IsEnabled = ApplyAppearanceToAllCheckBox.IsChecked != true;

            var selectedRuleId = SelectedOrganizationRule?.Id;
            var ruleRows = _runtime.State.OrganizationRules
                .OrderBy(rule => rule.Priority)
                .Select(CreateOrganizationRuleListItem)
                .ToArray();
            OrganizationRulesList.ItemsSource = null;
            OrganizationRulesList.ItemsSource = ruleRows;
            OrganizationRulesList.SelectedItem = ruleRows.FirstOrDefault(item => item.Rule.Id == selectedRuleId)
                ?? ruleRows.FirstOrDefault();
            UndoOrganizationButton.IsEnabled = _runtime.CanUndoOrganization;
            var conflicts = _runtime.GetOrganizationRuleConflicts();
            OrganizationConflictText.Text = conflicts.Count == 0
                ? string.Empty
                : $"{conflicts.Count} 组规则可能重叠，将按列表顺序处理";
            UpdateRuleCommandState();
            RefreshSelectedBox();
            RefreshAppearanceControls();
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshUpdateStatus()
    {
        var settings = _runtime.State.Settings.Updates;
        UpdateRepositoryText.Text = string.IsNullOrWhiteSpace(settings.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(settings.RepositoryName)
            ? "发布仓库尚未配置"
            : $"{settings.RepositoryOwner}/{settings.RepositoryName}";
        LastUpdateCheckText.Text = settings.LastCheckedAt is { } checkedAt
            ? $"上次检查 {checkedAt.LocalDateTime:MM-dd HH:mm}"
            : "尚未检查";

        var result = _runtime.LastUpdateCheck;
        UpdateStatusText.Text = _runtime.IsCheckingForUpdates
            ? "正在连接 GitHub Releases..."
            : result.Status switch
            {
                UpdateCheckStatus.NotConfigured => "尚未配置 GitHub 发布仓库",
                UpdateCheckStatus.UpToDate => "当前已是最新版本",
                UpdateCheckStatus.UpdateAvailable => $"发现新版本 {result.LatestVersion}",
                UpdateCheckStatus.Offline => "暂时无法连接 GitHub",
                UpdateCheckStatus.RateLimited => "GitHub 请求频率已达上限",
                UpdateCheckStatus.Failed => "检查更新失败",
                _ => "可手动检查 GitHub Releases 中的新版本"
            };
        UpdateStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[
            result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable => "AccentBrush",
                UpdateCheckStatus.UpToDate => "SuccessBrush",
                UpdateCheckStatus.Failed or UpdateCheckStatus.RateLimited => "DangerBrush",
                _ => "TextBrush"
            }];
        UpdateReleaseNameText.Text = !string.IsNullOrWhiteSpace(result.ReleaseName)
            ? result.ReleaseName
            : result.Message;
        var releaseUrl = !string.IsNullOrWhiteSpace(result.ReleasePageUrl)
            ? result.ReleasePageUrl
            : settings.CachedReleasePageUrl;
        OpenReleasePageButton.Visibility = string.IsNullOrWhiteSpace(releaseUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;
        var notes = !string.IsNullOrWhiteSpace(result.ReleaseNotes)
            ? result.ReleaseNotes
            : settings.CachedReleaseNotes;
        ReleaseNotesText.Text = notes;
        ReleaseNotesBorder.Visibility = string.IsNullOrWhiteSpace(notes)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RefreshDiagnostics()
    {
        var diagnostics = _runtime.GetDesktopHostDiagnostics();
        DiagnosticsStatusText.Text = diagnostics.Connected
            ? $"桌面已连接 · {diagnostics.SurfaceCount}/{diagnostics.MonitorCount} 个表面"
            : diagnostics.Paused ? "桌面接管已暂停" : "等待 Explorer 桌面宿主";
        DiagnosticsHostText.Text =
            $"{diagnostics.DesktopViewClass} {diagnostics.DesktopViewHandle} · 列表 {diagnostics.DesktopListViewHandle}";
        DiagnosticsTopologyText.Text = diagnostics.Monitors.Count == 0
            ? "未检测到显示器"
            : string.Join(" · ", diagnostics.Monitors);
    }

    private void RefreshSelectedBox()
    {
        if (SelectedBox is not { } box)
        {
            BoxTitleTextBox.Text = string.Empty;
            MappedFolderPanel.Visibility = Visibility.Collapsed;
            return;
        }
        BoxTitleTextBox.Text = box.Title;
        MappedFolderPanel.Visibility = box.IsMappedFolder ? Visibility.Visible : Visibility.Collapsed;
        if (box.MappedFolder is not { } mappedFolder)
        {
            return;
        }
        MappedFolderPathText.Text = mappedFolder.Path;
        MappedFolderReadOnlyCheckBox.IsChecked = mappedFolder.IsReadOnly;
        var snapshot = _runtime.GetMappedFolderSnapshot(box.Id);
        MappedFolderStatusText.Text = snapshot?.Availability switch
        {
            MappedFolderAvailability.Available => $"可用 · {snapshot.Items.Count} 个项目",
            MappedFolderAvailability.Missing => "文件夹不存在",
            MappedFolderAvailability.Offline => "磁盘或网络位置不可用",
            MappedFolderAvailability.AccessDenied => "没有访问权限",
            MappedFolderAvailability.Error => snapshot.Message ?? "读取失败",
            _ => "正在读取"
        };
    }

    private void PauseButton_OnClick(object sender, RoutedEventArgs eventArgs) => _runtime.SetPaused(!_runtime.IsPaused);
    private async void ReconnectButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        await _runtime.ReconnectDesktopAsync();

    private void AddBoxButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var box = _runtime.AddBox();
        RefreshView();
        BoxesList.SelectedItem = box;
    }

    private async void AddMappedFolderButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要映射到桌面的文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            var box = await _runtime.AddMappedFolderBoxAsync(dialog.FolderName);
            RefreshView();
            BoxesList.SelectedItem = box;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ChangeMappedFolderButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedBox is not { IsMappedFolder: true } box)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "更改映射文件夹",
            InitialDirectory = Directory.Exists(box.MappedFolder!.Path) ? box.MappedFolder.Path : null,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            await _runtime.UpdateMappedFolderAsync(box, dialog.FolderName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MappedFolderReadOnlyCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && SelectedBox is { IsMappedFolder: true } box)
        {
            _runtime.SetMappedFolderReadOnly(box, MappedFolderReadOnlyCheckBox.IsChecked == true);
        }
    }

    private void HotkeyControl_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating)
        {
            return;
        }
        if (ReferenceEquals(sender, ShowDesktopHotkeyEnabledCheckBox) ||
            ReferenceEquals(sender, ShowDesktopModifiersComboBox) ||
            ReferenceEquals(sender, ShowDesktopKeyComboBox))
        {
            UpdateHotkeyFromControls(
                HotkeyAction.ShowDesktop,
                ShowDesktopHotkeyEnabledCheckBox,
                ShowDesktopModifiersComboBox,
                ShowDesktopKeyComboBox);
        }
        else
        {
            UpdateHotkeyFromControls(
                HotkeyAction.OrganizeDesktop,
                OrganizeHotkeyEnabledCheckBox,
                OrganizeModifiersComboBox,
                OrganizeKeyComboBox);
        }
    }

    private void UpdateHotkeyFromControls(
        HotkeyAction action,
        CheckBox enabledCheckBox,
        ComboBox modifiersComboBox,
        ComboBox keyComboBox)
    {
        if (modifiersComboBox.SelectedValue is HotkeyModifiers modifiers &&
            keyComboBox.SelectedValue is HotkeyKey key)
        {
            _runtime.SetHotkey(action, enabledCheckBox.IsChecked == true, modifiers, key);
        }
    }

    private static void UpdateHotkeyStatus(TextBlock textBlock, HotkeyRegistrationStatus status)
    {
        textBlock.Text = status switch
        {
            HotkeyRegistrationStatus.Registered => "已注册",
            HotkeyRegistrationStatus.Conflict => "快捷键已被占用",
            HotkeyRegistrationStatus.Failed => "注册失败",
            _ => "未启用"
        };
        var resourceKey = status switch
        {
            HotkeyRegistrationStatus.Registered => "SuccessBrush",
            HotkeyRegistrationStatus.Conflict or HotkeyRegistrationStatus.Failed => "DangerBrush",
            _ => "MutedTextBrush"
        };
        textBlock.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[resourceKey];
    }

    private void DeleteBoxButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedBox is not { } box || box.IsSystemBox)
        {
            return;
        }
        var detail = box.IsMappedFolder
            ? "不会删除映射文件夹或其中的文件。"
            : "文件会恢复为未分组状态。";
        if (!_runtime.State.Settings.ConfirmDeleteBox || MessageBox.Show(
                $"删除“{box.Title}”？{detail}",
                "CrabDesk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _runtime.DeleteBox(box);
        }
    }

    private void StartupCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetStartWithWindows(StartupCheckBox.IsChecked == true);
        }
    }

    private void ConfirmDeleteCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating)
        {
            return;
        }
        _runtime.SetConfirmDeleteBox(ConfirmDeleteCheckBox.IsChecked == true);
    }

    private void LaunchToTrayCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            _runtime.SetLaunchToTray(LaunchToTrayCheckBox.IsChecked == true);
        }
    }

    private async void ShowSystemItemsCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            await _runtime.SetShowSystemItemsAsync(ShowSystemItemsCheckBox.IsChecked == true);
        }
    }

    private void DesktopContextMenuCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating)
        {
            return;
        }
        try
        {
            _runtime.SetDesktopContextMenuEnabled(DesktopContextMenuCheckBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshView();
        }
    }

    private void DesktopDoubleClickCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating)
        {
            return;
        }
        try
        {
            _runtime.SetToggleIconsOnDesktopDoubleClick(DesktopDoubleClickCheckBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshView();
        }
    }

    private void ExpandBoxOnHoverCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetExpandBoxOnHover(ExpandBoxOnHoverCheckBox.IsChecked == true);
        }
    }

    private void RefreshDesktopItemsCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetRefreshAfterRename(RefreshDesktopItemsCheckBox.IsChecked == true);
        }
    }

    private void AnimationEnabledCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetAnimationEnabled(AnimationEnabledCheckBox.IsChecked == true);
        }
    }

    private void ThemeModeRadioButton_OnChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating || sender is not System.Windows.Controls.RadioButton radioButton ||
            !Enum.TryParse<ApplicationThemeMode>(radioButton.Tag?.ToString(), out var mode))
        {
            return;
        }

        _runtime.SetThemeMode(mode);
        if (_runtime.State.Settings.Appearance.AnimationEnabled)
        {
            BeginAnimation(
                OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    0.92,
                    1,
                    TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                });
        }
    }

    private void OrganizationEnabledCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            _runtime.SetOrganizationEnabled(OrganizationEnabledCheckBox.IsChecked == true);
        }
    }

    private void CheckUpdatesOnStartupCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetCheckUpdatesOnStartup(CheckUpdatesOnStartupCheckBox.IsChecked == true);
        }
    }

    private void UpdateChannelRadioButton_OnChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && sender is RadioButton radioButton &&
            Enum.TryParse<UpdateChannel>(radioButton.Tag?.ToString(), out var channel))
        {
            _runtime.SetUpdateChannel(channel);
        }
    }

    private async void CheckForUpdatesButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            await _runtime.CheckForUpdatesAsync();
        }
        finally
        {
            RefreshView();
        }
    }

    private void OpenReleasePageButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        _runtime.OpenLatestReleasePage();

    private void PrivacyButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        _runtime.OpenLocalDocument("PRIVACY.md");

    private void LicenseButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        _runtime.OpenLocalDocument("LICENSE");

    private void ClearThumbnailCacheButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var cleared = _runtime.ClearThumbnailCache();
        ThumbnailCacheStatusText.Text = cleared == 0
            ? "缓存已经是空的"
            : $"已清理 {cleared} 个缓存项";
    }

    private void OpenConfigDirectoryButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        _runtime.OpenConfigDirectory();

    private void CopyDiagnosticsButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Clipboard.SetText(_runtime.GetDesktopHostDiagnosticsText());
            DiagnosticsStatusText.Text = "诊断信息已复制";
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            MessageBox.Show(exception.Message, "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResetLayoutButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (MessageBox.Show(
            "重置为默认桌面盒子布局？当前布局会先自动备份，桌面文件不会移动或删除。",
            "CrabDesk",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var result = await _runtime.ResetLayoutAsync();
            RefreshView();
            await RefreshBackupsAsync(result.Backup.Path);
            var ruleMessage = result.DisabledRuleCount == 0
                ? string.Empty
                : $"\n{result.DisabledRuleCount} 条引用旧盒子的整理规则已停用。";
            MessageBox.Show(
                $"布局已重置。恢复备份创建于 {result.Backup.CreatedAt:yyyy-MM-dd HH:mm:ss}。{ruleMessage}",
                "CrabDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunRulesOnStartupCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            _runtime.SetRunRulesOnStartup(RunRulesOnStartupCheckBox.IsChecked == true);
        }
    }

    private void RunRulesRealtimeCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            _runtime.SetRunRulesOnDesktopChanges(RunRulesRealtimeCheckBox.IsChecked == true);
        }
    }

    private void ReassignExistingItemsCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            _runtime.SetReassignExistingItems(ReassignExistingItemsCheckBox.IsChecked == true);
        }
    }

    private void ApplyOrganizationRulesButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var preview = _runtime.PreviewOrganizationRules();
        if (preview.Count == 0)
        {
            OrganizationResultText.Text = "没有匹配项目";
            return;
        }
        var dialog = new OrganizationPreviewDialog(preview, _runtime.State.Boxes, _runtime.IsDarkTheme) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var result = _runtime.ApplyOrganizationRules();
        OrganizationResultText.Text = $"已分组 {result.Assigned}，移出 {result.Unassigned}，忽略 {result.Ignored}" +
          (result.InvalidTargets > 0 ? $"，无效目标 {result.InvalidTargets}" : string.Empty);
    }

    private void InstallDefaultRulesButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _runtime.InstallDefaultOrganizationRules();
        OrganizationResultText.Text = "内置规则已就绪";
    }

    private void UndoOrganizationButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _runtime.UndoLastOrganization();
        OrganizationResultText.Text = "已撤销上次整理";
    }

    private void AddRuleButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        EditOrganizationRule(null);
    }

    private void EditRuleButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedOrganizationRule is { } rule)
        {
            EditOrganizationRule(rule);
        }
    }

    private void DuplicateRuleButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedOrganizationRule is not { } rule)
        {
            return;
        }
        var copy = _runtime.DuplicateOrganizationRule(rule.Id);
        RefreshView();
        OrganizationRulesList.SelectedItem = OrganizationRulesList.Items
            .OfType<OrganizationRuleListItem>()
            .FirstOrDefault(item => item.Rule.Id == copy?.Id);
        OrganizationResultText.Text = copy is null ? string.Empty : "规则副本已创建";
    }

    private void EditOrganizationRule(OrganizationRule? rule)
    {
        var dialog = new OrganizationRuleEditorDialog(
            rule,
            _runtime.State.Boxes.Where(box => !box.IsMappedFolder).ToArray(),
            _runtime.IsDarkTheme)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.EditedRule is not { } edited)
        {
            return;
        }
        _runtime.SaveOrganizationRule(edited);
        RefreshView();
        OrganizationRulesList.SelectedItem = OrganizationRulesList.Items
            .OfType<OrganizationRuleListItem>()
            .FirstOrDefault(item => item.Rule.Id == edited.Id);
        OrganizationResultText.Text = "规则已保存";
    }

    private void RuleEnabledCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && sender is CheckBox { DataContext: OrganizationRuleListItem item } checkBox)
        {
            _runtime.SetOrganizationRuleEnabled(item.Rule.Id, checkBox.IsChecked == true);
        }
    }

    private void MoveRuleUpButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedOrganizationRule is { } rule)
        {
            _runtime.MoveOrganizationRule(rule.Id, -1);
        }
    }

    private void MoveRuleDownButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedOrganizationRule is { } rule)
        {
            _runtime.MoveOrganizationRule(rule.Id, 1);
        }
    }

    private void DeleteRuleButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedOrganizationRule is not { } rule)
        {
            return;
        }
        if (MessageBox.Show(
                $"删除规则“{rule.Title}”？",
                "CrabDesk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _runtime.DeleteOrganizationRule(rule.Id);
        }
    }

    private OrganizationRuleListItem CreateOrganizationRuleListItem(OrganizationRule rule)
    {
        var extensions = rule.Extensions.Count == 0
            ? "*"
            : string.Join("; ", rule.Extensions.Select(extension =>
                extension.StartsWith('.') ? extension : $".{extension}"));
        var target = rule.Action switch
        {
            OrganizationRuleAction.KeepUnassigned => "未分组",
            OrganizationRuleAction.Ignore => "忽略",
            _ => _runtime.State.Boxes.FirstOrDefault(box => box.Id == rule.TargetBoxId)?.Title ?? "未选择"
        };
        return new OrganizationRuleListItem(rule, extensions, target);
    }

    private void OrganizationRulesList_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs) =>
        UpdateRuleCommandState();

    private void OrganizationRulesList_OnMouseDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs eventArgs)
    {
        if (SelectedOrganizationRule is { } rule)
        {
            EditOrganizationRule(rule);
        }
    }

    private void UpdateRuleCommandState()
    {
        if (!IsInitialized)
        {
            return;
        }
        var rule = SelectedOrganizationRule;
        var ordered = _runtime.State.OrganizationRules.OrderBy(candidate => candidate.Priority).ToArray();
        var index = rule is null ? -1 : Array.FindIndex(ordered, candidate => candidate.Id == rule.Id);
        EditRuleButton.IsEnabled = rule is not null;
        DuplicateRuleButton.IsEnabled = rule is not null;
        DeleteRuleButton.IsEnabled = rule is not null;
        MoveRuleUpButton.IsEnabled = index > 0;
        MoveRuleDownButton.IsEnabled = index >= 0 && index < ordered.Length - 1;
    }

    private async void CreateBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _activeBackupOperation = RunBackupOperationAsync(async () =>
        {
            var backup = await _runtime.CreateBackupAsync();
            BackupStatusText.Text = $"已备份 {backup.CreatedAt:HH:mm:ss}";
            await RefreshBackupsAsync(backup.Path);
        });
        await _activeBackupOperation;
    }

    private void DailyBackupCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            _runtime.SetDailyBackup(DailyBackupCheckBox.IsChecked == true);
        }
    }

    private void BackupRetentionSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        BackupRetentionValueText.Text = $"{eventArgs.NewValue:0} 天";
        if (!_updating)
        {
            _backupRetentionTimer.Stop();
            _backupRetentionTimer.Start();
        }
    }

    private async void ExportBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 CrabDesk 布局",
            Filter = "CrabDesk 布局 (*.crabdesk.json)|*.crabdesk.json|JSON 文件 (*.json)|*.json",
            FileName = $"CrabDesk-{DateTime.Now:yyyyMMdd-HHmmss}.crabdesk.json",
            AddExtension = true,
            DefaultExt = ".crabdesk.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunBackupOperationAsync(async () =>
        {
            await _runtime.ExportBackupAsync(dialog.FileName);
            BackupStatusText.Text = "布局已导出";
        });
    }

    private async void ImportBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 CrabDesk 布局",
            Filter = "CrabDesk 布局 (*.crabdesk.json)|*.crabdesk.json|JSON 文件 (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true || MessageBox.Show(
                "导入后将替换当前盒子、规则和设置。继续吗？",
                "CrabDesk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        await RestoreBackupPathAsync(dialog.FileName);
    }

    private async void RestoreBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (BackupList.SelectedItem is not LayoutBackupInfo backup ||
            (!_backupAutomationMode && MessageBox.Show(
                $"恢复 {backup.CreatedAt:yyyy-MM-dd HH:mm:ss} 的布局？",
                "CrabDesk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes))
        {
            return;
        }
        _activeBackupOperation = RestoreBackupPathAsync(backup.Path);
        await _activeBackupOperation;
    }

    private async void DeleteBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (BackupList.SelectedItem is not LayoutBackupInfo backup || MessageBox.Show(
                $"删除 {backup.CreatedAt:yyyy-MM-dd HH:mm:ss} 的备份？",
                "CrabDesk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        await RunBackupOperationAsync(async () =>
        {
            await _runtime.DeleteBackupAsync(backup.Path);
            BackupStatusText.Text = "备份已删除";
            await RefreshBackupsAsync();
        });
    }

    private async Task RestoreBackupPathAsync(string path)
    {
        await RunBackupOperationAsync(async () =>
        {
            await _runtime.RestoreBackupAsync(path);
            BackupStatusText.Text = "布局已恢复";
            RefreshView();
            await RefreshBackupsAsync();
        });
    }

    private async Task RefreshBackupsAsync(string? selectedPath = null)
    {
        try
        {
            selectedPath ??= (BackupList.SelectedItem as LayoutBackupInfo)?.Path;
            var backups = await _runtime.GetBackupsAsync();
            BackupList.ItemsSource = backups;
            BackupList.SelectedItem = backups.FirstOrDefault(backup =>
                string.Equals(backup.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? backups.FirstOrDefault();
            BackupDirectoryText.Text = _runtime.BackupDirectory;
        }
        catch (Exception exception)
        {
            BackupStatusText.Text = exception.Message;
        }
    }

    private async Task RunBackupOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            if (_backupAutomationMode)
            {
                _backupAutomationError = exception;
            }
            else
            {
                MessageBox.Show(exception.Message, "CrabDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal async Task<BackupUiAutomationResult> RunBackupUiAutomationAsync()
    {
        _backupAutomationMode = true;
        try
        {
            await RefreshBackupsAsync();
            CreateBackupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _activeBackupOperation;
            if (BackupList.SelectedItem is not LayoutBackupInfo originalBackup)
            {
                return new BackupUiAutomationResult(false, false, false, false, false, false, "备份列表没有新建的备份");
            }

            var box = _runtime.State.Boxes.First();
            BoxesList.SelectedItem = box;
            BoxTitleTextBox.Text = "Modified Layout";
            BoxTitleTextBox_OnLostFocus(BoxTitleTextBox, new RoutedEventArgs());
            var modified = box.Title == "Modified Layout";

            _backupAutomationError = null;
            using (File.Open(originalBackup.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                BackupList.SelectedItem = originalBackup;
                RestoreBackupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await _activeBackupOperation;
            }
            var failureReported = _backupAutomationError is not null;
            var failurePreserved = modified && _runtime.State.Boxes.First().Title == "Modified Layout";

            _backupAutomationError = null;
            BackupList.SelectedItem = originalBackup;
            RestoreBackupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _activeBackupOperation;
            var restored = _backupAutomationError is null &&
                _runtime.State.Boxes.First().Title == "Original Layout";
            var resetResult = await _runtime.ResetLayoutAsync();
            var resetCreatedBackup = File.Exists(resetResult.Backup.Path) &&
                _runtime.State.Boxes.All(candidate => candidate.Title != "Original Layout");
            await _runtime.RestoreBackupAsync(resetResult.Backup.Path);
            var resetBackupRestored = _runtime.State.Boxes.First().Title == "Original Layout";
            return new BackupUiAutomationResult(
                true,
                failureReported,
                failurePreserved,
                restored,
                resetCreatedBackup,
                resetBackupRestored,
                restored && resetCreatedBackup && resetBackupRestored
                    ? "设置窗口备份、重置和恢复验证通过"
                    : _backupAutomationError?.Message ?? "备份、重置或恢复后的布局不正确");
        }
        finally
        {
            _backupAutomationMode = false;
        }
    }

    internal async Task<ThemeCaptureReport> CaptureThemeScreenshotsAsync(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var originalTheme = _runtime.State.Settings.ThemeMode;
        var originalTab = SettingsTabs.SelectedIndex;
        var originalHeight = Height;
        var captures = new List<ThemeCaptureEntry>();
        var states = new List<ThemeVisualState>();
        var sliderStates = new List<ThemeSliderVisualState>();
        var ruleTableStates = new List<ThemeRuleTableVisualState>();
        try
        {
            foreach (var theme in new[]
                     {
                         ApplicationThemeMode.Light,
                         ApplicationThemeMode.Dark,
                         ApplicationThemeMode.System
                     })
            {
                _runtime.SetThemeMode(theme);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                ApplicationTheme.ApplyWindowChrome(this, _runtime.IsDarkTheme);
                var chromeStateAvailable = ApplicationTheme.TryGetWindowChromeDarkState(this, out var chromeIsDark);
                states.Add(new ThemeVisualState(
                    theme,
                    _runtime.IsDarkTheme,
                    chromeStateAvailable && chromeIsDark == _runtime.IsDarkTheme,
                    _runtime.TrayThemeMatchesCurrentTheme()));
                for (var tabIndex = 0; tabIndex < SettingsTabs.Items.Count; tabIndex++)
                {
                    SettingsTabs.SelectedIndex = tabIndex;
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    foreach (var slider in new[]
                             {
                                 BackupRetentionSlider,
                                 BoxOpacitySlider,
                                 CornerRadiusSlider,
                                 TitleBarHeightSlider,
                                 TitleFontSizeSlider,
                                 IconSizeSlider,
                                 LabelFontSizeSlider,
                                 HorizontalSpacingSlider,
                                 VerticalSpacingSlider
                             })
                    {
                        if (!slider.IsVisible || slider.ActualHeight <= 0 ||
                            sliderStates.Any(state => state.Theme == theme && state.Name == slider.Name))
                        {
                            continue;
                        }
                        slider.ApplyTemplate();
                        if (slider.Template.FindName("PART_Track", slider) is not System.Windows.Controls.Primitives.Track track)
                        {
                            continue;
                        }
                        track.ApplyTemplate();
                        var thumb = track.Thumb;
                        var thumbTop = thumb.TransformToAncestor(slider).Transform(new Point()).Y;
                        sliderStates.Add(new ThemeSliderVisualState(
                            theme,
                            slider.Name,
                            slider.ActualHeight,
                            track.ActualHeight,
                            thumb.ActualHeight,
                            thumbTop,
                            thumbTop >= -0.1 && thumbTop + thumb.ActualHeight <= slider.ActualHeight + 0.1));
                    }
                    if (tabIndex == 3)
                    {
                        ruleTableStates.Add(new ThemeRuleTableVisualState(
                            theme,
                            OrganizationRulesList.Items.Count,
                            OrganizationRulesList.ActualWidth,
                            OrganizationRulesList.ActualHeight));
                    }

                    async Task CaptureCurrentViewAsync(string suffix)
                    {
                        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                        var pixelWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
                        var pixelHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            pixelWidth,
                            pixelHeight,
                            96 * dpi.DpiScaleX,
                            96 * dpi.DpiScaleY,
                            System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(this);
                        bitmap.Freeze();

                        var path = Path.Combine(fullDirectory, $"{theme}-{tabIndex + 1:00}{suffix}.png");
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            encoder.Save(stream);
                            await stream.FlushAsync();
                        }
                        captures.Add(new ThemeCaptureEntry(theme, tabIndex, path, pixelWidth, pixelHeight));
                    }

                    await CaptureCurrentViewAsync(string.Empty);
                    var pageScrollViewer = tabIndex switch
                    {
                        0 => GeneralScrollViewer,
                        4 => AppearanceScrollViewer,
                        6 => AboutScrollViewer,
                        _ => null
                    };
                    if (pageScrollViewer is not null && pageScrollViewer.ScrollableHeight > 1)
                    {
                        pageScrollViewer.ScrollToBottom();
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                        await CaptureCurrentViewAsync("-Bottom");
                        pageScrollViewer.ScrollToTop();
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    }
                    Height = 1400;
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    await CaptureCurrentViewAsync("-Expanded");
                    Height = originalHeight;
                    UpdateLayout();
                }
            }
        }
        finally
        {
            _runtime.SetThemeMode(originalTheme);
            SettingsTabs.SelectedIndex = originalTab;
            Height = originalHeight;
            UpdateLayout();
        }
        return new ThemeCaptureReport(captures, states, sliderStates, ruleTableStates);
    }

    private void BoxesList_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_updating)
        {
            RefreshSelectedBox();
        }
    }

    private void BoxTitleTextBox_OnLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating || SelectedBox is not { } box || string.IsNullOrWhiteSpace(BoxTitleTextBox.Text))
        {
            return;
        }
        box.Title = BoxTitleTextBox.Text.Trim();
        _runtime.BoxChanged(box, true);
    }

    private Guid? AppearanceTargetBoxId => ApplyAppearanceToAllCheckBox.IsChecked == true
        ? null
        : (AppearanceBoxesList.SelectedItem as DesktopBox)?.Id;

    private DesktopBox? AppearancePreviewBox => AppearanceBoxesList.SelectedItem as DesktopBox
        ?? _runtime.State.Boxes.FirstOrDefault();

    private void ApplyAppearanceToAllCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        AppearanceBoxesList.IsEnabled = ApplyAppearanceToAllCheckBox.IsChecked != true;
        if (AppearanceBoxesList.SelectedItem is null)
        {
            AppearanceBoxesList.SelectedItem = _runtime.State.Boxes.FirstOrDefault();
        }
        RefreshAppearanceControls();
    }

    private void AppearanceBoxesList_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_updating && IsInitialized)
        {
            RefreshAppearanceControls();
        }
    }

    private void RefreshAppearanceControls()
    {
        if (!IsInitialized || AppearancePreviewBox is not { } box)
        {
            return;
        }

        var wasUpdating = _updating;
        _updating = true;
        try
        {
            var appearance = _runtime.State.Settings.Appearance;
            BoxBackgroundTextBox.Text = box.Appearance.Background;
            UpdateColorPreview(BoxBackgroundPreview, box.Appearance.Background);
            BoxAccentTextBox.Text = box.Appearance.Accent;
            UpdateColorPreview(BoxAccentPreview, box.Appearance.Accent);
            BoxOpacitySlider.Value = box.Appearance.Opacity * 100;
            BoxOpacityValueText.Text = $"{box.Appearance.Opacity:P0}";
            CornerRadiusSlider.Value = appearance.CornerRadius;
            CornerRadiusValueText.Text = $"{appearance.CornerRadius:0}px";
            ShowBoxBorderCheckBox.IsChecked = appearance.ShowBorder;
            ShowResizeGripCheckBox.IsChecked = appearance.ShowResizeGrip;
            TitleBarHeightSlider.Value = box.Appearance.TitleBarHeight;
            TitleBarHeightValueText.Text = $"{box.Appearance.TitleBarHeight:0}px";
            TitleLeftRadioButton.IsChecked = box.Appearance.TitleAlignment == BoxTitleAlignment.Left;
            TitleCenterRadioButton.IsChecked = box.Appearance.TitleAlignment == BoxTitleAlignment.Center;
            TitleFontSizeSlider.Value = box.Appearance.TitleFontSize;
            TitleFontSizeValueText.Text = $"{box.Appearance.TitleFontSize:0}px";
            TitleFontBoldCheckBox.IsChecked = box.Appearance.TitleFontBold;
            ShowCollapseButtonCheckBox.IsChecked = box.Appearance.ShowCollapseButton;
            TitleColorTextBox.Text = box.Appearance.TitleColor;
            UpdateTitleColorPreview(box.Appearance.TitleColor);
            IconSizeSlider.Value = box.Appearance.IconSize;
            IconSizeValueText.Text = $"{box.Appearance.IconSize:0}px";
            LabelFontSizeSlider.Value = box.Appearance.LabelFontSize;
            LabelFontSizeValueText.Text = $"{box.Appearance.LabelFontSize:0.#}px";
            ShowItemLabelsCheckBox.IsChecked = box.Appearance.ShowItemLabels;
            ShowShortcutBadgesCheckBox.IsChecked = box.Appearance.ShowShortcutBadges;
            HoverFeedbackCheckBox.IsChecked = appearance.HoverFeedback;
            HorizontalSpacingSlider.Value = appearance.IconHorizontalSpacing;
            HorizontalSpacingValueText.Text = $"{appearance.IconHorizontalSpacing:0}px";
            VerticalSpacingSlider.Value = appearance.IconVerticalSpacing;
            VerticalSpacingValueText.Text = $"{appearance.IconVerticalSpacing:0}px";
            GridViewRadioButton.IsChecked = box.ViewMode == BoxViewMode.Grid;
            ListViewRadioButton.IsChecked = box.ViewMode == BoxViewMode.List;
            ManualSortRadioButton.IsChecked = box.SortMode == BoxSortMode.Manual;
            NameSortRadioButton.IsChecked = box.SortMode == BoxSortMode.Name;
            TypeSortRadioButton.IsChecked = box.SortMode == BoxSortMode.Type;
            ModifiedSortRadioButton.IsChecked = box.SortMode == BoxSortMode.Modified;
            SelectionColorTextBox.Text = appearance.SelectionColor;
            UpdateSelectionColorPreview(appearance.SelectionColor);
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    private void CornerRadiusSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        CornerRadiusValueText.Text = $"{eventArgs.NewValue:0}px";
        if (!_updating)
        {
            _runtime.SetCornerRadius(eventArgs.NewValue);
        }
    }

    private void BoxBackgroundTextBox_OnLostFocus(object sender, RoutedEventArgs eventArgs) =>
        ApplyBoxColor(
            BoxBackgroundTextBox,
            BoxBackgroundPreview,
            AppearancePreviewBox?.Appearance.Background ?? "#FF2A2D32",
            value => _runtime.SetBoxBackground(AppearanceTargetBoxId, value));

    private void BoxAccentTextBox_OnLostFocus(object sender, RoutedEventArgs eventArgs) =>
        ApplyBoxColor(
            BoxAccentTextBox,
            BoxAccentPreview,
            AppearancePreviewBox?.Appearance.Accent ?? "#FF4EA1D3",
            value => _runtime.SetBoxAccent(AppearanceTargetBoxId, value));

    private void BoxOpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        BoxOpacityValueText.Text = $"{eventArgs.NewValue:0}%";
        if (!_updating)
        {
            _runtime.SetBoxOpacity(AppearanceTargetBoxId, eventArgs.NewValue / 100);
        }
    }

    private void ApplyBoxColor(
        TextBox textBox,
        Border preview,
        string currentValue,
        Action<string> apply)
    {
        if (_updating)
        {
            return;
        }
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(textBox.Text.Trim());
            var normalized = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            textBox.Text = normalized;
            UpdateColorPreview(preview, normalized);
            apply(normalized);
        }
        catch (FormatException)
        {
            textBox.Text = currentValue;
            UpdateColorPreview(preview, currentValue);
        }
    }

    private static void UpdateColorPreview(Border preview, string value)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            preview.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        catch (FormatException)
        {
            preview.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void ShowBoxBorderCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetShowBoxBorder(ShowBoxBorderCheckBox.IsChecked == true);
        }
    }

    private void ShowResizeGripCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetShowResizeGrip(ShowResizeGripCheckBox.IsChecked == true);
        }
    }

    private void BoxTitleAlignmentRadioButton_OnChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && sender is System.Windows.Controls.RadioButton radioButton &&
            Enum.TryParse<BoxTitleAlignment>(radioButton.Tag?.ToString(), out var alignment))
        {
            _runtime.SetBoxTitleAlignment(AppearanceTargetBoxId, alignment);
        }
    }

    private void TitleBarHeightSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        TitleBarHeightValueText.Text = $"{eventArgs.NewValue:0}px";
        if (!_updating)
        {
            _runtime.SetBoxTitleBarHeight(AppearanceTargetBoxId, eventArgs.NewValue);
        }
    }

    private void TitleFontSizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        TitleFontSizeValueText.Text = $"{eventArgs.NewValue:0}px";
        if (!_updating)
        {
            _runtime.SetBoxTitleFontSize(AppearanceTargetBoxId, eventArgs.NewValue);
        }
    }

    private void TitleFontBoldCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetBoxTitleFontBold(AppearanceTargetBoxId, TitleFontBoldCheckBox.IsChecked == true);
        }
    }

    private void ShowCollapseButtonCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetShowCollapseButton(AppearanceTargetBoxId, ShowCollapseButtonCheckBox.IsChecked == true);
        }
    }

    private void TitleColorTextBox_OnLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating)
        {
            return;
        }

        var value = TitleColorTextBox.Text.Trim();
        if (value.Equals("Auto", StringComparison.OrdinalIgnoreCase) || value == "自动")
        {
            TitleColorTextBox.Text = "Auto";
            UpdateTitleColorPreview("Auto");
            _runtime.SetBoxTitleColor(AppearanceTargetBoxId, "Auto");
            return;
        }

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            var normalized = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            TitleColorTextBox.Text = normalized;
            UpdateTitleColorPreview(normalized);
            _runtime.SetBoxTitleColor(AppearanceTargetBoxId, normalized);
        }
        catch (FormatException)
        {
            var current = AppearancePreviewBox?.Appearance.TitleColor ?? "Auto";
            TitleColorTextBox.Text = current;
            UpdateTitleColorPreview(current);
        }
    }

    private void UpdateTitleColorPreview(string value)
    {
        if (value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            TitleColorPreview.Background = new System.Windows.Media.SolidColorBrush(
                _runtime.IsDarkTheme
                    ? System.Windows.Media.Colors.White
                    : System.Windows.Media.Color.FromRgb(31, 35, 41));
            return;
        }
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            TitleColorPreview.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        catch (FormatException)
        {
            TitleColorPreview.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void IconSizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        IconSizeValueText.Text = $"{eventArgs.NewValue:0}px";
        if (!_updating)
        {
            _runtime.SetBoxIconSize(AppearanceTargetBoxId, eventArgs.NewValue);
        }
    }

    private void LabelFontSizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        LabelFontSizeValueText.Text = $"{eventArgs.NewValue:0.#}px";
        if (!_updating)
        {
            _runtime.SetBoxLabelFontSize(AppearanceTargetBoxId, eventArgs.NewValue);
        }
    }

    private void ShowItemLabelsCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetBoxShowItemLabels(AppearanceTargetBoxId, ShowItemLabelsCheckBox.IsChecked == true);
        }
    }

    private void ShowShortcutBadgesCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetBoxShowShortcutBadges(AppearanceTargetBoxId, ShowShortcutBadgesCheckBox.IsChecked == true);
        }
    }

    private void HoverFeedbackCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating)
        {
            _runtime.SetHoverFeedback(HoverFeedbackCheckBox.IsChecked == true);
        }
    }

    private void IconSpacingSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (!IsInitialized)
        {
            return;
        }
        HorizontalSpacingValueText.Text = $"{HorizontalSpacingSlider.Value:0}px";
        VerticalSpacingValueText.Text = $"{VerticalSpacingSlider.Value:0}px";
        if (!_updating)
        {
            _runtime.SetIconSpacing(HorizontalSpacingSlider.Value, VerticalSpacingSlider.Value);
        }
    }

    private void BoxViewModeRadioButton_OnChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && sender is System.Windows.Controls.RadioButton radioButton &&
            Enum.TryParse<BoxViewMode>(radioButton.Tag?.ToString(), out var mode))
        {
            _runtime.SetBoxViewMode(AppearanceTargetBoxId, mode);
        }
    }

    private void BoxSortModeRadioButton_OnChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updating && sender is System.Windows.Controls.RadioButton radioButton &&
            Enum.TryParse<BoxSortMode>(radioButton.Tag?.ToString(), out var mode))
        {
            _runtime.SetBoxSortMode(AppearanceTargetBoxId, mode);
        }
    }

    private void SelectionColorTextBox_OnLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating)
        {
            return;
        }

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                SelectionColorTextBox.Text.Trim());
            var normalized = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            SelectionColorTextBox.Text = normalized;
            UpdateSelectionColorPreview(normalized);
            _runtime.SetSelectionColor(normalized);
        }
        catch (FormatException)
        {
            SelectionColorTextBox.Text = _runtime.State.Settings.Appearance.SelectionColor;
            UpdateSelectionColorPreview(SelectionColorTextBox.Text);
        }
    }

    private void UpdateSelectionColorPreview(string value)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            SelectionColorPreview.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        catch (FormatException)
        {
            SelectionColorPreview.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void ResetAppearanceButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (MessageBox.Show(
            "恢复全部盒子和图标的默认外观？",
            "CrabDesk",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _runtime.ResetAppearance();
        RefreshAppearanceControls();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        Hide();
        _runtime.NotifyMinimizedToTray();
    }

}
