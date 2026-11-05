using CrabDesk.Core;
using CrabDesk.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private void Runtime_WidgetMonitorUpdated(object? sender, EventArgs args)
    {
        if (!IsLoaded) return;
        if (!_galleryIsCatalog)
            foreach (var item in _galleryItems.Where(item => DesktopWidgets.RequiresMonitoring(item.Widget.Kind)))
                item.Refresh(item.Appearance, item.Caption, item.ExistingCount, refreshPreview: true);
        UpdateMonitorStatus();
    }

    private void LoadMonitorSettings(DesktopWidget widget)
    {
        if (widget.Kind != DesktopWidgetKind.Network) return;
        NetworkDisplayModePicker.DisplayMemberPath = nameof(NetworkDisplayChoice.Label);
        // Only the offered styles are listed; the retired combined style still
        // exists so saved widgets load, but it must not be selectable.
        NetworkDisplayModePicker.ItemsSource = DesktopWidgets.OfferedNetworkDisplayModes
            .Select(mode => new NetworkDisplayChoice(mode, DesktopWidgets.NetworkDisplayName(mode))).ToArray();
        SyncNetworkDisplaySettings(widget);
        var choices = new List<NetworkAdapterChoice> { new("", "自动选择活动适配器") };
        choices.AddRange(WidgetNetworkMonitor.GetAdapters().Select(adapter => new NetworkAdapterChoice(adapter.Id, $"{adapter.Name} · {adapter.NetworkType}")));
        if (!choices.Any(choice => choice.Id == widget.NetworkAdapterId))
            choices.Add(new(widget.NetworkAdapterId, "已保存的适配器（当前不可用）"));
        NetworkAdapterPicker.DisplayMemberPath = nameof(NetworkAdapterChoice.Label);
        NetworkAdapterPicker.ItemsSource = choices;
        NetworkAdapterPicker.SelectedItem = choices.First(choice => choice.Id == widget.NetworkAdapterId);
        NetworkLatencyInput.Text = widget.NetworkLatencyTarget;
    }

    private void SyncSizeChoices(DesktopWidget widget)
    {
        var sizes = DesktopWidgets.SupportedSizes(widget);
        if (!SizePicker.Items.OfType<SizeChoice>().Select(choice => choice.Size).SequenceEqual(sizes))
            SizePicker.ItemsSource = sizes.Select(size => new SizeChoice(size, DesktopWidgets.SizeLabel(size))).ToArray();
        SizePicker.SelectedItem = SizePicker.Items.OfType<SizeChoice>().FirstOrDefault(choice => choice.Size == widget.Size);
    }

    private void SyncNetworkDisplaySettings(DesktopWidget widget)
    {
        if (widget.Kind != DesktopWidgetKind.Network) return;
        var choice = NetworkDisplayModePicker.Items.OfType<NetworkDisplayChoice>().FirstOrDefault(item => item.Mode == widget.NetworkDisplayMode);
        if (!ReferenceEquals(NetworkDisplayModePicker.SelectedItem, choice)) NetworkDisplayModePicker.SelectedItem = choice;
    }

    private void NetworkDisplayModePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Network } widget ||
            NetworkDisplayModePicker.SelectedItem is not NetworkDisplayChoice choice || choice.Mode == widget.NetworkDisplayMode) return;
        _saving = true;
        try { _runtime.SetNetworkDisplayMode(widget, choice.Mode); Notice.IsOpen = false; }
        catch (InvalidOperationException exception) { Error(exception.Message); }
        finally { _saving = false; }
        // Also restore the selection if the desktop cannot fit the requested style.
        _loading = true;
        try { SyncNetworkDisplaySettings(widget); SyncSizeChoices(widget); }
        finally { _loading = false; }
        RefreshGallery(changedWidget: widget);
    }

    private void UpdateMonitorStatus()
    {
        if (_selected is not { } widget) return;
        if (widget.Kind == DesktopWidgetKind.Performance)
        {
            PerformanceSummary.Text = widget.Performance is { } snapshot
                ? $"内存：{(snapshot.MemoryTotalBytes > 0 ? WidgetMonitorMetrics.Bytes(snapshot.MemoryUsedBytes) + " / " + WidgetMonitorMetrics.Bytes(snapshot.MemoryTotalBytes) : "不可用")}\n磁盘容量：{WidgetMonitorMetrics.Bytes(snapshot.DiskUsedBytes)} / {WidgetMonitorMetrics.Bytes(snapshot.DiskTotalBytes)}"
                : string.IsNullOrEmpty(widget.MonitorError) ? "正在等待系统采样…" : widget.MonitorError;
        }
        if (widget.Kind != DesktopWidgetKind.Network) return;
        NetworkSummary.Text = widget.Network is { } network
            ? $"{WidgetMonitorPresentation.ConnectionText(network)}\nIPv4：{Address(network.IPv4)}\nIPv6：{Address(network.IPv6)}\n延迟：{(network.LatencyMilliseconds is { } latency ? latency + " ms" : network.LatencyStatus)} · {Address(network.LatencyTarget)}"
            : string.IsNullOrEmpty(widget.MonitorError) ? "正在等待网络采样…" : widget.MonitorError;
        NetworkPublicIpStatus.Text = "公网 IP：" + (widget.IsPublicIpLoading ? "正在查询…" :
            !string.IsNullOrEmpty(widget.PublicIpError) ? widget.PublicIpError :
            !string.IsNullOrEmpty(widget.PublicIp) ? widget.PublicIp : "尚未查询");
        QueryPublicIpButton.IsEnabled = !widget.IsPublicIpLoading;
        QueryPublicIpButton.Content = string.IsNullOrEmpty(widget.PublicIp) ? "查询公网 IP" : "刷新公网 IP";
    }

    private static string Address(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void NetworkAdapterPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Network } widget || NetworkAdapterPicker.SelectedItem is not NetworkAdapterChoice choice) return;
        ConfigureNetwork(widget, choice.Id, widget.NetworkLatencyTarget);
    }

    private void NetworkLatencyInput_LostFocus(object sender, RoutedEventArgs args) => SaveLatencyTarget();
    private void NetworkLatencyInput_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        SaveLatencyTarget();
        args.Handled = true;
    }

    private void SaveLatencyTarget()
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Network } widget) return;
        var target = NetworkLatencyInput.Text.Trim();
        if (!WidgetMonitorMetrics.IsValidLatencyTarget(target)) { Error("请输入有效的 IP 地址或主机名，留空可使用默认网关。"); return; }
        ConfigureNetwork(widget, widget.NetworkAdapterId, target);
    }

    private void ConfigureNetwork(DesktopWidget widget, string adapterId, string target)
    {
        if (widget.NetworkAdapterId == adapterId && widget.NetworkLatencyTarget == target) return;
        _saving = true;
        try { _runtime.ConfigureNetworkWidget(widget, adapterId, target); Notice.IsOpen = false; }
        catch (InvalidOperationException exception) { Error(exception.Message); }
        finally { _saving = false; }
        RefreshGallery(changedWidget: widget);
        UpdateMonitorStatus();
    }

    private async void QueryPublicIp_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Network } widget || widget.IsPublicIpLoading) return;
        var work = _runtime.QueryWidgetPublicIpAsync(widget);
        UpdateMonitorStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateMonitorStatus();
    }

    private sealed record NetworkAdapterChoice(string Id, string Label);
    private sealed record NetworkDisplayChoice(NetworkWidgetDisplayMode Mode, string Label);
}
