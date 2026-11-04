using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private Forms.Timer? _widgetTimer;
    private string _lastWidgetClockTick = string.Empty;
    private Guid? _hoveredWidgetId;
    private Guid? _hoveredWidgetMenuId;
    private Guid? _hoveredWeatherLocationId;
    private RectangleF _hoveredCalendarButton;
    private DesktopWidget? _pressedWidget;
    private PointF _widgetPressPoint;
    private LayoutRect _widgetStartBounds;
    private bool _widgetDragging;
    private bool _widgetCanDrag;
    private readonly Dictionary<Guid, DateOnly> _widgetMonths = [];
    private readonly Dictionary<Guid, float> _widgetScroll = [];
    private IEnumerable<DesktopWidget> DesktopWidgetItems => _runtime.State.Widgets.Where(w =>
        string.Equals(w.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase));

    private void InitializeWidgets()
    {
        _calendarToolTipTimer.Tick += ShowCalendarToolTip;
        VisibleChanged += (_, _) => { if (!Visible) { DismissWidgetInputs(); HideCalendarHover(); } };
        _widgetTimer = new Forms.Timer { Interval = 1000 };
        _widgetTimer.Tick += (_, _) =>
        {
            if (_resourcesDisposed || _runtime.IsPaused || !Visible) return;
            var items = DesktopWidgetItems.ToArray();
            if (items.Length == 0) return;
            if (items.Any(widget => widget.Kind is DesktopWidgetKind.Performance or DesktopWidgetKind.Network))
                _ = _runtime.RefreshMonitorWidgetsAsync();
            var now = DateTime.Now;
            var tick = now.ToString(items.Any(w => w.Kind == DesktopWidgetKind.Clock && w.ShowSeconds) ? "yyyyMMddHHmmss" : "yyyyMMddHHmm");
            if (_lastWidgetClockTick != tick)
            {
                _lastWidgetClockTick = tick;
                if (items.Any(w => w.Kind is DesktopWidgetKind.Clock or DesktopWidgetKind.Calendar)) RequestVisualLayerRender();
                foreach (var widget in items.Where(w => w.Kind == DesktopWidgetKind.Weather))
                    _ = _runtime.RefreshWidgetWeatherAsync(widget, force: false);
            }
        };
        _widgetTimer.Start();
    }

    internal void RefreshMonitorVisuals()
    {
        if (!_resourcesDisposed && !IsDisposed && !_runtime.IsPaused &&
            DesktopWidgetItems.Any(widget => widget.Kind is DesktopWidgetKind.Performance or DesktopWidgetKind.Network)) RequestVisualLayerRender();
    }

    internal void RefreshWidgets()
    {
        if (IsDisposed || _resourcesDisposed) return;
        if (_runtime.IsPaused || _calendarToolTipWidgetId is { } hovered && !DesktopWidgetItems.Any(w => w.Id == hovered)) HideCalendarHover();
        RefreshWidgetTranslationInput();
        RefreshWidgetNoteInput();
        RefreshWidgetTodoInput();
        if (_pressedWidget is not null && !_runtime.State.Widgets.Contains(_pressedWidget)) FinishWidgetPress(false);
        var ids = _runtime.State.Widgets.Select(w => w.Id).ToHashSet();
        foreach (var id in _widgetScroll.Keys.Where(id => !ids.Contains(id)).ToArray()) _widgetScroll.Remove(id);
        foreach (var id in _translationSourceScroll.Keys.Where(id => !ids.Contains(id)).ToArray()) _translationSourceScroll.Remove(id);
        foreach (var id in _widgetMonths.Keys.Where(id => !ids.Contains(id)).ToArray()) _widgetMonths.Remove(id);
        _expandedTodoWidgets.RemoveWhere(id => !_runtime.State.Widgets.Any(widget => widget.Id == id && widget.Tasks.Any(task => task.IsCompleted)));
        UpdateWindowRegion();
        RequestLayerRender();
    }

    private DesktopWidget? WidgetAt(PointF point)
    {
        EnsureGeometry();
        if (_boxes.Any(box => box.Bounds.Contains(point))) return null;
        return DesktopWidgetItems.LastOrDefault(w => w.Bounds.Contains(point.X, point.Y));
    }

    private bool HandleWidgetMouseDown(Forms.MouseEventArgs args, PointF point)
    {
        HideCalendarHover();
        var widget = WidgetAt(point);
        if (widget is null) return false;
        ClearHoverState();
        if (args.Button == Forms.MouseButtons.Right) { ShowWidgetMenu(widget, args.Location); return true; }
        if (args.Button != Forms.MouseButtons.Left) return true;
        _widgetResizeEdges = WidgetSizing.HitTest(widget, point.X, point.Y);
        if (_widgetResizeEdges == WidgetResizeEdges.None && WidgetMenuRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point)) { ShowWidgetMenu(widget, args.Location); return true; }
        // Show the press immediately rather than waiting for the click to finish:
        // that delay is what made the card's buttons feel inert.
        if (widget.Kind == DesktopWidgetKind.ControlCenter) BeginControlCenterPress(widget, point);
        _pressedWidget = widget;
        _widgetPressPoint = point;
        _widgetStartBounds = widget.Bounds;
        _widgetDragging = false;
        _widgetResizing = false;
        _widgetCanDrag = _widgetResizeEdges == WidgetResizeEdges.None && !widget.IsLocked && !IsWeatherLocationPoint(widget, point) && !IsCalendarNavigationPoint(widget, point) &&
            !(widget.Kind == DesktopWidgetKind.Todo && GetTodoLayout(widget).Add.Contains(point)) &&
            !(widget.Kind == DesktopWidgetKind.Weather && WeatherLocationsButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point)) &&
            !(widget.Kind == DesktopWidgetKind.Weather && widget.Location is not null && WeatherCityButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point)) &&
            (widget.Kind == DesktopWidgetKind.Clock || widget.Kind == DesktopWidgetKind.Network && widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Speeds || point.Y < widget.Bounds.Y + 28 ||
             widget.Kind == DesktopWidgetKind.Weather &&
             (GetWeatherLayout(widget, _runtime.State.Settings.Appearance.CornerRadius).Temperature.Contains(point) || GetWeatherLayout(widget, _runtime.State.Settings.Appearance.CornerRadius).Icon.Contains(point))) &&
            // A slider must not drag the card: pressing one and moving adjusts the
            // level, and the pointer routinely leaves the control while dragging,
            // which used to start moving the whole widget instead.
            !(widget.Kind == DesktopWidgetKind.ControlCenter && IsControlCenterDragTarget(widget, point));
        Capture = true;
        return true;
    }

    private bool HandleWidgetMouseMove(PointF point)
    {
        if (_pressedWidget is { } pressed)
        {
            if (_widgetResizeEdges != WidgetResizeEdges.None)
            {
                UpdateWidgetResize(pressed, point);
                Cursor = WidgetResizeCursor(_widgetResizeEdges);
                return true;
            }
            // A held slider consumes the move: without this the press fell through
            // to the drag branch and moving the mouse slid the whole card, which
            // is why the sliders could be clicked but never dragged.
            if (pressed.Kind == DesktopWidgetKind.ControlCenter && UpdateControlCenterDrag(pressed, point))
            {
                Cursor = Forms.Cursors.Hand;
                return true;
            }
            if (_widgetCanDrag && (Math.Abs(point.X - _widgetPressPoint.X) > 4 || Math.Abs(point.Y - _widgetPressPoint.Y) > 4)) _widgetDragging = true;
            if (_widgetDragging)
            {
                var bounds = new LayoutRect(_widgetStartBounds.X + point.X - _widgetPressPoint.X,
                    _widgetStartBounds.Y + point.Y - _widgetPressPoint.Y, _widgetStartBounds.Width, _widgetStartBounds.Height);
                ApplyWidgetDragPosition(pressed, bounds, _monitor);
            }
            Cursor = _widgetDragging ? Forms.Cursors.SizeAll : Forms.Cursors.Default;
            return true;
        }
        var widget = WidgetAt(point);
        var menuId = widget is not null && WidgetMenuRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point) ? widget.Id : (Guid?)null;
        UpdateCalendarHover(widget, point);
        // Only the control centre has per-button hover state; other cards skip this.
        UpdateControlCenterHover(widget, point);
        var locationId = widget?.Kind == DesktopWidgetKind.Weather && WeatherLocationButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point) ? widget.Id : (Guid?)null;
        var calendarButton = RectangleF.Empty;
        var translationButton = RectangleF.Empty;
        if (widget?.Kind == DesktopWidgetKind.Translation)
        {
            var layout = GetTranslationLayout(widget);
            translationButton = new[] { layout.Clear, layout.Copy, layout.Translate }.FirstOrDefault(button => button.Contains(point));
        }
        Guid? todoTaskId = null, todoAddId = null, todoGroupId = null;
        if (widget?.Kind == DesktopWidgetKind.Todo)
        {
            var layout = GetTodoLayout(widget);
            if (layout.Add.Contains(point)) todoAddId = widget.Id;
            todoTaskId = TodoTaskAtPoint(widget, point);
            if (TodoCompletedHeaderRectangle(widget).Contains(point)) todoGroupId = widget.Id;
        }
        if (widget?.Kind == DesktopWidgetKind.Calendar)
        {
            var header = GetCalendarHeaderLayout(widget);
            calendarButton = new[] { header.Previous, header.Today, header.Next }.FirstOrDefault(button => button.Contains(point));
        }
        if (_hoveredWidgetId != widget?.Id || _hoveredWidgetMenuId != menuId || _hoveredWeatherLocationId != locationId ||
            _hoveredCalendarButton != calendarButton || _hoveredTranslationButton != translationButton ||
            _hoveredTodoTaskId != todoTaskId || _hoveredTodoAddId != todoAddId || _hoveredTodoGroupId != todoGroupId)
        {
            _hoveredWidgetId = widget?.Id;
            _hoveredWidgetMenuId = menuId;
            _hoveredWeatherLocationId = locationId;
            _hoveredCalendarButton = calendarButton;
            _hoveredTranslationButton = translationButton;
            _hoveredTodoTaskId = todoTaskId; _hoveredTodoAddId = todoAddId;
            _hoveredTodoGroupId = todoGroupId;
            RequestVisualLayerRender();
        }
        if (widget is null) return false;
        ClearHoverState();
        var resizeEdges = WidgetSizing.HitTest(widget, point.X, point.Y);
        if (resizeEdges != WidgetResizeEdges.None) { Cursor = WidgetResizeCursor(resizeEdges); return true; }
        if (widget.Kind == DesktopWidgetKind.Translation)
        {
            var layout = GetTranslationLayout(widget);
            if (layout.Source.Contains(point) || layout.Result.Contains(point) && !string.IsNullOrEmpty(widget.Translation))
            { Cursor = Forms.Cursors.IBeam; return true; }
        }
        Cursor = WidgetMenuRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point) || IsWeatherLocationPoint(widget, point) || IsCalendarNavigationPoint(widget, point) ||
            widget.Kind == DesktopWidgetKind.Network && NetworkPublicIpRectangle(widget).Contains(point) ||
            widget.Kind == DesktopWidgetKind.Todo && GetTodoLayout(widget).Add.Contains(point) || WidgetBodyRectangle(widget).Contains(point)
            || widget.Kind == DesktopWidgetKind.Weather && WeatherLocationsButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point)
            || widget.Kind == DesktopWidgetKind.Weather && widget.Location is not null && WeatherCityButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point)
            ? Forms.Cursors.Hand : !widget.IsLocked ? Forms.Cursors.SizeAll : Forms.Cursors.Default;
        return true;
    }

    private bool HandleWidgetMouseUp(Forms.MouseEventArgs args, PointF point)
    {
        if (_pressedWidget is not { } widget || args.Button != Forms.MouseButtons.Left) return false;
        EndControlCenterPress();
        if (_widgetResizeEdges != WidgetResizeEdges.None)
        {
            UpdateWidgetResize(widget, point);
            FinishWidgetPress(_widgetResizing);
            Cursor = Forms.Cursors.Default;
            return true;
        }
        var dragged = _widgetDragging;
        if (dragged)
        {
            var screen = PointToScreen(args.Location);
            var target = _runtime.Monitors.FirstOrDefault(m => m.PixelBounds.Contains(screen.X, screen.Y)) ?? _monitor;
            var scale = Math.Max(.1, target.DpiScale);
            var bounds = string.Equals(target.Id, _monitor.Id, StringComparison.OrdinalIgnoreCase)
                ? new LayoutRect(_widgetStartBounds.X + point.X - _widgetPressPoint.X,
                    _widgetStartBounds.Y + point.Y - _widgetPressPoint.Y, widget.Bounds.Width, widget.Bounds.Height)
                : new LayoutRect((screen.X - target.PixelBounds.X) / scale - (_widgetPressPoint.X - _widgetStartBounds.X) * _scale / scale,
                    (screen.Y - target.PixelBounds.Y) / scale - (_widgetPressPoint.Y - _widgetStartBounds.Y) * _scale / scale, widget.Bounds.Width, widget.Bounds.Height);
            ApplyWidgetDragPosition(widget, bounds, target);
        }
        FinishWidgetPress(true);
        if (!dragged && ReferenceEquals(WidgetAt(point), widget)) HandleWidgetClick(widget, point);
        return true;
    }

    private void FinishWidgetPress(bool save)
    {
        EndControlCenterPress();
        var widget = _pressedWidget;
        save &= _widgetResizeEdges == WidgetResizeEdges.None || _widgetResizing;
        ClearBoxAlignmentGuides();
        _pressedWidget = null;
        _widgetDragging = false;
        _widgetResizing = false;
        _widgetResizeEdges = WidgetResizeEdges.None;
        _widgetCanDrag = false;
        Capture = false;
        if (widget is not null)
        {
            DesktopWidgets.NormalizeForMonitors(_runtime.State, _runtime.Monitors);
            _runtime.RefreshWidgetPosition(widget, save);
        }
    }

    private void HandleWidgetClick(DesktopWidget widget, PointF point)
    {
        var bounds = WidgetRectangle(widget);
        if (IsWeatherLocationPoint(widget, point)) { LocateWeatherFromCard(widget); return; }
        if (widget.Kind == DesktopWidgetKind.Weather && WeatherLocationsButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point))
        { _runtime.OpenWidgetWeatherLocations(widget); return; }
        if (widget.Kind == DesktopWidgetKind.Weather && widget.Location is not null && WeatherCityButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point))
        { ShowWeatherCityMenu(widget); return; }
        if (widget.Kind == DesktopWidgetKind.ControlCenter && HandleControlCenterClick(widget, point)) return;
        if (widget.Kind == DesktopWidgetKind.Note && WidgetBodyRectangle(widget).Contains(point))
        { BeginWidgetNoteInput(widget); return; }
        if (widget.Kind == DesktopWidgetKind.Translation) { HandleTranslationWidgetClick(widget, point); return; }
        if (widget.Kind == DesktopWidgetKind.Weather && widget.Cities.Count > 1)
        {
            var layout = WeatherPresentation(widget, _runtime.State.Settings.Appearance.CornerRadius);
            var previous = WeatherRectangle(widget, layout.Previous);
            var next = WeatherRectangle(widget, layout.Next);
            if (previous.Contains(point) || next.Contains(point))
            {
                var index = widget.Cities.FindIndex(city => city.Id == widget.ActiveCityId);
                var step = previous.Contains(point) ? -1 : 1;
                var city = widget.Cities[(index + step + widget.Cities.Count) % widget.Cities.Count];
                _runtime.SelectWidgetCity(widget, city.Id);
                _ = _runtime.RefreshWidgetWeatherAsync(widget, force: false);
                return;
            }
        }
        if (widget.Kind == DesktopWidgetKind.Calendar)
        {
            var month = WidgetMonth(widget);
            var header = GetCalendarHeaderLayout(widget);
            if (header.Previous.Contains(point) && month > DateOnly.MinValue) month = month.AddMonths(-1);
            else if (header.Next.Contains(point) && month < new DateOnly(9998, 12, 1)) month = month.AddMonths(1);
            else if (header.Today.Contains(point)) month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
            else return;
            _widgetMonths[widget.Id] = month;
            HideCalendarHover();
            RequestVisualLayerRender();
            return;
        }
        if (widget.Kind == DesktopWidgetKind.Todo) { HandleTodoWidgetClick(widget, point); return; }
        if (widget.Kind == DesktopWidgetKind.Network && NetworkPublicIpRectangle(widget).Contains(point))
        { _ = _runtime.QueryWidgetPublicIpAsync(widget); return; }
        if (widget.Kind == DesktopWidgetKind.Weather && WidgetBodyRectangle(widget).Contains(point))
            _runtime.OpenWidgetWeatherDetails(widget);
    }

    private bool TryScrollWidget(PointF point, int delta)
    {
        var widget = WidgetAt(point);
        if (widget is null) return false;
        if (widget.Kind is DesktopWidgetKind.Note or DesktopWidgetKind.Translation or DesktopWidgetKind.Todo)
        {
            if (widget.Kind == DesktopWidgetKind.Translation && GetTranslationLayout(widget).Source.Contains(point))
            {
                _translationSourceScroll[widget.Id] = Math.Max(0, _translationSourceScroll.GetValueOrDefault(widget.Id) + (delta > 0 ? -36 : 36));
                RequestVisualLayerRender();
                return true;
            }
            var step = widget.Kind == DesktopWidgetKind.Todo ? 1 : 36;
            _widgetScroll[widget.Id] = Math.Max(0, _widgetScroll.GetValueOrDefault(widget.Id) + (delta > 0 ? -step : step));
            if (widget.Kind == DesktopWidgetKind.Todo)
            {
                DismissWidgetTodoInput();
                _widgetScroll[widget.Id] = WidgetTodoStart(widget);
                _hoveredTodoTaskId = TodoTaskAtPoint(widget, point);
                _hoveredTodoGroupId = TodoCompletedHeaderRectangle(widget).Contains(point) ? widget.Id : null;
            }
            RequestVisualLayerRender();
        }
        return true;
    }

    private void ShowWidgetMenu(DesktopWidget widget, Point location)
    {
        var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)_scale };
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Closed += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose); else menu.Dispose(); };
        menu.Items.Add("组件设置", null, (_, _) => _runtime.OpenWidgetEditor(widget.Id));
        menu.Items.Add(new FluentToolStripMenuItem("锁定位置") { Checked = widget.IsLocked });
        menu.Items[1].Click += (_, _) => { widget.IsLocked = !widget.IsLocked; _runtime.WidgetChanged(widget); };
        var sizes = new FluentToolStripMenuItem("尺寸");
        foreach (var size in DesktopWidgets.SupportedSizes(widget))
        {
            var item = new FluentToolStripMenuItem(DesktopWidgets.SizeLabel(size)) { Checked = widget.Size == size };
            item.Click += (_, _) => TryAction(() =>
            {
                _runtime.ResizeWidget(widget, size);
                if (size == DesktopWidgetSize.Custom) _runtime.OpenWidgetEditor(widget.Id);
            });
            sizes.DropDownItems.Add(item);
        }
        menu.Items.Add(sizes);
        if (widget.Kind is DesktopWidgetKind.Performance or DesktopWidgetKind.Network) AddMonitorContextMenu(menu, widget);

        if (widget.Kind == DesktopWidgetKind.Note)
            menu.Items.Add("编辑便签", null, (_, _) => BeginWidgetNoteInput(widget));
        if (widget.Kind == DesktopWidgetKind.Translation)
            menu.Items.Add("配置 AI 接口", null, (_, _) => _runtime.OpenWidgetEditor(widget.Id));
        if (widget.Kind == DesktopWidgetKind.Calendar) menu.Items.Add("回到本月", null, (_, _) =>
        { _widgetMonths.Remove(widget.Id); HideCalendarHover(); RequestVisualLayerRender(); });
        if (widget.Kind == DesktopWidgetKind.Weather)
        {
            menu.Items.Add("位置管理", null, (_, _) => _runtime.OpenWidgetWeatherLocations(widget));
            menu.Items.Add("使用当前位置", null, (_, _) => LocateWeatherFromCard(widget));
            menu.Items.Add("系统定位设置", null, (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }));
            menu.Items.Add("天气详情", null, (_, _) => _runtime.OpenWidgetWeatherDetails(widget));
            if (widget.Cities.Count > 1)
            {
                var cities = new FluentToolStripMenuItem("切换城市");
                foreach (var city in widget.Cities)
                {
                    var item = new FluentToolStripMenuItem(city.DisplayName) { Checked = city.Id == widget.ActiveCityId };
                    item.Click += async (_, _) => { _runtime.SelectWidgetCity(widget, city.Id); await _runtime.RefreshWidgetWeatherAsync(widget, force: false); };
                    cities.DropDownItems.Add(item);
                }
                menu.Items.Add(cities);
            }
            menu.Items.Add("刷新天气", null, async (_, _) => await _runtime.RefreshWidgetWeatherAsync(widget));
            menu.Items.Add(new FluentToolStripMenuItem("天气数据：" + (widget.Weather?.Provider ?? WidgetWeatherService.ProviderName)) { Enabled = false });
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("管理小组件", null, (_, _) => _runtime.OpenWidgetEditor());
        menu.Show(this, location);
    }
}
