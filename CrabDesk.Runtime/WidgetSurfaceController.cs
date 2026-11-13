using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>Real component rendering and interaction, shared by desktop and centre hosts.</summary>
internal abstract partial class WidgetSurfaceController : Forms.Form, IWidgetHostContext
{
    protected readonly CrabDeskRuntime _runtime;
    protected MonitorLayout _monitor;
    protected double _scale;
    protected bool _resourcesDisposed;
    private bool _widgetResourcesDisposed;
    private int _interactionDepth;
    private readonly Dictionary<string, Bitmap?> _widgetFileIcons = new(StringComparer.OrdinalIgnoreCase);
    [ThreadStatic] private static IWidgetHostContext? _currentWidgetHost;

    protected WidgetSurfaceController(CrabDeskRuntime runtime, MonitorLayout monitor)
    {
        _runtime = runtime;
        _monitor = monitor;
        _scale = Math.Max(.1, monitor.DpiScale);
        InitializeWidgets();
    }

    protected virtual IReadOnlyList<DesktopWidget> HostWidgets => _runtime.State.Widgets.Where(widget =>
        widget.Placement == WidgetPlacement.Desktop && string.Equals(widget.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
    protected virtual LayoutRect ViewBounds(DesktopWidget widget) => widget.Bounds;
    protected virtual bool CanTransformWidgets => true;
    protected virtual bool ForceSolidAppearance => false;
    protected virtual WidgetColorTheme? SurfaceColorTheme => null;
    protected static bool IsWidgetSolid(DesktopWidget widget) => _currentWidgetHost is WidgetSurfaceController { ForceSolidAppearance: true } || widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid;
    protected static bool WidgetHasBackground(DesktopWidget widget) => _currentWidgetHost is WidgetSurfaceController { ForceSolidAppearance: true } || DesktopWidgets.HasBackground(widget);
    protected bool HostHasActiveWidgets => HostWidgets.Any(_runtime.IsWidgetActive);
    protected bool OwnsActiveWidget(DesktopWidget widget) => HostWidgets.Contains(widget) && _runtime.IsWidgetActive(widget);
    protected bool HasProtectedInteraction => _interactionDepth > 0 || _fileShelfItemDragging || Capture;
    protected virtual void RequestVisualLayerRender() => Invalidate();
    protected virtual void RequestLayerRender() => Invalidate();
    protected virtual bool UpdateWindowRegion() => true;
    protected virtual bool IsWidgetOccluded(PointF point) => false;
    protected virtual void ClearHoverState() { }
    protected virtual void ClearDropPreview() { }
    protected virtual void ClearBoxAlignmentGuides() { }
    protected virtual void UpdateMovingWidget(DesktopWidget widget, PointF point) { }
    protected virtual void UpdateWidgetResize(DesktopWidget widget, PointF point) { }
    protected virtual bool TryPresentLyricsFrame() => false;
    protected virtual bool WidgetUsesAcrylic(DesktopWidget widget) => false;
    protected virtual Task<bool> ConfirmDesktopAsync(string title, string message, string primaryText) =>
        Task.FromResult(DesktopConfirmationDialog.Show(this, _runtime.IsDarkTheme, title, message, primaryText));
    protected int ToPixel(double value) => (int)Math.Round(value * _scale, MidpointRounding.AwayFromZero);
    protected virtual PointF ToDip(Point point) => new((float)(point.X / _scale), (float)(point.Y / _scale));
    protected Point WidgetPointToScreen(PointF point) => ((IWidgetHostContext)this).ViewToScreen(point);
    protected void ShowWidgetMenuAt(Forms.ToolStripDropDown menu, PointF point) => menu.Show(this, PointToClient(WidgetPointToScreen(point)));

    IReadOnlyList<DesktopWidget> IWidgetHostContext.Widgets => HostWidgets;
    LayoutRect IWidgetHostContext.GetViewBounds(DesktopWidget widget) => ViewBounds(widget);
    double IWidgetHostContext.DpiScale => _scale;
    MonitorLayout IWidgetHostContext.Monitor => _monitor;
    Forms.Form IWidgetHostContext.Owner => this;
    protected virtual Point ViewToScreen(PointF point) => PointToScreen(new(ToPixel(point.X), ToPixel(point.Y)));
    Point IWidgetHostContext.ViewToScreen(PointF point) => ViewToScreen(point);
    bool IWidgetHostContext.OwnsWidget(DesktopWidget widget) => HostWidgets.Contains(widget);
    void IWidgetHostContext.RequestRedraw() => RequestVisualLayerRender();

    // Existing static presentation APIs continue to use desktop Bounds unless an
    // actual host enters a view scope. The model is never cloned or rewritten.
    protected static LayoutRect GetWidgetBounds(DesktopWidget widget) => _currentWidgetHost?.GetViewBounds(widget) ?? widget.Bounds;
    internal IDisposable EnterWidgetView() => new ViewScope(this);
    private sealed class ViewScope : IDisposable
    {
        private readonly IWidgetHostContext? _previous;
        internal ViewScope(IWidgetHostContext host) { _previous = _currentWidgetHost; _currentWidgetHost = host; }
        public void Dispose() => _currentWidgetHost = _previous;
    }

    protected IDisposable ProtectWidgetInteraction()
    {
        _interactionDepth++;
        return new InteractionScope(this);
    }
    private sealed class InteractionScope(WidgetSurfaceController host) : IDisposable
    {
        private WidgetSurfaceController? _host = host;
        public void Dispose() { if (_host is { } value) { value._interactionDepth--; _host = null; } }
    }

    protected async Task PickWidgetFilesAsync(DesktopWidget widget)
    {
        using var interaction = ProtectWidgetInteraction();
        await _runtime.PickFileShelfFilesAsync(widget);
    }

    protected virtual Bitmap? GetWidgetFileIcon(DesktopItemRef item, float iconSize)
    {
        var path = item.FileSystemPath;
        if (path is null) return null;
        if (_widgetFileIcons.TryGetValue(path, out var cached)) return cached;
        // No Explorer host is required. Keep the real file's associated icon;
        // inaccessible files use the existing coloured generic asset.
        try { using var icon = Icon.ExtractAssociatedIcon(path); return _widgetFileIcons[path] = icon?.ToBitmap(); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return _widgetFileIcons[path] = null; }
    }

    protected void TryAction(Action action)
    {
        using var interaction = ProtectWidgetInteraction();
        try { action(); }
        catch (Exception error) { DesktopConfirmationDialog.ShowMessage(this, _runtime.IsDarkTheme, "CrabDesk", error.Message, DesktopDialogKind.Error); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_widgetResourcesDisposed)
        {
            _widgetResourcesDisposed = true;
            DismissWidgetInputs();
            _resourcesDisposed = true;
            DisposeLyricsVisuals();
            _controlPopup?.Dispose(); _microphonePopup?.Dispose(); _controlMixerPopup?.Dispose();
            _controlDeviceMenu?.Dispose(); _todoDeleteMenu?.Dispose(); _controlArtwork?.Dispose();
            _widgetTimer?.Dispose(); _calendarToolTipBitmap?.Dispose();
            _calendarToolTipTimer.Dispose(); _calendarToolTipMotion.Dispose();
            _translationInput?.Dispose(); _translationResultInput?.Dispose(); _noteInput?.Dispose(); _todoInput?.Dispose();
            foreach (var visual in _translationTextVisuals.Values) visual.Dispose();
            foreach (var visual in _noteTextVisuals.Values) visual.Dispose();
            foreach (var visual in _todoTextVisuals.Values) visual.Dispose();
            foreach (var icon in _widgetFileIcons.Values) icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
