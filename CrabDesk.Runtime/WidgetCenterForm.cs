using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>An activatable top-level host, independent of Explorer/desktop surfaces.</summary>
internal sealed partial class WidgetCenterForm : WidgetSurfaceController
{
    private const double HeaderHeight = 64;
    private const double PaddingDip = 16;
    private const double ScrollGutterDip = 8;
    private const double PanelWidthDip = 180 * 2 + 16 + PaddingDip * 2 + ScrollGutterDip;
    private Dictionary<Guid, LayoutRect> _viewBounds = [];
    private readonly WidgetCenterChrome _chrome = new();
    private readonly WidgetCenterPicker _picker;
    private bool _addHovered, _scrollHovered;
    private bool _addPressed, _scrollDragging;
    private double _scrollDragStart;
    private float _scrollDragY;
    private int _headerFocus = -1;
    private readonly Forms.Timer _outsideTimer = new() { Interval = 220 };
    private readonly Forms.Timer _placementTimer = new() { Interval = 250 };
    private readonly DesktopAnimationFrameClock _panelMotion = new();
    private WidgetCenterComposition? _panelComposition;
    private Bitmap? _panelFrame;
    private readonly Windows.UI.ViewManagement.UISettings _panelUiSettings = new();
    private bool CanUsePanelAcrylic => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        !System.Windows.SystemParameters.HighContrast && _panelUiSettings.AdvancedEffectsEnabled;
    private IReadOnlyList<DesktopWidget> _hostWidgets = [];
    private double _scrollOffset;
    private double _contentHeight;
    private bool _ready;
    private bool _presenting;
    private bool _acceptingDrop;
    private bool _pickerVisible;

    internal WidgetCenterForm(CrabDeskRuntime runtime, MonitorLayout monitor) : base(runtime, monitor)
    {
        Text = "CrabDesk 组件中心";
        AccessibleName = "组件中心";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        DoubleBuffered = true;
        AllowDrop = true;
        KeyPreview = true;
        TopMost = true;
        SetStyle(Forms.ControlStyles.ResizeRedraw, true);
        _picker = new WidgetCenterPicker(runtime, _scale) { Visible = false };
        _picker.Selected += () => { _pickerVisible = false; _picker.Hide(); _scrollOffset = 0; RefreshWidgets(); };
        Controls.Add(_picker);
        _outsideTimer.Tick += (_, _) => CheckOutsideActivation();
        _panelMotion.Frame += AdvancePanelMotion;
        _placementTimer.Tick += (_, _) => { _placementTimer.Stop(); SavePlacement(); };
        _ready = true;
        RestorePlacement();
        RefreshWidgets();
    }

    protected override bool CanTransformWidgets => false;
    protected override WidgetColorTheme? SurfaceColorTheme =>
        _runtime.IsWidgetCenterDarkTheme ? WidgetColorTheme.Dark : WidgetColorTheme.Light;
    protected override IReadOnlyList<DesktopWidget> HostWidgets => _hostWidgets;
    protected override LayoutRect ViewBounds(DesktopWidget widget) => _viewBounds.TryGetValue(widget.Id, out var bounds) ? bounds : default;
    protected override Point ViewToScreen(PointF point) => PointToScreen(new(ToPixel(point.X), ToPixel(point.Y - _scrollOffset)));
    protected override PointF ToDip(Point point) => new((float)(point.X / _scale), (float)(point.Y / _scale + _scrollOffset));
    protected override bool IsWidgetOccluded(PointF point) => _panelMotion.Enabled || _pickerVisible || point.Y < HeaderHeight + _scrollOffset;
    internal IReadOnlyDictionary<Guid, LayoutRect> WidgetViewBounds => _viewBounds;
    internal double ScrollOffset => _scrollOffset;

    internal void Present()
    {
        if (IsDisposed || Disposing) return;
        if (Visible)
        {
            if (_panelMotion.Enabled && !_panelShowing)
                StartPanelMotion(showing: true, _runtime.State.Settings.Appearance.AnimationEnabled &&
                    System.Windows.SystemParameters.ClientAreaAnimation);
            BringToFront();
            Activate();
            return;
        }
        _presenting = true;
        try
        {
            StopPanelMotion();
            RestorePlacement();
            RefreshWidgets();
            _panelTarget = Location;
            var animate = _runtime.State.Settings.Appearance.AnimationEnabled && System.Windows.SystemParameters.ClientAreaAnimation;
            _ = Handle;
            ApplyCenterMaterial();
            FreezePanelFrame();
            MovePanel(animate ? Width : 0);
            Show();
            BringToFront();
            Activate();
            StartPanelMotion(showing: true, animate);
        }
        finally { _presenting = false; }
    }

    internal void Dismiss()
    {
        if (IsDisposed || Disposing) return;
        _outsideTimer.Stop(); _placementTimer.Stop(); _widgetTimer?.Stop();
        FinishWidgetReorder(false);
        DismissWidgetInputs();
        FinishWidgetPress(false);
        HideCalendarHover();
        _acceptingDrop = false;
        _addPressed = _scrollDragging = false;
        Capture = false;
        if (!Visible) { StopPanelMotion(); _pickerVisible = false; _picker.Hide(); return; }
        if (!_panelMotion.Enabled) { _panelTarget = Location; SavePlacement(); }
        FreezePanelFrame();
        _pickerVisible = false; _picker.Hide();
        var animate = !_resourcesDisposed && _runtime.State.Settings.WidgetCenter.Enabled &&
            _runtime.State.Settings.Appearance.AnimationEnabled && System.Windows.SystemParameters.ClientAreaAnimation;
        StartPanelMotion(showing: false, animate);
        // Enabled is configuration, not window visibility. Only runtime can change it.
    }

    internal new void RefreshWidgets()
    {
        if (!_ready || IsDisposed || _resourcesDisposed) return;
        var widgets = _runtime.State.Widgets.Where(widget => widget.Placement == WidgetPlacement.Center)
            .OrderBy(widget => widget.CenterOrder).ThenBy(widget => widget.Id).ToArray();
        if (_reorderWidget is not null && !_reorderOriginalOrder.ToHashSet().SetEquals(widgets.Select(widget => widget.Id)))
            FinishWidgetReorder(false);
        var available = Math.Max(1, ClientSize.Width / _scale - PaddingDip * 2 - ScrollGutterDip);
        var layout = WidgetCenterLayout.Arrange(widgets, available, gripHeight: ReorderGripHeight, order: _reorderPreviewOrder);
        var bounds = layout.Bounds.ToDictionary(pair => pair.Key,
            pair => pair.Value with { X = pair.Value.X + PaddingDip, Y = pair.Value.Y + HeaderHeight + PaddingDip });
        var changed = bounds.Count != _viewBounds.Count || bounds.Any(pair => !_viewBounds.TryGetValue(pair.Key, out var old) || old != pair.Value);
        _hostWidgets = widgets;
        _viewBounds = bounds;
        _contentHeight = layout.Height + PaddingDip * 2;
        if (changed) { DismissWidgetInputs(); HideCalendarHover(); }
        LayoutChrome();
        base.RefreshWidgets();
        if (_pickerVisible) _picker.Invalidate();
    }

    internal void RefreshTheme()
    {
        if (!_ready || IsDisposed || _resourcesDisposed) return;
        DismissWidgetInputs();
        HideCalendarHover();
        LayoutChrome();
        ApplyCenterMaterial();
        Invalidate(true);
    }

    private void TogglePicker()
    {
        FinishWidgetReorder(false);
        DismissWidgetInputs();
        _pickerVisible = !_pickerVisible;
        _picker.Visible = _pickerVisible;
        LayoutChrome();
        if (_pickerVisible) _picker.Focus();
        Invalidate();
    }

    private void LayoutChrome()
    {
        if (!_ready) return;
        var background = _runtime.IsWidgetCenterDarkTheme ? Color.FromArgb(31, 34, 40) : Color.FromArgb(248, 249, 252);
        MinimumSize = new(ToPixel(320), ToPixel(240));
        _picker.UpdateScale(_scale);
        BackColor = background;
        ForeColor = ResolveAutoTextColor(background);
        _picker.BackColor = BackColor; _picker.ForeColor = ForeColor;
        var viewport = Math.Max(1, ClientSize.Height / _scale - HeaderHeight);
        if (_pickerVisible)
        {
            _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, ScrollContentHeight - viewport));
            _picker.SetBounds(ToPixel(PaddingDip), ToPixel(HeaderHeight - _scrollOffset),
                ClientSize.Width - ToPixel(PaddingDip * 2 + ScrollGutterDip), _picker.ContentHeight);
            var region = new Region(new Rectangle(0, Math.Max(0, ToPixel(_scrollOffset)), _picker.Width, Math.Max(1, ClientSize.Height - ToPixel(HeaderHeight))));
            var old = _picker.Region; _picker.Region = region; old?.Dispose();
            _picker.BringToFront();
        }
        else _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _contentHeight - viewport));
    }

    private double ScrollContentHeight => _pickerVisible ? _picker.ContentHeight / _scale : _contentHeight;

    internal void SetScrollOffset(double offset)
    {
        if (!_ready) return;
        var height = _pickerVisible ? _picker.ContentHeight / _scale : _contentHeight;
        var next = Math.Clamp(offset, 0, Math.Max(0, height - (ClientSize.Height / _scale - HeaderHeight)));
        if (Math.Abs(next - _scrollOffset) < .01) return;
        DismissWidgetInputs(); HideCalendarHover();
        _scrollOffset = next;
        LayoutChrome(); Invalidate();
    }

    protected override void OnPaintBackground(Forms.PaintEventArgs e)
    {
        if (_panelComposition is null && !_panelFrameFrozen) base.OnPaintBackground(e);
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        if (_panelFrameFrozen && _panelFrame is not null)
        {
            if (_panelComposition is null) e.Graphics.DrawImageUnscaled(_panelFrame, Point.Empty);
            return;
        }
        if (_panelComposition is null)
        {
            DrawPanel(e.Graphics, acrylic: false);
            return;
        }
        RenderPanelFrame(includePicker: false);
    }

    private void FreezePanelFrame()
    {
        if (_panelFrameFrozen) return;
        _panelFrameFrozen = true;
        RenderPanelFrame(includePicker: true);
        _lyricsAnimationTimer?.Stop();
    }

    private void RenderPanelFrame(bool includePicker)
    {
        if (_panelFrame is null || _panelFrame.Size != ClientSize)
        {
            _panelFrame?.Dispose();
            _panelFrame = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
        using var graphics = Graphics.FromImage(_panelFrame);
        graphics.Clear(Color.Transparent);
        var panelState = graphics.Save();
        DrawPanel(graphics, _panelComposition is not null && CanUsePanelAcrylic);
        graphics.Restore(panelState);
        if (includePicker && _pickerVisible)
        {
            var state = graphics.Save();
            graphics.SetClip(new Rectangle(0, ToPixel(HeaderHeight), ClientSize.Width, Math.Max(1, ClientSize.Height - ToPixel(HeaderHeight))));
            graphics.TranslateTransform(_picker.Left, _picker.Top);
            _picker.Draw(graphics);
            graphics.Restore(state);
        }
        _panelComposition?.Present(_panelFrame, _scale);
    }

    private void DrawPanel(Graphics graphics, bool acrylic)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var state = graphics.Save();
        graphics.ScaleTransform((float)_scale, (float)_scale);
        var width = (float)(ClientSize.Width / _scale);
        var height = (float)(ClientSize.Height / _scale);
        using var panel = new SolidBrush(acrylic ? Color.FromArgb(150, BackColor) : BackColor);
        using var outline = RoundedRectangle(new(.5f, .5f, width - 1, height - 1), 12);
        graphics.FillPath(panel, outline);
        if (!_pickerVisible)
        {
            graphics.SetClip(new RectangleF(0, (float)HeaderHeight, width, Math.Max(1, height - (float)HeaderHeight)));
            graphics.TranslateTransform(0, -(float)_scrollOffset);
            foreach (var widget in _hostWidgets.Where(widget => !_reorderDragging || widget != _reorderWidget))
                if (new RectangleF((float)ViewBounds(widget).X, (float)ViewBounds(widget).Y,
                    (float)ViewBounds(widget).Width, (float)ViewBounds(widget).Height).IntersectsWith(
                    new RectangleF(0, (float)(HeaderHeight + _scrollOffset), width, height)))
                    DrawWidget(graphics, widget);
            DrawWidgetReorder(graphics);
            DrawCalendarToolTip(graphics);
            if (_hostWidgets.Count == 0)
            {
                WidgetColorIcons.Draw(graphics, "control-center", new(width / 2 - 28, (float)(HeaderHeight + _scrollOffset + 68), 56, 56));
                _chrome.DrawText(graphics, "把常用组件放在这里", new(24, (float)(HeaderHeight + _scrollOffset + 142), width - 48, 32), ForeColor, 18, semibold: true, centered: true);
                _chrome.DrawText(graphics, "点击“添加组件”，添加新组件或移入已有组件", new(24, (float)(HeaderHeight + _scrollOffset + 180), width - 48, 28), Color.FromArgb(180, ForeColor), 12, centered: true);
            }
        }
        graphics.Restore(state);
        graphics.ScaleTransform((float)_scale, (float)_scale);
        _chrome.DrawHeader(graphics, width, ForeColor, _pickerVisible, _addHovered, _addPressed, _headerFocus == 0);
        _chrome.DrawScroll(graphics, WidgetCenterChrome.ScrollTrack(width, height), height - HeaderHeight,
            ScrollContentHeight, _scrollOffset, ForeColor, _scrollHovered, _scrollDragging);
        using var divider = new Pen(Color.FromArgb(24, ForeColor));
        graphics.DrawLine(divider, 16, (float)HeaderHeight - 1, width - 16, (float)HeaderHeight - 1);
        graphics.DrawPath(divider, outline);
    }

    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_panelMotion.Enabled || HandleChromeMouseDown(e)) return;
        if (e.Y < ToPixel(HeaderHeight) || _pickerVisible) return;
        if (BeginWidgetReorder(e, ToDip(e.Location))) return;
        CaptureControlPopupToggle(e);
        HandleWidgetMouseDown(e, ToDip(e.Location));
    }
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panelMotion.Enabled) return;
        if (MoveWidgetReorder(e, ToDip(e.Location))) return;
        if (HandleChromeMouseMove(e)) return;
        if (!_pickerVisible && !_panelMotion.Enabled && e.Y >= ToPixel(HeaderHeight)) HandleWidgetMouseMove(ToDip(e.Location));
    }
    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_reorderWidget is not null)
        {
            FinishWidgetReorder(e.Button == Forms.MouseButtons.Left && ClientRectangle.Contains(e.Location));
            return;
        }
        if (HandleChromeMouseUp(e)) return;
        if (!_pickerVisible && !_panelMotion.Enabled) HandleWidgetMouseUp(e, ToDip(e.Location));
    }
    protected override void OnMouseDoubleClick(Forms.MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var widget = WidgetAt(ToDip(e.Location));
        if (widget?.Kind == DesktopWidgetKind.FileShelf && FileShelfItemAt(widget, ToDip(e.Location)) is { } item)
            _ = OpenFileShelfItemAsync(widget, item, false);
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _addHovered = _scrollHovered = false;
        if (_pressedWidget is null) { _hoveredWidgetId = null; HideCalendarHover(); Invalidate(); }
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            FinishWidgetReorder(false);
            _addPressed = _scrollDragging = false;
            if (_pressedWidget is not null) FinishWidgetPress(false);
            Invalidate();
        }
    }
    protected override void OnMouseWheel(Forms.MouseEventArgs e)
    {
        if (_panelMotion.Enabled || _reorderWidget is not null || e.Delta == 0) return;
        // Read scrollable card content naturally; Ctrl+wheel always scrolls the host.
        var point = ToDip(e.Location);
        if (!_pickerVisible && (Forms.Control.ModifierKeys & Forms.Keys.Control) == 0 &&
            TryScrollCenterWidget(point, e.Delta)) return;
        SetScrollOffset(_scrollOffset + (e.Delta > 0 ? -60 : 60));
    }
    private bool TryScrollCenterWidget(PointF point, int delta)
    {
        using var widgetView = EnterWidgetView();
        var widget = WidgetAt(point);
        if (widget?.Kind != DesktopWidgetKind.Note)
            return widget?.Kind is DesktopWidgetKind.Todo or DesktopWidgetKind.FileShelf or DesktopWidgetKind.Translation &&
                TryScrollWidget(point, delta);
        var field = NoteFieldRectangle(widget);
        if (!field.Contains(point) || !_noteTextVisuals.TryGetValue(widget.Id, out var visual)) return false;
        var maximum = Math.Max(0, visual.Height - Math.Max(1, field.Height - 12));
        var current = Math.Clamp(_widgetScroll.GetValueOrDefault(widget.Id), 0, maximum);
        var next = Math.Clamp(current + (delta > 0 ? -36 : 36), 0, maximum);
        if (Math.Abs(next - current) < .01) return false;
        _widgetScroll[widget.Id] = (float)next;
        Invalidate();
        return true;
    }
    protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData)
    {
        if (keyData == Forms.Keys.Escape)
        {
            if (_reorderWidget is not null) FinishWidgetReorder(false);
            else _runtime.HideWidgetCenter();
            return true;
        }
        if (_panelMotion.Enabled) return true;
        if (HandleChromeKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnFormClosing(Forms.FormClosingEventArgs e)
    {
        if (e.CloseReason == Forms.CloseReason.UserClosing) { e.Cancel = true; _runtime.HideWidgetCenter(); }
        base.OnFormClosing(e);
    }
    protected override void OnDeactivate(EventArgs e)
    { base.OnDeactivate(e); if (Visible && !_presenting) _outsideTimer.Start(); }
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); _outsideTimer.Stop(); }
    protected override void OnClientSizeChanged(EventArgs e)
    { base.OnClientSizeChanged(e); if (_ready) { RefreshWidgets(); QueuePlacementSave(); } }
    protected override void OnLocationChanged(EventArgs e)
    { base.OnLocationChanged(e); if (_ready && !_panelFrameFrozen) { DismissWidgetInputs(); QueuePlacementSave(); } }
    protected override void OnDpiChanged(Forms.DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96d;
        _monitor = _runtime.Monitors.FirstOrDefault(m => m.PixelBounds.Contains(Left, Top)) ?? _monitor;
        if (_ready) { RefreshWidgets(); ApplyCenterMaterial(); }
    }
    protected override void OnDragEnter(Forms.DragEventArgs e) { base.OnDragEnter(e); OnDragOver(e); }
    protected override void OnDragOver(Forms.DragEventArgs e)
    {
        base.OnDragOver(e); _acceptingDrop = true;
        if (_panelMotion.Enabled || _pickerVisible) { e.Effect = Forms.DragDropEffects.None; return; }
        TryHandleFileShelfDragOver(e, ToDip(PointToClient(new(e.X, e.Y))));
    }
    protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); _acceptingDrop = false; SetFileShelfDropTarget(null); }
    protected override async void OnDragDrop(Forms.DragEventArgs e)
    {
        base.OnDragDrop(e);
        using var interaction = ProtectWidgetInteraction();
        try { if (!_pickerVisible) await TryHandleFileShelfDropAsync(e, ToDip(PointToClient(new(e.X, e.Y)))); }
        finally { _acceptingDrop = false; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reorderScrollTimer.Dispose(); _reorderGhost?.Dispose();
            _panelMotion.Dispose(); _outsideTimer.Dispose(); _placementTimer.Dispose();
            _chrome.Dispose(); _panelComposition?.Dispose(); _panelFrame?.Dispose();
        }
        base.Dispose(disposing);
    }
}
