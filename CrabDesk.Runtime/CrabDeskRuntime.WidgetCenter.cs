using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    private WidgetCenterForm? _widgetCenter;
    private bool _widgetCenterVisible;
    private long _widgetCenterBlurHiddenAt;
    public bool IsWidgetCenterVisible => _widgetCenterVisible;
    public bool IsWidgetCenterDarkTheme { get; private set; }

    public void SetWidgetCenterThemeMode(ApplicationThemeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_disposed || State.Settings.WidgetCenter.ThemeMode == mode) return;
        State.Settings.WidgetCenter.ThemeMode = mode;
        ApplyWidgetCenterTheme();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    private void ApplyWidgetCenterTheme()
    {
        var dark = ApplicationTheme.ResolveIsDark(State.Settings.WidgetCenter.ThemeMode);
        if (IsWidgetCenterDarkTheme == dark) return;
        IsWidgetCenterDarkTheme = dark;
        _widgetCenter?.RefreshTheme();
    }

    internal bool IsWidgetActive(DesktopWidget widget) => !_disposed &&
        (widget.Placement == WidgetPlacement.Center
            ? State.Settings.WidgetCenter.Enabled && IsWidgetCenterVisible
            : !IsPaused);

    private bool HasActiveWidgets => State.Widgets.Any(IsWidgetActive);

    private void UpdateWidgetActivity()
    {
        if (!State.Widgets.Any(widget => widget.Kind == DesktopWidgetKind.Lyrics && IsWidgetActive(widget))) PauseLyrics();
        if (!State.Widgets.Any(widget => DesktopWidgets.RequiresMonitoring(widget.Kind) && IsWidgetActive(widget))) PauseMonitorSampling();
        else if (!State.Widgets.Any(widget => widget.Kind == DesktopWidgetKind.Network && IsWidgetActive(widget)))
            _networkMonitor?.StopProcessMonitoring();
    }

    public void SetWidgetCenterEnabled(bool enabled)
    {
        if (_disposed || State.Settings.WidgetCenter.Enabled == enabled) return;
        State.Settings.WidgetCenter.Enabled = enabled;
        if (!enabled) HideWidgetCenter();
        ApplyHotkeys();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    internal void OnTrayLeftClick()
    {
        if (State.Settings.WidgetCenter.Enabled)
        {
            var recentlyHidden = !IsWidgetCenterVisible && _widgetCenterBlurHiddenAt != 0 &&
                Environment.TickCount64 - _widgetCenterBlurHiddenAt < 400;
            _widgetCenterBlurHiddenAt = 0;
            if (!recentlyHidden) ToggleWidgetCenter();
        }
        else RequestShowSettings();
    }

    internal void HideWidgetCenterOnDeactivate()
    {
        if (!IsWidgetCenterVisible) return;
        _widgetCenterBlurHiddenAt = Environment.TickCount64;
        HideWidgetCenter();
    }

    public void ToggleWidgetCenter()
    {
        if (IsWidgetCenterVisible) HideWidgetCenter();
        else ShowWidgetCenter();
    }

    public void ShowWidgetCenter()
    {
        if (_disposed || !State.Settings.WidgetCenter.Enabled) return;
        if (_widgetCenter is null || _widgetCenter.IsDisposed)
        {
            var point = Forms.Cursor.Position;
            var monitor = Monitors.FirstOrDefault(m => m.PixelWorkArea.Contains(point.X, point.Y)) ??
                Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
            if (monitor is null) return;
            _widgetCenter = new WidgetCenterForm(this, monitor);
        }
        _widgetCenterVisible = true;
        try { _widgetCenter.Present(); }
        catch
        {
            _widgetCenterVisible = false;
            _widgetCenter.Dispose();
            _widgetCenter = null;
            throw;
        }
        _ = RefreshMonitorWidgetsAsync(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void HideWidgetCenter()
    {
        if (!IsWidgetCenterVisible) return;
        _widgetCenterVisible = false;
        _widgetCenter?.Dismiss();
        UpdateWidgetActivity();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DesktopWidget AddWidgetToCenter(DesktopWidgetKind kind)
    {
        if (_disposed || !State.Settings.WidgetCenter.Enabled)
            throw new InvalidOperationException("请先开启组件中心。");
        var widget = AddWidget(kind, null, WidgetPlacement.Center);
        MoveWidgetToCenter(widget);
        return widget;
    }

    public void MoveWidgetToCenter(DesktopWidget widget)
    {
        if (_disposed || !State.Settings.WidgetCenter.Enabled || !State.Widgets.Contains(widget) ||
            widget.Placement == WidgetPlacement.Center) return;
        DismissDesktopWidgetInput();
        widget.CenterOrder = State.Widgets.Count(w => w.Placement == WidgetPlacement.Center);
        widget.Placement = WidgetPlacement.Center;
        WidgetChanged(widget);
        if (IsWidgetCenterVisible) _ = RefreshMonitorWidgetsAsync(true);
    }

    public void MoveWidgetToDesktop(DesktopWidget widget)
    {
        if (_disposed || !State.Widgets.Contains(widget) || widget.Placement == WidgetPlacement.Desktop) return;
        DismissDesktopWidgetInput();
        var monitor = Monitors.FirstOrDefault(m => m.Id == widget.MonitorId) ??
            Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        if (monitor is null) throw new InvalidOperationException("没有可用的显示器。");
        widget.MonitorId = monitor.Id;
        widget.Bounds = widget.Bounds.Clamp(WidgetWorkArea(monitor), WidgetSizing.MinimumWidth, WidgetSizing.MinimumHeight);
        widget.Placement = WidgetPlacement.Desktop;
        WidgetChanged(widget);
        if (!IsPaused) _ = RefreshMonitorWidgetsAsync(true);
    }

    public void ReorderWidgetCenter(IReadOnlyList<Guid> order)
    {
        if (_disposed || !State.Settings.WidgetCenter.Enabled) return;
        var widgets = State.Widgets.Where(widget => widget.Placement == WidgetPlacement.Center)
            .OrderBy(widget => widget.CenterOrder).ThenBy(widget => widget.Id).ToArray();
        if (order.Count != widgets.Length || order.Distinct().Count() != order.Count ||
            !order.ToHashSet().SetEquals(widgets.Select(widget => widget.Id))) return;
        if (widgets.Select(widget => widget.Id).SequenceEqual(order)) return;
        var byId = widgets.ToDictionary(widget => widget.Id);
        for (var index = 0; index < order.Count; index++) byId[order[index]].CenterOrder = index;
        RefreshWidgetCenter();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SaveWidgetCenterPlacement(string monitorId, double x, double y, double width, double height)
    {
        if (_disposed) return;
        var settings = State.Settings.WidgetCenter;
        settings.MonitorId = monitorId;
        settings.X = x;
        settings.Y = y;
        settings.Width = width;
        settings.Height = height;
        settings.HasPosition = true;
        WidgetCenterSettings.Normalize(settings);
        ScheduleSave();
    }

    private void RefreshWidgetCenter() => _widgetCenter?.RefreshWidgets();

    private void DisposeWidgetCenter()
    {
        _widgetCenterVisible = false;
        _widgetCenter?.Dismiss();
        _widgetCenter?.Dispose();
        _widgetCenter = null;
    }
}
