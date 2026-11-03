namespace CrabDesk.Core;

public sealed record WidgetNetworkRatePoint(DateTimeOffset CapturedAt, double? Download, double? Upload);

public static class WidgetNetworkGraph
{
    public const int WindowSeconds = 60;
    public readonly record struct Point(double X, double Y);
    public sealed record Graph(double Maximum, IReadOnlyList<Point[]> Download, IReadOnlyList<Point[]> Upload);
    public readonly record struct Layout(LayoutRect Download, LayoutRect Upload, LayoutRect Plot, LayoutRect Footer);

    public static void Append(DesktopWidget widget, WidgetNetworkSnapshot? snapshot)
    {
        var history = widget.NetworkHistory;
        if (snapshot is not { IsConnected: true } || widget.Network?.InterfaceId != snapshot.InterfaceId)
            history.Clear();
        if (snapshot is not { IsConnected: true }) return;
        if (history.Count > 0 && snapshot.CapturedAt <= history[^1].CapturedAt) return;
        history.Add(new(snapshot.CapturedAt, Valid(snapshot.DownloadBytesPerSecond), Valid(snapshot.UploadBytesPerSecond)));
        history.RemoveAll(point => point.CapturedAt < snapshot.CapturedAt.AddSeconds(-WindowSeconds));
        if (history.Count > 120) history.RemoveRange(0, history.Count - 120);
    }

    private static double? Valid(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    // Timestamp positions preserve real elapsed time; missing samples leave gaps rather than false zeroes.
    public static Graph Project(IReadOnlyList<WidgetNetworkRatePoint> history, LayoutRect plot, DateTimeOffset now)
    {
        var start = now.AddSeconds(-WindowSeconds);
        var visible = history.Where(point => point.CapturedAt >= start && point.CapturedAt <= now).ToArray();
        var maximum = Math.Max(1024, visible.SelectMany(point => new[] { Valid(point.Download) ?? 0, Valid(point.Upload) ?? 0 }).DefaultIfEmpty().Max() * 1.15);
        IReadOnlyList<Point[]> Series(Func<WidgetNetworkRatePoint, double?> selector)
        {
            var segments = new List<Point[]>(); var points = new List<Point>(); DateTimeOffset? previous = null;
            void Flush() { if (points.Count > 0) segments.Add(points.ToArray()); points.Clear(); }
            foreach (var sample in visible)
            {
                if (previous is { } last && (sample.CapturedAt - last).TotalSeconds > 3) Flush();
                previous = sample.CapturedAt;
                if (Valid(selector(sample)) is not { } value) { Flush(); continue; }
                points.Add(new(plot.X + (sample.CapturedAt - start).TotalSeconds / WindowSeconds * plot.Width,
                    plot.Y + plot.Height * (1 - value / maximum)));
            }
            Flush(); return segments;
        }
        return new(maximum, Series(point => point.Download), Series(point => point.Upload));
    }

    /// <summary>
    /// Layout for the curve style: a compact headline of current speeds, the
    /// plot filling the rest of the body, and a footer with the time span and
    /// the scale maximum.
    /// </summary>
    public static Layout CurveLayout(double width, double height, double radius)
    {
        var body = WidgetMonitorPresentation.Body(width, height, radius);
        // The title row is gone and nothing sits above the plot, so the curve
        // reclaims that space instead of starting below a title it does not have.
        var inset = 12 + Math.Clamp(radius, 0, 20) * .2;
        // Keep the curve off the rounded edge: Body already insets by the card
        // radius, and this adds the usual text margin on top of it.
        var left = body.X + 2;
        var widthUsed = Math.Max(0, body.Width - 4);
        // A single compact row of current speeds: a curve alone says nothing
        // about the magnitude right now, which is what the reader looks for first.
        // DrawNetworkSpeed lays out a caption line above the value, so the header
        // needs both rows before the plot may start.
        var headerHeight = 40d;
        var half = Math.Max(0, (widthUsed - 12) / 2);
        var download = new LayoutRect(left, inset, half, headerHeight);
        var upload = new LayoutRect(left + half + 12, inset, half, headerHeight);
        var plotTop = inset + headerHeight + 8;
        var footer = new LayoutRect(left, height - inset - 12, widthUsed, 12);
        var plot = new LayoutRect(left, plotTop, widthUsed, Math.Max(0, footer.Y - plotTop - 4));
        return new(download, upload, plot, footer);
    }

    public static (LayoutRect Download, LayoutRect Upload) SpeedLayout(double width, double height, double radius)
    {
        var inset = 12 + Math.Clamp(radius, 0, 20) * .2;
        if (width < 300)
        {
            var row = (height - 2 * inset) / 2;
            return (new(inset, inset, width - 2 * inset, row), new(inset, inset + row, width - 2 * inset, row));
        }
        var half = (width - inset * 2 - 16) / 2;
        return (new(inset, (height - 40) / 2, half, 40), new(inset + half + 16, (height - 40) / 2, half, 40));
    }
}
