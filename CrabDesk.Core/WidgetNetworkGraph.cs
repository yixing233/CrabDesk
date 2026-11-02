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

    public static Layout ChartLayout(double width, double height, double radius)
    {
        var body = WidgetMonitorPresentation.Body(width, height, radius);
        var half = (body.Width - 16) / 2;
        return new(new(body.X, body.Y, half, 40), new(body.X + half + 16, body.Y, half, 40),
            new(body.X, body.Y + 48, body.Width, body.Height - 68),
            new(body.X, body.Y + body.Height - 16, body.Width, 16));
    }

    /// <summary>
    /// Layout for the curve-only style: the plot takes the whole body minus the
    /// footer. Download and upload share no header row here, so both spans are
    /// reported as the plot area to keep the record shape unchanged.
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
        var top = inset;
        var footer = new LayoutRect(left, height - inset - 12, widthUsed, 12);
        var plot = new LayoutRect(left, top, widthUsed, Math.Max(0, footer.Y - top - 4));
        return new(plot, plot, plot, footer);
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
