using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private const double TabTransitionMilliseconds = 180;
    private readonly Dictionary<Guid, TabContentTransition> _tabTransitions = [];
    private bool HasFrameAnimations =>
        _heightAnimations.Count > 0 || _scrollAnimationKey is not null || _tabTransitions.Count > 0;

    private sealed class TabContentTransition(
        Bitmap outgoing, RectangleF body, RectangleF fromIndicator) : IDisposable
    {
        public Bitmap Outgoing { get; } = outgoing;
        public Bitmap? Incoming { get; set; }
        public RectangleF Body { get; } = body;
        public RectangleF FromIndicator { get; } = fromIndicator;
        public RectangleF ToIndicator { get; set; }
        public long StartedTimestamp { get; set; }
        public float Progress => (float)Math.Clamp(
            Stopwatch.GetElapsedTime(StartedTimestamp).TotalMilliseconds / TabTransitionMilliseconds, 0, 1);
        public void Dispose()
        {
            Outgoing.Dispose();
            Incoming?.Dispose();
        }
    }

    private static RectangleF GetSelectedTabBounds(BoxGeometry box)
    {
        var count = box.ManualTabs.Count > 0 ? box.ManualTabs.Count : box.CategoryTabs.Count;
        for (var index = 0; index < count; index++)
        {
            var active = box.ManualTabs.Count > 0
                ? box.ManualTabs[index].Id == box.ActiveManualTabId
                : box.CategoryTabs[index].Category == box.ActiveMappedFolderCategory;
            if (active) return GetBoxTabBounds(box, index, count);
        }
        return RectangleF.Empty;
    }

    private RectangleF GetTabIndicatorBounds(BoxGeometry box)
    {
        if (!_tabTransitions.TryGetValue(box.Box.Id, out var transition))
            return GetSelectedTabBounds(box);
        var eased = (float)AnimationMath.EaseOutSine(transition.Progress);
        var from = transition.FromIndicator;
        var to = transition.ToIndicator;
        return new RectangleF(from.X + (to.X - from.X) * eased, to.Y,
            from.Width + (to.Width - from.Width) * eased, to.Height);
    }

    private TabContentTransition? PrepareTabTransition(BoxGeometry box, bool preserveItemSelection)
    {
        // A tab switch ends the previous view's scroll, including its delayed
        // hover resume. OLE tab previews stay immediate for accurate drop targets.
        if (_scrollAnimationKey?.BoxId == box.Box.Id) CancelScrollAnimationForDirectManipulation();
        ClearItemHover();
        ReleaseHeightAnimationVisualCache(box.Box.Id);
        if (!_runtime.State.Settings.Appearance.AnimationEnabled || preserveItemSelection ||
            box.IsCollapsed || box.Body.Width <= 0 || box.Body.Height <= 0 ||
            _heightAnimations.ContainsKey(box.Box.Id) || IsTransformActive || _dragStarted)
        {
            CancelTabTransition(box.Box.Id);
            return null;
        }

        // Snapshot the visible blend on rapid clicks instead of jumping to the
        // preceding tab's final frame. Only content is cached, never the backdrop.
        var fromIndicator = GetTabIndicatorBounds(box);
        var outgoing = CaptureTabContent(box, captureTransition: true);
        CancelTabTransition(box.Box.Id);
        return new TabContentTransition(outgoing, box.Body, fromIndicator);
    }

    private void CompleteTabSelection(BoxGeometry previousBox, TabContentTransition? transition)
    {
        _geometryDirty = true;
        EnsureGeometry();
        ClearExpandedItemHitBounds(previousBox.Box.Id);
        var box = _boxes.First(candidate => candidate.Box.Id == previousBox.Box.Id);
        if (transition is not null)
        {
            try
            {
                transition.Incoming = CaptureTabContent(box, captureTransition: false);
                transition.ToIndicator = GetSelectedTabBounds(box);
                transition.StartedTimestamp = Stopwatch.GetTimestamp();
                _tabTransitions.Add(box.Box.Id, transition);
                _animationFrameClock.RequestFrames();
            }
            catch
            {
                transition.Dispose();
                throw;
            }
        }
        RequestManualTabRender(box.Bounds);
    }

    private Bitmap CaptureTabContent(BoxGeometry box, bool captureTransition)
    {
        var bitmap = DesktopLayerBitmapFactory.Create(
            Math.Max(1, (int)Math.Ceiling(box.Body.Width * _scale)),
            Math.Max(1, (int)Math.Ceiling(box.Body.Height * _scale)));
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Transform = new Matrix((float)_scale, 0, 0, (float)_scale,
                -(float)(box.Body.X * _scale), -(float)(box.Body.Y * _scale));
            if (!captureTransition || !DrawTabTransitionContent(graphics, box))
                DrawBoxContent(graphics, box, box.Body, includeDropPreview: false,
                    includeSelectionRectangle: false, includeItemHoverFeedback: false);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private bool DrawTabTransitionContent(Graphics graphics, BoxGeometry box)
    {
        if (!_tabTransitions.TryGetValue(box.Box.Id, out var transition)) return false;
        if (!_runtime.State.Settings.Appearance.AnimationEnabled || box.Body != transition.Body || box.IsCollapsed)
        {
            CancelTabTransition(box.Box.Id);
            return false;
        }
        transition.Incoming ??= CaptureTabContent(box, captureTransition: false);
        var eased = (float)AnimationMath.EaseOutSine(transition.Progress);
        var direction = Math.Sign(transition.ToIndicator.X - transition.FromIndicator.X);
        var distance = Math.Min(18f, box.Body.Width * 0.06f) * direction;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(box.Body, CombineMode.Intersect);
            DrawTabContentFrame(graphics, transition.Outgoing, box.Body, -distance * eased, 1 - eased);
            DrawTabContentFrame(graphics, transition.Incoming, box.Body, distance * (1 - eased), eased);
        }
        finally { graphics.Restore(state); }
        return true;
    }

    private static void DrawTabContentFrame(Graphics graphics, Bitmap bitmap, RectangleF body, float offset, float opacity)
    {
        if (opacity <= 0) return;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        graphics.DrawImage(bitmap,
            new PointF[] { new(body.Left + offset, body.Top), new(body.Right + offset, body.Top), new(body.Left + offset, body.Bottom) },
            new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel, attributes);
    }

    private RectangleF? AdvanceTabTransitions()
    {
        RectangleF? dirty = null;
        foreach (var (id, transition) in _tabTransitions.ToArray())
        {
            var box = _boxes.FirstOrDefault(candidate => candidate.Box.Id == id);
            if (box is not null)
                dirty = dirty is { } area ? RectangleF.Union(area, box.Bounds) : box.Bounds;
            if (box is null || transition.Progress >= 1 || !_runtime.State.Settings.Appearance.AnimationEnabled)
            {
                CancelTabTransition(id);
                QueueHoverReconcile();
            }
        }
        return dirty;
    }

    private void CancelTabTransition(Guid boxId)
    {
        if (!_tabTransitions.Remove(boxId, out var transition)) return;
        transition.Dispose();
        // A height cache prepared while tabs were blending must not replay
        // that intermediate content on the next collapse or expansion.
        ReleaseHeightAnimationVisualCache(boxId);
        _animationFrameClock.StopWhenIdle(HasFrameAnimations);
    }

    private void ClearTabTransitions()
    {
        foreach (var transition in _tabTransitions.Values) transition.Dispose();
        _tabTransitions.Clear();
    }
}
