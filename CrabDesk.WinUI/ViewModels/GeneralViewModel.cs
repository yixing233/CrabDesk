using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.ViewModels;

public partial class GeneralViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogs;
    private readonly IInfoBarService? _notifications;

    [ObservableProperty] private bool _isRepairingDesktopIcons;
    [ObservableProperty] private string _desktopIconRepairStatus = string.Empty;
    [ObservableProperty] private InfoBarSeverity _desktopIconRepairSeverity = InfoBarSeverity.Informational;

    public GeneralViewModel(
        ICrabDeskService service,
        IThemeService themeService,
        IDialogService dialogs,
        IInfoBarService? notifications = null)
    {
        _service = service;
        _themeService = themeService;
        _dialogs = dialogs;
        _notifications = notifications;
        _service.Changed += OnServiceChanged;
    }

    public string ConnectionStatus => _service.DesktopConnected ? "桌面已连接" : "桌面未连接";
    public string PauseButtonText => _service.IsPaused ? "恢复接管" : "暂停接管";
    public bool IsConnected => _service.DesktopConnected;
    public bool CanRepairDesktopIcons => !IsRepairingDesktopIcons;
    public bool HasDesktopIconRepairStatus => !string.IsNullOrWhiteSpace(DesktopIconRepairStatus);

    public bool StartWithWindows
    {
        get => _service.State.Settings.StartWithWindows;
        set
        {
            if (value == StartWithWindows) return;
            _service.SetStartWithWindows(value);
            OnPropertyChanged();
        }
    }

    public bool ConfirmDeleteBox
    {
        get => _service.State.Settings.ConfirmDeleteBox;
        set { if (value != ConfirmDeleteBox) { _service.SetConfirmDeleteBox(value); OnPropertyChanged(); } }
    }

    public bool LaunchToTray
    {
        get => _service.State.Settings.DesktopBehavior.LaunchToTray;
        set { if (value != LaunchToTray) { _service.SetLaunchToTray(value); OnPropertyChanged(); } }
    }

    public bool ShowSystemItems
    {
        get => _service.State.Settings.ShowSystemItems;
        set
        {
            if (value == ShowSystemItems) return;
            _ = _service.SetShowSystemItemsAsync(value);
            OnPropertyChanged();
        }
    }


    public bool RefreshAfterRename
    {
        get => _service.State.Settings.DesktopBehavior.RefreshAfterRename;
        set { if (value != RefreshAfterRename) { _service.SetRefreshAfterRename(value); OnPropertyChanged(); } }
    }

    public bool AnimationEnabled
    {
        get => _service.State.Settings.Appearance.AnimationEnabled;
        set { if (value != AnimationEnabled) { _service.SetAnimationEnabled(value); OnPropertyChanged(); } }
    }

    public ApplicationThemeMode ThemeMode
    {
        get => _service.State.Settings.ThemeMode;
        set
        {
            if (value == ThemeMode) return;
            _service.SetThemeMode(value);
            _themeService.Apply(value);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ApplicationThemeMode> ThemeModes { get; } =
        [ApplicationThemeMode.System, ApplicationThemeMode.Light, ApplicationThemeMode.Dark];

    [RelayCommand]
    private void TogglePause() => _service.SetPaused(!_service.IsPaused);

    [RelayCommand]
    private async Task ReconnectAsync() => await _service.ReconnectDesktopAsync();

    [RelayCommand(CanExecute = nameof(CanRepairDesktopIcons))]
    private async Task RepairDesktopIconsAsync()
    {
        if (IsRepairingDesktopIcons || !await _dialogs.ConfirmAsync(
                "修复桌面图标",
                "CrabDesk 会暂时撤下接管层，重启当前桌面 Shell 的 Explorer 进程并等待桌面恢复，然后重新接管。任务栏和桌面会短暂闪动；不会启动额外的文件管理器窗口。",
                "重启并修复"))
        {
            return;
        }

        IsRepairingDesktopIcons = true;
        DesktopIconRepairSeverity = InfoBarSeverity.Informational;
        _notifications?.Show("正在修复桌面图标", DesktopIconRepairSeverity);
        DesktopIconRepairStatus = "正在重启 Explorer 桌面 Shell 并重建接管层…";
        RepairDesktopIconsCommand.NotifyCanExecuteChanged();
        try
        {
            var repaired = await _service.RepairDesktopIconsAsync();
            DesktopIconRepairSeverity = repaired ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            DesktopIconRepairStatus = repaired
                ? "桌面图标已修复"
                : "Explorer 桌面没有在等待时间内恢复；未启动额外的文件管理器窗口，可稍后再次修复";
        }
        catch (Exception exception)
        {
            DesktopIconRepairSeverity = InfoBarSeverity.Error;
            _notifications?.Show(exception.Message, DesktopIconRepairSeverity);
            DesktopIconRepairStatus = $"修复失败：{exception.Message}";
        }
        finally
        {
            _notifications?.Show(DesktopIconRepairStatus, DesktopIconRepairSeverity);
            IsRepairingDesktopIcons = false;
            OnPropertyChanged(nameof(CanRepairDesktopIcons));
            RepairDesktopIconsCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnDesktopIconRepairStatusChanged(string value) =>
        OnPropertyChanged(nameof(HasDesktopIconRepairStatus));

    private void OnServiceChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(string.Empty);
    }
}
