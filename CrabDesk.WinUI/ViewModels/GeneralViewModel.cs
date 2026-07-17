using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public partial class GeneralViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IThemeService _themeService;

    public GeneralViewModel(ICrabDeskService service, IThemeService themeService)
    {
        _service = service;
        _themeService = themeService;
        _service.Changed += OnServiceChanged;
    }

    public string ConnectionStatus => _service.DesktopConnected ? "桌面已连接" : "桌面未连接";
    public string PauseButtonText => _service.IsPaused ? "恢复接管" : "暂停接管";
    public bool IsConnected => _service.DesktopConnected;

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

    public bool ToggleIconsOnDoubleClick
    {
        get => _service.State.Settings.DesktopBehavior.ToggleIconsOnDesktopDoubleClick;
        set { if (value != ToggleIconsOnDoubleClick) { _service.SetToggleIconsOnDesktopDoubleClick(value); OnPropertyChanged(); } }
    }

    public bool ExpandBoxOnHover
    {
        get => _service.State.Settings.DesktopBehavior.ExpandBoxOnHover;
        set { if (value != ExpandBoxOnHover) { _service.SetExpandBoxOnHover(value); OnPropertyChanged(); } }
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

    private void OnServiceChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(string.Empty);
    }
}
