namespace CrabDesk.Core;

public readonly record struct DesktopBoxAlignmentGuide(bool IsVertical, double Position, double Start, double End);

public sealed record DesktopBoxAlignmentResult(LayoutRect Bounds, IReadOnlyList<DesktopBoxAlignmentGuide> Guides);

/// <summary>Free movement with optional edge/center snapping to nearby boxes only.</summary>
public static class DesktopBoxAlignmentEngine
{
    public const double DefaultThreshold = 8;
    public const double DefaultProximity = 48;

    public static LayoutRect Align(LayoutRect moving, IEnumerable<LayoutRect> peers, LayoutRect workArea,
        double threshold = DefaultThreshold) => Calculate(moving, peers, workArea, threshold).Bounds;

    public static DesktopBoxAlignmentResult Calculate(
        LayoutRect moving,
        IEnumerable<LayoutRect> peers,
        LayoutRect workArea,
        double threshold = DefaultThreshold,
        double proximity = DefaultProximity)
    {
        if (!IsValid(moving) || !IsValid(workArea) ||
            !double.IsFinite(threshold) || threshold < 0 || !double.IsFinite(proximity) || proximity < 0)
            return new(moving, []);

        moving = moving with
        {
            X = ClampStart(moving.X, moving.Width, workArea.X, workArea.Width),
            Y = ClampStart(moving.Y, moving.Height, workArea.Y, workArea.Height)
        };
        // A remote peer on the same infinite alignment line is not a neighbor.
        var nearby = peers.Where(peer => IsValid(peer) &&
            Gap(moving.X, moving.Width, peer.X, peer.Width) <= proximity &&
            Gap(moving.Y, moving.Height, peer.Y, peer.Height) <= proximity).ToArray();
        var x = FindAxis(moving, nearby, workArea, threshold, vertical: true);
        var y = FindAxis(moving, nearby, workArea, threshold, vertical: false);
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
