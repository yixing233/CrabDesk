using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private bool _usesAcrylicBackground;
    private Action<Bitmap, Point, IReadOnlyList<AcrylicBoxRegion>>? _acrylicFramePresenter;
    private Action<Bitmap, Rectangle>? _acrylicPartialFramePresenter;
    private Action? _acrylicEmptyFramePresenter;
    private Bitmap? _acrylicFrameBitmap;

    internal void SetAcrylicFramePresenter(Action<Bitmap, Point, IReadOnlyList<AcrylicBoxRegion>> presenter) =>
        _acrylicFramePresenter = presenter;

    internal void SetAcrylicPartialFramePresenter(Action<Bitmap, Rectangle> presenter) =>
        _acrylicPartialFramePresenter = presenter;

    internal void SetAcrylicEmptyFramePresenter(Action presenter) =>
        _acrylicEmptyFramePresenter = presenter;

    private Bitmap GetAcrylicFrameBitmap()
    {
        if (_acrylicFrameBitmap is null || _acrylicFrameBitmap.Size != ClientSize)
        {
            _acrylicFrameBitmap?.Dispose();
            _acrylicFrameBitmap = DesktopLayerBitmapFactory.Create(ClientSize.Width, ClientSize.Height);
        }
        return _acrylicFrameBitmap;
    }

    private bool TryPresentLyricsFrame()
    {
        if (_resourcesDisposed || IsDisposed || !_desktopAttached || !_lastPresentSucceeded || _presentingLayer ||
            !_usesAcrylicBackground || _acrylicPartialFramePresenter is null || _acrylicFrameBitmap is null ||
            _acrylicFrameBitmap.Size != ClientSize || HasDynamicVisual || _widgetDragging || _widgetResizing ||
            _calendarToolTipWidgetId is not null || IncomingBoxTransforms.Any()) return false;
        var widgets = DesktopWidgetItems.ToArray();
        var lyrics = widgets.Where(widget => widget.Kind == DesktopWidgetKind.Lyrics).ToArray();
        if (lyrics.Length == 0) return false;
        for (var index = 0; index < lyrics.Length; index++)
            if (lyrics.Skip(index + 1).Any(other => RectangleF.Inflate(WidgetRectangle(other), 8, 8)
                    .IntersectsWith(RectangleF.Inflate(WidgetRectangle(lyrics[index]), 8, 8)))) return false;
        // Widgets paint inside their rounded bounds; two DIPs cover edge antialiasing.
        var dirty = lyrics.Select(widget => RectangleF.Inflate(WidgetRectangle(widget), 2, 2)).Aggregate(RectangleF.Union);
        // An overlapping foreground needs the complete back-to-front blur pass.
        if (widgets.Any(widget => widget.Kind != DesktopWidgetKind.Lyrics &&
                RectangleF.Inflate(WidgetRectangle(widget), 8, 8).IntersectsWith(dirty)) ||
            _boxes.Any(box => RectangleF.Inflate(box.Bounds, 8, 8).IntersectsWith(dirty))) return false;
        var pixels = Rectangle.Intersect(new Rectangle(Point.Empty, ClientSize), Rectangle.FromLTRB(
            (int)Math.Floor(dirty.Left * _scale), (int)Math.Floor(dirty.Top * _scale),
            (int)Math.Ceiling(dirty.Right * _scale), (int)Math.Ceiling(dirty.Bottom * _scale)));
        if (pixels.Width <= 0 || pixels.Height <= 0) return true;
        _presentingLayer = true;
        try
        {
            using (var graphics = Graphics.FromImage(_acrylicFrameBitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent)) graphics.FillRectangle(clear, pixels);
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                graphics.ScaleTransform((float)_scale, (float)_scale);
                graphics.SetClip(dirty, CombineMode.Intersect);
                foreach (var widget in lyrics) DrawWidget(graphics, widget);
            }
            _acrylicPartialFramePresenter(_acrylicFrameBitmap, pixels);
            return true;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error($"Partial lyrics frame failed monitor={_monitor.Id}", error);
            return false;
        }
        finally { _presentingLayer = false; }
    }

    internal void SetAcrylicBackground(bool enabled)
    {
        if (_usesAcrylicBackground == enabled) return;
        _usesAcrylicBackground = enabled;
        ReleaseMovingBoxVisualCache();
        foreach (var id in _heightAnimationVisualCaches.Keys.ToArray()) ReleaseHeightAnimationVisualCache(id);
    }

    internal IEnumerable<AcrylicBoxRegion> GetAcrylicRegions()
    {
        if (IsDisposed || _resourcesDisposed) yield break;
        EnsureGeometry();
        var transform = _movingBox ?? _resizingBox;
        foreach (var widget in DesktopWidgetItems.Where(DesktopWidgets.HasBackground))
            yield return CreateAcrylicRegion(widget.Id, WidgetRectangle(widget), _monitor,
                (float)_runtime.State.Settings.Appearance.CornerRadius);
        foreach (var geometry in _boxes)
        {
            if (geometry.Box.Id == transform?.Id || IsIncomingBoxTransform(geometry.Box.Id)) continue;
            yield return CreateAcrylicRegion(geometry.Box.Id, geometry.Bounds, _monitor,
                (float)_runtime.State.Settings.Appearance.CornerRadius);
        }
        if (GetTransformAcrylicRegion() is { } ownTransform) yield return ownTransform;
        // Foreground and backdrop must use the same capture owner's view. The
        // destination has independent hover/animation state and may still see
        // the incoming box as collapsed, even though its content is expanded.
        foreach (var source in IncomingBoxTransforms)
            if (source.GetTransformAcrylicRegion() is { } incoming) yield return incoming;
    }

    private AcrylicBoxRegion? GetTransformAcrylicRegion()
    {
        if (IsDisposed || _resourcesDisposed || (_movingBox ?? _resizingBox) is not { } transform) return null;
        EnsureGeometry();
        var monitor = _runtime.Monitors.FirstOrDefault(m => m.Id == transform.MonitorId) ?? _monitor;
        var geometry = _boxes.FirstOrDefault(g => g.Box.Id == transform.Id);
        var bounds = monitor.Id == _monitor.Id && geometry is not null
            ? GetTransformGeometry(geometry).Bounds
            : CreateBoxGeometry(transform, (float)GetVisualBoxHeight(transform), IsEffectivelyCollapsed(transform)).Bounds;
        return CreateAcrylicRegion(transform.Id, bounds, monitor,
            (float)_runtime.State.Settings.Appearance.CornerRadius);
    }

    internal static AcrylicBoxRegion CreateAcrylicRegion(Guid id, RectangleF bounds, MonitorLayout monitor, float radius)
    {
        var scale = (float)monitor.DpiScale;
        return new AcrylicBoxRegion(id, new RectangleF(
            (float)monitor.PixelBounds.X + bounds.X * scale,
            (float)monitor.PixelBounds.Y + bounds.Y * scale,
            bounds.Width * scale, bounds.Height * scale), radius * scale);
    }

    private void PaintAcrylicFrame(Bitmap bitmap)
    {
        using (var clear = Graphics.FromImage(bitmap)) clear.Clear(Color.Transparent);
        var clipBounds = new RectangleF(0, 0,
            (float)(ClientSize.Width / _scale), (float)(ClientSize.Height / _scale));
        clipBounds.Inflate(8, 8);
        var behind = new List<RectangleF>();
        foreach (var widget in DesktopWidgetItems.Where(widget => WidgetRectangle(widget).IntersectsWith(clipBounds)))
        {
            var bounds = WidgetRectangle(widget);
            if (DesktopWidgets.HasBackground(widget))
                AcrylicOverlapBlur.Apply(bitmap, bounds,
                    (float)_runtime.State.Settings.Appearance.CornerRadius, _scale, behind);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                graphics.ScaleTransform((float)_scale, (float)_scale);
                DrawWidget(graphics, widget);
            }
            behind.Add(bounds);
        }
        foreach (var box in _boxes.Where(box => box.Bounds.IntersectsWith(clipBounds) && !IsIncomingBoxTransform(box.Box.Id)))
        {
            // No Graphics is open while the previous pixels are sampled.
            // Follow the same back-to-front geometry order as painting/input.
            AcrylicOverlapBlur.Apply(bitmap, box.Bounds,
                (float)_runtime.State.Settings.Appearance.CornerRadius, _scale, behind);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                graphics.TextContrast = 4;
                graphics.ScaleTransform((float)_scale, (float)_scale);
                DrawBox(graphics, box, clipBounds);
            }
            behind.Add(box.Bounds);
        }
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.ScaleTransform((float)_scale, (float)_scale);
            DrawIncomingBoxTransforms(graphics, clipBounds);
            DrawBoxAlignmentGuides(graphics, _monitor.Id);
            DrawCalendarToolTip(graphics);
        }
    }
    internal static double ResolveBoxTintOpacity(double opacity, bool acrylic) =>
        Math.Clamp(opacity, 0.35, 1) * (acrylic ? 0.72 : 1);

}
