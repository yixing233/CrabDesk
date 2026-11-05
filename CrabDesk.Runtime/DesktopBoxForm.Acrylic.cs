using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private bool _usesAcrylicBackground;
    private Action<Bitmap, Point, IReadOnlyList<AcrylicBoxRegion>>? _acrylicFramePresenter;

    internal void SetAcrylicFramePresenter(Action<Bitmap, Point, IReadOnlyList<AcrylicBoxRegion>> presenter) =>
        _acrylicFramePresenter = presenter;

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
        foreach (var widget in DesktopWidgetItems)
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
