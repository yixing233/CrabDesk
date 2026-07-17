using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;

    [ObservableProperty] private string _maintenanceStatus = string.Empty;
    [ObservableProperty] private bool _isUpdateBusy;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _isUpdateProgressIndeterminate;
    [ObservableProperty] private string _updateActivity = string.Empty;

    public AboutViewModel(ICrabDeskService service, IDialogService dialogs, IClipboardService clipboard)
    {
        _service = service;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _service.Changed += OnServiceChanged;
    }

    public string VersionText => $"版本 {_service.CurrentVersion}";
    public string ConfigDirectory => _service.ConfigDirectory;
    public bool CheckUpdatesOnStartup
    {
        get => _service.State.Settings.Updates.CheckOnStartup;
        set { if (value != CheckUpdatesOnStartup) { _service.SetCheckUpdatesOnStartup(value); OnPropertyChanged(); } }
    }
    public UpdateChannel UpdateChannel
    {
        get => _service.State.Settings.Updates.Channel;
        set { if (value != UpdateChannel) { _service.SetUpdateChannel(value); OnPropertyChanged(); } }
    }
    public IReadOnlyList<UpdateChannel> UpdateChannels { get; } = [UpdateChannel.Stable, UpdateChannel.Preview];
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
    public bool CanCheckForUpdates => !IsUpdateBusy && !_service.IsCheckingForUpdates;
    public bool CanDownloadUpdate => !IsUpdateBusy &&
        _service.LastUpdateCheck.Status == UpdateCheckStatus.UpdateAvailable &&
        !string.IsNullOrWhiteSpace(_service.LastUpdateCheck.InstallerUrl) &&
        !string.IsNullOrWhiteSpace(_service.LastUpdateCheck.Sha256Url);

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        await _service.CheckForUpdatesAsync();
        RefreshUpdateState();
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
        UpdateActivity = "正在准备下载";
        RefreshUpdateState();
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                UpdateActivity = value.Stage;
                IsUpdateProgressIndeterminate = value.TotalBytes is not > 0;
                UpdateProgress = value.Percentage;
                OnPropertyChanged(nameof(UpdateStatus));
            });
            var result = await _service.DownloadUpdateAsync(progress);
            if (!result.Success)
            {
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
            _service.LaunchUpdateInstaller(result.InstallerPath);
        }
        catch (Exception exception)
        {
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
        MaintenanceStatus = $"已清理 {count} 项缓存";
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        _clipboard.SetText(_service.GetDesktopHostDiagnosticsText());
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
        MaintenanceStatus = $"布局已重置，备份：{Path.GetFileName(result.Backup.Path)}";
    }

    private void OnServiceChanged(object? sender, EventArgs eventArgs) => RefreshUpdateState();

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
