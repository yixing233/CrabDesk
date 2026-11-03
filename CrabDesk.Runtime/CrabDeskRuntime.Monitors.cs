using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    private WidgetPerformanceMonitor? _performanceMonitor;
    private WidgetNetworkMonitor? _networkMonitor;
    private CancellationTokenSource? _monitorLifetime;
    private CancellationTokenSource? _monitorPingCancellation;
    private Task? _monitorPingTask;
    private long _monitorLastTick, _monitorLastPing;
    private int _monitorSampling;
    private string _monitorPingKey = "";
    private Dictionary<Guid, CancellationTokenSource>? _publicIpRequests;
    private Func<CancellationToken, Task<string>>? _publicIpQuery = WidgetNetworkMonitor.QueryPublicIpAsync;
    public event EventHandler? WidgetMonitorUpdated;

    public async Task RefreshMonitorWidgetsAsync(bool force = false)
    {
        if (_disposed || IsPaused) return;
        var performance = State.Widgets.FirstOrDefault(widget => widget.Kind == DesktopWidgetKind.Performance);
        var network = State.Widgets.FirstOrDefault(widget => widget.Kind == DesktopWidgetKind.Network);
        if (performance is null && network is null) return;
        var now = Stopwatch.GetTimestamp();
        if (!force && _monitorLastTick != 0 && Stopwatch.GetElapsedTime(_monitorLastTick, now).TotalMilliseconds < 900) return;
        if (Interlocked.CompareExchange(ref _monitorSampling, 1, 0) != 0) return;
        var gap = _monitorLastTick == 0 ? 0 : Stopwatch.GetElapsedTime(_monitorLastTick, now).TotalSeconds;
        _monitorLastTick = now;
        _monitorLifetime ??= new();
        var token = _monitorLifetime.Token;
        var adapter = network?.NetworkAdapterId ?? ""; var target = network?.NetworkLatencyTarget ?? "";
        if (performance is not null) _performanceMonitor ??= new();
        if (network is not null) _networkMonitor ??= new();
        var performanceSource = _performanceMonitor; var networkSource = _networkMonitor;
        try
        {
            var data = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                WidgetPerformanceSnapshot? machine = null; WidgetNetworkSnapshot? connection = null;
                var machineError = ""; var networkError = "";
                try { if (performance is not null) machine = performanceSource!.Sample(); }
                catch (Exception exception) { machineError = "性能采样失败：" + exception.Message; }
                try { if (network is not null) connection = networkSource!.Sample(adapter, target); }
                catch (Exception exception) { networkError = "网络采样失败：" + exception.Message; }
                return (machine, connection, machineError, networkError);
            }, token);
            if (network is not null && networkSource is not null && !token.IsCancellationRequested && !IsPaused &&
                State.Widgets.Contains(network) && network.NetworkAdapterId == adapter && network.NetworkLatencyTarget == target)
            {
                var key = adapter + "\n" + target;
                if ((_monitorPingTask is null || _monitorPingTask.IsCompleted) &&
                    (key != _monitorPingKey || _monitorLastPing == 0 || Stopwatch.GetElapsedTime(_monitorLastPing).TotalSeconds >= 5))
                {
                    _monitorPingCancellation?.Dispose();
                    _monitorPingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    _monitorPingKey = key; _monitorLastPing = now;
                    var pingToken = _monitorPingCancellation.Token;
                    _monitorPingTask = Task.Run(() => networkSource.UpdateLatencyAsync(adapter, target, pingToken), pingToken);
                }
            }
            if (!token.IsCancellationRequested)
                _beginInvoke(() =>
                {
                    if (_disposed || IsPaused) return;
                    if (performance is not null && State.Widgets.Contains(performance))
                    { performance.Performance = gap > 5 ? null : data.machine; performance.MonitorError = data.machineError; }
                    if (network is not null && State.Widgets.Contains(network) && network.NetworkAdapterId == adapter && network.NetworkLatencyTarget == target)
                    {
                        if (network.Network?.InterfaceId != data.connection?.InterfaceId || data.connection?.IsConnected != true)
                        { CancelPublicIpRequest(network.Id); network.PublicIp = ""; network.PublicIpError = ""; network.IsPublicIpLoading = false; }
                        WidgetNetworkGraph.Append(network, data.connection);
                        network.Network = data.connection; network.MonitorError = data.networkError;
                    }
                    NotifyMonitorFrame();
                });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { Interlocked.Exchange(ref _monitorSampling, 0); }
    }

    private void NotifyMonitorFrame()
    {
        if (_disposed) return;
        _surfaceManager?.RefreshMonitorVisuals();
        WidgetMonitorUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void SetNetworkDisplayMode(DesktopWidget widget, NetworkWidgetDisplayMode mode)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Network || !State.Widgets.Contains(widget) ||
            !DesktopWidgets.OfferedNetworkDisplayModes.Contains(mode) || widget.NetworkDisplayMode == mode) return;
        var monitor = Monitors.FirstOrDefault(m => m.Id == widget.MonitorId);
        if (monitor is null) return;
        var size = mode == NetworkWidgetDisplayMode.Speeds
            ? (widget.Size is DesktopWidgetSize.Small or DesktopWidgetSize.Wide ? widget.Size : DesktopWidgetSize.Small)
            : DesktopWidgetSize.Large;
        var (width, height) = DesktopWidgets.Dimensions(size);
        var area = WidgetWorkArea(monitor);
        if (area.Width < width || area.Height < height) throw new InvalidOperationException("当前显示器工作区无法容纳此样式。");
        widget.NetworkDisplayMode = mode; widget.Size = size;
        widget.Bounds = new LayoutRect(widget.Bounds.X, widget.Bounds.Y, width, height).Clamp(area, width, height);
        WidgetChanged(widget);
    }

    public void ConfigureNetworkWidget(DesktopWidget widget, string adapterId, string latencyTarget)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Network || !State.Widgets.Contains(widget)) return;
        adapterId = adapterId.Trim(); latencyTarget = latencyTarget.Trim();
        if (!WidgetMonitorMetrics.IsValidLatencyTarget(latencyTarget)) throw new ArgumentException("请输入 IP 地址或主机名，留空可测量网关延迟。");
        if (widget.NetworkAdapterId == adapterId && widget.NetworkLatencyTarget == latencyTarget) return;
        if (widget.NetworkAdapterId != adapterId)
        { CancelPublicIpRequest(widget.Id); widget.PublicIp = ""; widget.PublicIpError = ""; widget.IsPublicIpLoading = false; widget.NetworkHistory.Clear(); }
        widget.NetworkAdapterId = adapterId; widget.NetworkLatencyTarget = latencyTarget; widget.Network = null;
        _monitorPingCancellation?.Cancel(); _monitorLastPing = 0; _monitorLastTick = 0;
        WidgetChanged(widget);
        _ = RefreshMonitorWidgetsAsync(true);
    }

    public async Task QueryWidgetPublicIpAsync(DesktopWidget widget)
    {
        if (_disposed || IsPaused || widget.Kind != DesktopWidgetKind.Network || !State.Widgets.Contains(widget) || widget.IsPublicIpLoading) return;
        if (widget.Network is not { IsConnected: true } current)
        { widget.PublicIpError = "当前未连接网络"; NotifyMonitorFrame(); return; }
        _monitorLifetime ??= new(); _publicIpRequests ??= new();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_monitorLifetime.Token);
        _publicIpRequests[widget.Id] = request;
        var adapter = widget.NetworkAdapterId; var interfaceId = current.InterfaceId;
        widget.IsPublicIpLoading = true; widget.PublicIpError = ""; NotifyMonitorFrame();
        try
        {
            var address = await (_publicIpQuery ?? WidgetNetworkMonitor.QueryPublicIpAsync)(request.Token);
            _beginInvoke(() =>
            {
                if (!_disposed && !IsPaused && !request.IsCancellationRequested && State.Widgets.Contains(widget) &&
                    widget.NetworkAdapterId == adapter && widget.Network is { IsConnected: true } active && active.InterfaceId == interfaceId) widget.PublicIp = address;
            });
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed && !request.IsCancellationRequested) widget.PublicIpError = "查询失败，点击重试"; }
        finally
        {
            if (_publicIpRequests.TryGetValue(widget.Id, out var active) && active == request)
            { _publicIpRequests.Remove(widget.Id); widget.IsPublicIpLoading = false; }
            if (!_disposed) _beginInvoke(NotifyMonitorFrame);
        }
    }

    private void CancelPublicIpRequest(Guid id)
    {
        if (_publicIpRequests?.Remove(id, out var source) == true) source.Cancel();
    }

    private void PauseMonitorSampling()
    {
        _monitorPingCancellation?.Cancel();
        if (_publicIpRequests is not null) foreach (var source in _publicIpRequests.Values.ToArray()) source.Cancel();
        foreach (var widget in State.Widgets) widget.IsPublicIpLoading = false;
    }

    private void DisposeMonitoring()
    {
        PauseMonitorSampling(); _monitorLifetime?.Cancel();
        var performance = _performanceMonitor; var network = _networkMonitor;
        _performanceMonitor = null; _networkMonitor = null;
        _ = Task.Run(() => { performance?.Dispose(); network?.Dispose(); });
    }
}
