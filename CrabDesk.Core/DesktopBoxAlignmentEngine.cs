namespace CrabDesk.Core;

public readonly record struct DesktopBoxAlignmentGuide(bool IsVertical, double Position, double Start, double End);

public sealed record DesktopBoxAlignmentResult(LayoutRect Bounds, IReadOnlyList<DesktopBoxAlignmentGuide> Guides);

/// <summary>Free movement with edge/center snapping to alignment lines across the work area.</summary>
public static class DesktopBoxAlignmentEngine
{
    public const double DefaultThreshold = 8;

    public static LayoutRect Align(LayoutRect moving, IEnumerable<LayoutRect> peers, LayoutRect workArea,
        double threshold = DefaultThreshold) => Calculate(moving, peers, workArea, threshold).Bounds;

    public static DesktopBoxAlignmentResult Calculate(
        LayoutRect moving,
        IEnumerable<LayoutRect> peers,
        LayoutRect workArea,
        double threshold = DefaultThreshold)
    {
        if (!IsValid(moving) || !IsValid(workArea) ||
            !double.IsFinite(threshold) || threshold < 0)
            return new(moving, []);

        moving = moving with
        {
            X = ClampStart(moving.X, moving.Width, workArea.X, workArea.Width),
            Y = ClampStart(moving.Y, moving.Height, workArea.Y, workArea.Height)
        };
        // Card separation does not affect alignment; only the distance to
        // each edge or center line controls snapping.
        var candidates = peers.Where(IsValid).ToArray();
        var x = FindAxis(moving, candidates, workArea, threshold, vertical: true);
        var y = FindAxis(moving, candidates, workArea, threshold, vertical: false);
        var aligned = moving with { X = x?.Start ?? moving.X, Y = y?.Start ?? moving.Y };
        var guides = new List<DesktopBoxAlignmentGuide>(2);
        if (x is { } horizontalSnap)
            guides.Add(new(true, horizontalSnap.Anchor,
                Math.Min(aligned.Y, horizontalSnap.Peer.Y),
                Math.Max(aligned.Y + aligned.Height, horizontalSnap.Peer.Y + horizontalSnap.Peer.Height)));
        if (y is { } verticalSnap)
            guides.Add(new(false, verticalSnap.Anchor,
                Math.Min(aligned.X, verticalSnap.Peer.X),
                Math.Max(aligned.X + aligned.Width, verticalSnap.Peer.X + verticalSnap.Peer.Width)));
        return new(aligned, guides);
    }

    /// <summary>
    /// Snaps the edges being dragged during a resize, rather than the whole
    /// rectangle. Dragging moves every anchor together, so one offset fits all;
    /// resizing pins the edges the user is not holding and moves only the ones
    /// they are, which is why each axis needs its own decision and the result is
    /// expressed as a change in size instead of a change in position.
    ///
    /// Edges that are not being resized stay where they are, so a snapped left
    /// edge can never drag the right one along with it.
    /// </summary>
    public static ResizeResult SnapResize(
        LayoutRect resizing,
        WidgetResizeEdges edges,
        IEnumerable<LayoutRect> peers,
        LayoutRect workArea,
        double threshold = DefaultThreshold)
    {
        if (!IsValid(resizing) || !IsValid(workArea) ||
            !double.IsFinite(threshold) || threshold < 0 || edges == WidgetResizeEdges.None)
            return new(resizing, []);

        var candidates = peers.Where(IsValid).ToArray();
        var left = resizing.X;
        var top = resizing.Y;
        var right = resizing.X + resizing.Width;
        var bottom = resizing.Y + resizing.Height;
        var guides = new List<DesktopBoxAlignmentGuide>(2);

        if (edges.HasFlag(WidgetResizeEdges.Left))
        {
            if (FindEdge(candidates, left, threshold, vertical: true, ownStart: resizing.Y, ownSize: resizing.Height) is { } hit)
            {
                left = hit.Anchor;
                guides.Add(new(true, hit.Anchor, resizing.Y, resizing.Y + resizing.Height));
            }
        }
        else if (edges.HasFlag(WidgetResizeEdges.Right))
        {
            if (FindEdge(candidates, right, threshold, vertical: true, ownStart: resizing.Y, ownSize: resizing.Height) is { } hit)
            {
                right = hit.Anchor;
                guides.Add(new(true, hit.Anchor, resizing.Y, resizing.Y + resizing.Height));
            }
        }

        if (edges.HasFlag(WidgetResizeEdges.Top))
        {
            if (FindEdge(candidates, top, threshold, vertical: false, ownStart: resizing.X, ownSize: resizing.Width) is { } hit)
            {
                top = hit.Anchor;
                guides.Add(new(false, hit.Anchor, resizing.X, resizing.X + resizing.Width));
            }
        }
        else if (edges.HasFlag(WidgetResizeEdges.Bottom))
        {
            if (FindEdge(candidates, bottom, threshold, vertical: false, ownStart: resizing.X, ownSize: resizing.Width) is { } hit)
            {
                bottom = hit.Anchor;
                guides.Add(new(false, hit.Anchor, resizing.X, resizing.X + resizing.Width));
            }
        }

        // The minimum size still wins, and it is enforced on the edge the user is
        // holding: pushing the opposite edge instead would silently move a side
        // the resize was never allowed to touch.
        var minWidth = Math.Min(WidgetSizing.MinimumWidth, workArea.Width);
        var minHeight = Math.Min(WidgetSizing.MinimumHeight, workArea.Height);
        if (edges.HasFlag(WidgetResizeEdges.Left)) left = Math.Min(left, right - minWidth);
        else if (edges.HasFlag(WidgetResizeEdges.Right)) right = Math.Max(right, left + minWidth);
        if (edges.HasFlag(WidgetResizeEdges.Top)) top = Math.Min(top, bottom - minHeight);
        else if (edges.HasFlag(WidgetResizeEdges.Bottom)) bottom = Math.Max(bottom, top + minHeight);
        return new(new LayoutRect(left, top, right - left, bottom - top), guides);
    }

    /// <summary>
    /// Nearest edge or center line of a peer within the threshold. Every peer
    /// contributes three lines per axis, the same ones the drag path snaps to,
    /// so a card lined up against a box edge lines up the same way when resized.
    /// </summary>
    private static Candidate? FindEdge(LayoutRect[] peers, double position, double threshold,
        bool vertical, double ownStart, double ownSize)
    {
        Candidate? best = null;
        foreach (var peer in peers)
        {
            var peerStart = vertical ? peer.X : peer.Y;
            var peerSize = vertical ? peer.Width : peer.Height;
            var gap = vertical ? Gap(ownStart, ownSize, peer.Y, peer.Height)
                : Gap(ownStart, ownSize, peer.X, peer.Width);
            for (var anchorIndex = 0; anchorIndex < 3; anchorIndex++)
            {
                var anchor = peerStart + peerSize * anchorIndex / 2;
                var distance = Math.Abs(anchor - position);
                if (distance > threshold) continue;
                var candidate = new Candidate(anchor, anchor, distance, gap, peer);
                if (best is null || Compare(candidate, best.Value) < 0) best = candidate;
            }
        }
        return best;
    }

    public readonly record struct ResizeResult(LayoutRect Bounds, IReadOnlyList<DesktopBoxAlignmentGuide> Guides);

    private readonly record struct Candidate(double Start, double Anchor, double Distance, double Gap, LayoutRect Peer);

    private static Candidate? FindAxis(LayoutRect moving, LayoutRect[] peers, LayoutRect area,
        double threshold, bool vertical)
    {
        var start = vertical ? moving.X : moving.Y;
        var size = vertical ? moving.Width : moving.Height;
        var areaStart = vertical ? area.X : area.Y;
        var areaSize = vertical ? area.Width : area.Height;
        Candidate? best = null;
        foreach (var peer in peers)
        {
            var peerStart = vertical ? peer.X : peer.Y;
            var peerSize = vertical ? peer.Width : peer.Height;
            var gap = vertical ? Gap(moving.Y, moving.Height, peer.Y, peer.Height)
                : Gap(moving.X, moving.Width, peer.X, peer.Width);
            for (var movingAnchor = 0; movingAnchor < 3; movingAnchor++)
                for (var peerAnchor = 0; peerAnchor < 3; peerAnchor++)
                {
                    var anchor = peerStart + peerSize * peerAnchor / 2;
                    var candidateStart = anchor - size * movingAnchor / 2;
                    var distance = Math.Abs(candidateStart - start);
                    if (distance > threshold ||
                        candidateStart != ClampStart(candidateStart, size, areaStart, areaSize)) continue;
                    var candidate = new Candidate(candidateStart, anchor, distance, gap, peer);
                    // Stable ties avoid changing the target when state/stack order changes.
                    if (best is null || Compare(candidate, best.Value) < 0) best = candidate;
                }
        }
        return best;
    }

    private static int Compare(Candidate a, Candidate b)
    {
        var distance = a.Distance.CompareTo(b.Distance);
        if (distance != 0) return distance;
        var gap = a.Gap.CompareTo(b.Gap);
        if (gap != 0) return gap;
        var anchor = a.Anchor.CompareTo(b.Anchor);
        if (anchor != 0) return anchor;
        var start = a.Start.CompareTo(b.Start);
        if (start != 0) return start;
        var x = a.Peer.X.CompareTo(b.Peer.X);
        if (x != 0) return x;
        var y = a.Peer.Y.CompareTo(b.Peer.Y);
        if (y != 0) return y;
        var width = a.Peer.Width.CompareTo(b.Peer.Width);
        return width != 0 ? width : a.Peer.Height.CompareTo(b.Peer.Height);
    }

    private static double Gap(double a, double aSize, double b, double bSize) =>
        Math.Max(0, Math.Max(a - (b + bSize), b - (a + aSize)));

    private static double ClampStart(double value, double size, double areaStart, double areaSize) =>
        Math.Clamp(value, areaStart, areaStart + Math.Max(0, areaSize - size));

    private static bool IsValid(LayoutRect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0;
}
