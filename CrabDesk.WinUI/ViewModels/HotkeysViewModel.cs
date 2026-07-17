using CommunityToolkit.Mvvm.ComponentModel;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public sealed record OptionItem<T>(string Label, T Value);

public partial class HotkeysViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;

    public HotkeysViewModel(ICrabDeskService service)
    {
        _service = service;
        _service.Changed += (_, _) => OnPropertyChanged(string.Empty);
    }

    public IReadOnlyList<OptionItem<HotkeyModifiers>> ModifierOptions { get; } =
    [
        new("Ctrl + Alt", HotkeyModifiers.Control | HotkeyModifiers.Alt),
        new("Ctrl + Shift", HotkeyModifiers.Control | HotkeyModifiers.Shift),
        new("Alt + Shift", HotkeyModifiers.Alt | HotkeyModifiers.Shift),
        new("Win + Ctrl", HotkeyModifiers.Windows | HotkeyModifiers.Control)
    ];

    public IReadOnlyList<OptionItem<HotkeyKey>> KeyOptions { get; } =
        Enum.GetValues<HotkeyKey>().Select(key => new OptionItem<HotkeyKey>(key.ToString(), key)).ToArray();

    public bool ShowDesktopEnabled
    {
        get => _service.State.Settings.Hotkeys.ShowDesktop.Enabled;
        set { if (value != ShowDesktopEnabled) { UpdateShowDesktop(enabled: value); OnPropertyChanged(); } }
    }
    public OptionItem<HotkeyModifiers>? ShowDesktopModifiers
    {
        get => ModifierOptions.FirstOrDefault(item => item.Value == _service.State.Settings.Hotkeys.ShowDesktop.Modifiers);
        set { if (value is not null) { UpdateShowDesktop(modifiers: value.Value); OnPropertyChanged(); } }
    }
    public OptionItem<HotkeyKey>? ShowDesktopKey
    {
        get => KeyOptions.FirstOrDefault(item => item.Value == _service.State.Settings.Hotkeys.ShowDesktop.Key);
        set { if (value is not null) { UpdateShowDesktop(key: value.Value); OnPropertyChanged(); } }
    }
    public string ShowDesktopKeyText
    {
        get => _service.State.Settings.Hotkeys.ShowDesktop.Key.ToString();
        set
        {
            if (TryParseKey(value, out var key))
            {
                UpdateShowDesktop(key: key);
            }
            OnPropertyChanged();
        }
    }
    public string ShowDesktopStatus => FormatStatus(_service.GetHotkeyStatus(HotkeyAction.ShowDesktop));
    public bool ShowDesktopHasStatus => _service.GetHotkeyStatus(HotkeyAction.ShowDesktop) != HotkeyRegistrationStatus.Disabled;

    public bool OrganizeEnabled
    {
        get => _service.State.Settings.Hotkeys.OrganizeDesktop.Enabled;
        set { if (value != OrganizeEnabled) { UpdateOrganize(enabled: value); OnPropertyChanged(); } }
    }
    public OptionItem<HotkeyModifiers>? OrganizeModifiers
    {
        get => ModifierOptions.FirstOrDefault(item => item.Value == _service.State.Settings.Hotkeys.OrganizeDesktop.Modifiers);
        set { if (value is not null) { UpdateOrganize(modifiers: value.Value); OnPropertyChanged(); } }
    }
    public OptionItem<HotkeyKey>? OrganizeKey
    {
        get => KeyOptions.FirstOrDefault(item => item.Value == _service.State.Settings.Hotkeys.OrganizeDesktop.Key);
        set { if (value is not null) { UpdateOrganize(key: value.Value); OnPropertyChanged(); } }
    }
    public string OrganizeKeyText
    {
        get => _service.State.Settings.Hotkeys.OrganizeDesktop.Key.ToString();
        set
        {
            if (TryParseKey(value, out var key))
            {
                UpdateOrganize(key: key);
            }
            OnPropertyChanged();
        }
    }
    public string OrganizeStatus => FormatStatus(_service.GetHotkeyStatus(HotkeyAction.OrganizeDesktop));
    public bool OrganizeHasStatus => _service.GetHotkeyStatus(HotkeyAction.OrganizeDesktop) != HotkeyRegistrationStatus.Disabled;

    private void UpdateShowDesktop(bool? enabled = null, HotkeyModifiers? modifiers = null, HotkeyKey? key = null)
    {
        var binding = _service.State.Settings.Hotkeys.ShowDesktop;
        _service.SetHotkey(HotkeyAction.ShowDesktop, enabled ?? binding.Enabled, modifiers ?? binding.Modifiers, key ?? binding.Key);
    }

    private void UpdateOrganize(bool? enabled = null, HotkeyModifiers? modifiers = null, HotkeyKey? key = null)
    {
        var binding = _service.State.Settings.Hotkeys.OrganizeDesktop;
        _service.SetHotkey(HotkeyAction.OrganizeDesktop, enabled ?? binding.Enabled, modifiers ?? binding.Modifiers, key ?? binding.Key);
    }

    private static bool TryParseKey(string? value, out HotkeyKey key)
    {
        key = default;
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        var isLetter = normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z';
        var isFunctionKey = normalized.StartsWith('F') &&
                            int.TryParse(normalized.AsSpan(1), out var functionNumber) &&
                            functionNumber is >= 1 and <= 12;
        return (isLetter || isFunctionKey) && Enum.TryParse(normalized, out key);
    }

    private static string FormatStatus(HotkeyRegistrationStatus status) => status switch
    {
        HotkeyRegistrationStatus.Registered => "已注册",
        HotkeyRegistrationStatus.Conflict => "与其他应用冲突",
        HotkeyRegistrationStatus.Failed => "注册失败",
        _ => string.Empty
    };
}
