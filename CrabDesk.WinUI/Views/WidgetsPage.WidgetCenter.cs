using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private readonly ICrabDeskService _widgetCenterService = App.GetService<ICrabDeskService>();
    private bool _loadingWidgetCenter;

    private void LoadWidgetCenterSettings()
    {
        _loadingWidgetCenter = true;
        try
        {
            var enabled = _widgetCenterService.State.Settings.WidgetCenter.Enabled;
            WidgetCenterEnabledToggle.IsOn = enabled;
            OpenWidgetCenterButton.IsEnabled = enabled;
            WidgetCenterThemePicker.SelectedItem = WidgetCenterThemePicker.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == _widgetCenterService.State.Settings.WidgetCenter.ThemeMode.ToString());
            var count = _widgetCenterService.State.Widgets.Count(widget => widget.Placement == WidgetPlacement.Center);
            WidgetCenterStatus.Text = enabled
                ? $"{count} 个组件在中心 · " + (_widgetCenterService.IsWidgetCenterVisible ? "已打开" : "未打开")
                : count > 0 ? $"已关闭 · 保留中心内的 {count} 个组件" : "默认关闭，开启后可选择移入已有组件。";
            if (_selected is { } widget) LoadWidgetPlacement(widget);
        }
        finally { _loadingWidgetCenter = false; }
    }

    private void WidgetCenterEnabled_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loadingWidgetCenter || !IsLoaded) return;
        FlushEditorChanges();
        _saving = true;
        try { Try(() => _widgetCenterService.SetWidgetCenterEnabled(WidgetCenterEnabledToggle.IsOn)); }
        finally { _saving = false; }
        LoadWidgetCenterSettings();
        RefreshGallery(refreshPreviews: false);
    }

    private void OpenWidgetCenter_Click(object sender, RoutedEventArgs args)
    {
        if (!_widgetCenterService.State.Settings.WidgetCenter.Enabled) return;
        Try(_widgetCenterService.ShowWidgetCenter);
        LoadWidgetCenterSettings();
    }

    private void WidgetCenterThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingWidgetCenter || !IsLoaded ||
            WidgetCenterThemePicker.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<ApplicationThemeMode>(tag, out var mode)) return;
        FlushEditorChanges();
        _saving = true;
        try { Try(() => _widgetCenterService.SetWidgetCenterThemeMode(mode)); }
        finally { _saving = false; }
        LoadWidgetCenterSettings();
        RefreshGallery();
    }

    private void LoadWidgetPlacement(DesktopWidget widget)
    {
        var wasLoading = _loadingWidgetCenter;
        _loadingWidgetCenter = true;
        try
        {
            var inCenter = widget.Placement == WidgetPlacement.Center;
            var enabled = _widgetCenterService.State.Settings.WidgetCenter.Enabled;
            WidgetPlacementPicker.SelectedIndex = inCenter ? 1 : 0;
            WidgetCenterPlacementOption.IsEnabled = enabled || inCenter;
            MonitorPicker.IsEnabled = !inCenter;
            MonitorPicker.SelectedItem = MonitorPicker.Items.OfType<MonitorChoice>().FirstOrDefault(monitor => monitor.Id == widget.MonitorId);
            WidgetCenterMaterialHint.Visibility = Show(inCenter);
            WidgetCenterLyricsMaterialHint.Visibility = Show(inCenter);
            LyricsTransparentBackground.Visibility = Show(!inCenter);
            LyricsInk.Visibility = Show(!inCenter);
            WidgetMaterialPicker.Visibility = Show(!inCenter);
            UpdateWidgetMaterialHint();
            WidgetPlacementHint.Text = inCenter
                ? (enabled ? "组件显示在中心，可随时移回桌面。" : "组件中心已关闭，归属保留；可移回桌面或重新开启中心。")
                    + "中心仅使用实色，原桌面材质设置保留，移回桌面后恢复。"
                : enabled ? "移入组件中心后仅使用实色，不再显示在桌面；组件内容与原桌面材质设置保持不变。" : "先在小组件页开启组件中心，再将此组件移入。";
        }
        finally { _loadingWidgetCenter = wasLoading; }
    }

    private void WidgetPlacementPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _loadingWidgetCenter || !IsLoaded || _selected is not { } widget ||
            WidgetPlacementPicker.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<WidgetPlacement>(tag, out var placement)) return;
        FlushEditorChanges();
        _saving = true;
        try { Try(() => ApplyWidgetPlacement(_widgetCenterService, widget, placement)); }
        finally { _saving = false; }
        LoadWidgetCenterSettings();
        RefreshGallery(changedWidget: widget);
    }

    internal static void ApplyWidgetPlacement(ICrabDeskService service, DesktopWidget widget, WidgetPlacement placement)
    {
        if (!service.State.Widgets.Contains(widget) || widget.Placement == placement) return;
        switch (placement)
        {
            case WidgetPlacement.Center:
                if (!service.State.Settings.WidgetCenter.Enabled)
                    throw new InvalidOperationException("请先开启组件中心。");
                service.MoveWidgetToCenter(widget);
                break;
            case WidgetPlacement.Desktop:
                service.MoveWidgetToDesktop(widget);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(placement));
        }
    }
}
