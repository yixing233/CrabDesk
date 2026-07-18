using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IInfoBarService? _notifications;

    [ObservableProperty] private string _maintenanceStatus = string.Empty;
    [ObservableProperty] private bool _isUpdateBusy;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _isUpdateProgressIndeterminate;
    [ObservableProperty] private string _updateActivity = string.Empty;
    [ObservableProperty] private string _updateProgressText = string.Empty;
    [ObservableProperty] private InfoBarSeverity _updateInfoSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private InfoBarSeverity _maintenanceInfoSeverity = InfoBarSeverity.Success;

    public AboutViewModel(
        ICrabDeskService service,
        IDialogService dialogs,
        IClipboardService clipboard,
        IInfoBarService? notifications = null)
    {
        _service = service;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _notifications = notifications;
        _service.Changed += OnServiceChanged;
        UpdateInfoSeverity = ResolveUpdateSeverity(_service.LastUpdateCheck.Status);
    }

    public string VersionText => $"版本 {_service.CurrentVersion}";
    public string ConfigDirectory => _service.ConfigDirectory;
    public bool CheckUpdatesOnStartup
    {
        get => _service.State.Settings.Updates.CheckOnStartup;
        set { if (value != CheckUpdatesOnStartup) { _service.SetCheckUpdatesOnStartup(value); OnPropertyChanged(); } }
    }
    public string UpdateStatus
    {
        get
        {
            if (IsUpdateBusy && !string.IsNullOrWhiteSpace(UpdateActivity))
            {
                return UpdateActivity;
            }
            var result = _service.LastUpdateCheck;
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                return result.Message;
            }
            return result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable => $"发现新版本 {result.LatestVersion}",
                UpdateCheckStatus.UpToDate => $"当前已是最新版本 {result.CurrentVersion}",
                UpdateCheckStatus.NotConfigured => "尚未配置更新仓库",
                UpdateCheckStatus.Offline => "当前网络不可用",
                UpdateCheckStatus.RateLimited => "GitHub 请求频率已达上限",
                UpdateCheckStatus.Failed => "检查更新失败",
                _ => "尚未检查更新"
            };
        }
    }
    public bool CanOpenRelease => !string.IsNullOrWhiteSpace(_service.LastUpdateCheck.ReleasePageUrl);
    public bool HasMaintenanceStatus => !string.IsNullOrWhiteSpace(MaintenanceStatus);
    public bool CanCheckForUpdates => !IsUpdateBusy && !_service.IsCheckingForUpdates;
    public bool CanDownloadUpdate => !IsUpdateBusy &&
        _service.LastUpdateCheck.Status == UpdateCheckStatus.UpdateAvailable &&
        !string.IsNullOrWhiteSpace(_service.LastUpdateCheck.InstallerUrl) &&
        !string.IsNullOrWhiteSpace(_service.LastUpdateCheck.Sha256Url);

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        await _service.CheckForUpdatesAsync();
        UpdateInfoSeverity = ResolveUpdateSeverity(_service.LastUpdateCheck.Status);
        RefreshUpdateState();
        _notifications?.Show(UpdateStatus, UpdateInfoSeverity);
    }

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadAndInstallUpdateAsync()
    {
        var update = _service.LastUpdateCheck;
        var confirmed = await _dialogs.ConfirmAsync(
            "下载并安装更新",
            $"将下载 CrabDesk {update.LatestVersion}。校验完成后 CrabDesk 会正常退出并打开安装程序。",
            "下载并安装");
        if (!confirmed)
        {
            return;
        }

        IsUpdateBusy = true;
        IsUpdateProgressIndeterminate = true;
        UpdateProgress = 0;
        UpdateProgressText = string.Empty;
        UpdateInfoSeverity = InfoBarSeverity.Informational;
        _notifications?.Show("正在准备下载", UpdateInfoSeverity);
        UpdateActivity = "正在准备下载";
        RefreshUpdateState();
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                UpdateActivity = value.Stage;
                IsUpdateProgressIndeterminate = value.TotalBytes is not > 0;
                UpdateProgress = value.Percentage;
                UpdateProgressText = FormatDownloadProgress(value);
                OnPropertyChanged(nameof(UpdateStatus));
            });
            var result = await _service.DownloadUpdateAsync(progress);
            if (!result.Success)
            {
                UpdateActivity = result.Message;
                UpdateInfoSeverity = InfoBarSeverity.Error;
                _notifications?.Show(result.Message, UpdateInfoSeverity);
                await _dialogs.ShowMessageAsync("更新失败", result.Message);
                return;
            }
            if (result.IsPrerelease && !result.SignatureTrusted)
            {
                var installPreview = await _dialogs.ConfirmAsync(
                    "未签名的预览版本",
                    "该预览版已通过 SHA-256 校验，但 Authenticode 签名不受信任。仅在你明确需要测试版时继续。",
                    "仍然安装");
                if (!installPreview)
                {
                    return;
                }
            }

            UpdateActivity = result.SignatureTrusted
                ? $"验证完成：{result.SignerSubject}"
                : "预览版哈希验证完成";
            UpdateProgress = 100;
            UpdateProgressText = "100%";
            UpdateInfoSeverity = InfoBarSeverity.Success;
            _notifications?.Show(UpdateActivity, UpdateInfoSeverity);
            _service.LaunchUpdateInstaller(result.InstallerPath);
        }
        catch (Exception exception)
        {
            UpdateActivity = exception.Message;
            UpdateInfoSeverity = InfoBarSeverity.Error;
            _notifications?.Show(exception.Message, UpdateInfoSeverity);
            await _dialogs.ShowMessageAsync("更新失败", exception.Message);
        }
        finally
        {
            IsUpdateBusy = false;
            IsUpdateProgressIndeterminate = false;
            RefreshUpdateState();
        }
    }

    [RelayCommand] private void OpenRelease() => _service.OpenLatestReleasePage();
    [RelayCommand] private void OpenConfigDirectory() => _service.OpenConfigDirectory();

    [RelayCommand]
    private void ClearCache()
    {
        var count = _service.ClearThumbnailCache();
        MaintenanceInfoSeverity = InfoBarSeverity.Success;
        _notifications?.Show($"已清理 {count} 项缓存", MaintenanceInfoSeverity);
        MaintenanceStatus = $"已清理 {count} 项缓存";
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        _clipboard.SetText(_service.GetDesktopHostDiagnosticsText());
        MaintenanceInfoSeverity = InfoBarSeverity.Success;
        _notifications?.Show("诊断信息已复制", MaintenanceInfoSeverity);
        MaintenanceStatus = "诊断信息已复制";
    }

    [RelayCommand]
    private async Task ResetLayoutAsync()
    {
        if (!await _dialogs.ConfirmAsync("重置桌面布局", "当前布局会先自动备份，然后恢复默认盒子。", "重置布局"))
        {
            return;
        }
        var result = await _service.ResetLayoutAsync();
        MaintenanceInfoSeverity = InfoBarSeverity.Success;
        _notifications?.Show("布局已重置", MaintenanceInfoSeverity);
        MaintenanceStatus = $"布局已重置，备份：{Path.GetFileName(result.Backup.Path)}";
    }

    partial void OnMaintenanceStatusChanged(string value) =>
        OnPropertyChanged(nameof(HasMaintenanceStatus));

    private void OnServiceChanged(object? sender, EventArgs eventArgs)
    {
        if (!IsUpdateBusy)
        {
            UpdateInfoSeverity = ResolveUpdateSeverity(_service.LastUpdateCheck.Status);
        }
        RefreshUpdateState();
    }

    private static InfoBarSeverity ResolveUpdateSeverity(UpdateCheckStatus status) => status switch
    {
        UpdateCheckStatus.UpToDate => InfoBarSeverity.Success,
        UpdateCheckStatus.UpdateAvailable => InfoBarSeverity.Warning,
        UpdateCheckStatus.Offline or UpdateCheckStatus.RateLimited or UpdateCheckStatus.Failed => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    private static string FormatDownloadProgress(UpdateDownloadProgress progress)
    {
        if (progress.TotalBytes is not > 0)
        {
            return progress.Stage;
        }
        var receivedMiB = progress.BytesReceived / 1024d / 1024d;
        var totalMiB = progress.TotalBytes.Value / 1024d / 1024d;
        return $"{progress.Percentage:0}%  ·  {receivedMiB:0.0}/{totalMiB:0.0} MiB";
    }

    private void RefreshUpdateState()
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(CanOpenRelease));
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        DownloadAndInstallUpdateCommand.NotifyCanExecuteChanged();
    }
}
