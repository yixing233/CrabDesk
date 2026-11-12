using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private static RectangleF MonitorRectangle(DesktopWidget widget, LayoutRect rect) => new(
        (float)(widget.Bounds.X + rect.X), (float)(widget.Bounds.Y + rect.Y), (float)rect.Width, (float)rect.Height);

    private LayoutRect MonitorBody(DesktopWidget widget) => widget.Kind == DesktopWidgetKind.ControlCenter
        ? ControlCenterPresentation.Body(widget.Bounds.Width, widget.Bounds.Height, WidgetCornerRadius)
        : WidgetMonitorPresentation.Body(widget.Bounds.Width, widget.Bounds.Height, WidgetCornerRadius);

    private void DrawPerformanceWidget(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var body = MonitorBody(widget); var wide = body.Width >= 300;
        var rows = WidgetMonitorPresentation.PerformanceMetrics(widget.Performance);
        for (var index = 0; index < rows.Length; index++)
        {
            var metric = rows[index]; var cell = MonitorRectangle(widget, WidgetMonitorPresentation.PerformanceCell(body, index));
            var asset = WidgetContentVisuals.PerformanceAsset(index);
            var tone = ContentTone(asset, text);
            var solid = widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid;
            DrawContentWash(graphics, new(cell.X - 4, cell.Y - 2, cell.Width + 8, cell.Height - 2), solid ? text : tone, solid ? 6 : 16);
            DrawContentIcon(graphics, asset, new(cell.X, cell.Y, 20, 20));
            WidgetText(graphics, metric.Label, new(cell.X + 26, cell.Y, cell.Width - 78, 20), muted, 12);
            WidgetText(graphics, metric.Value, new(cell.Right - 52, cell.Y, 52, 20), text, 13.5f, bold: true, centered: true);
            // Temperature sits on the label row, right after the metric name.
            // Keeping it there leaves the value column for the percentage alone
            // and stops it floating between the number and the model name.
            if (metric.TemperatureCelsius is { } celsius)
            {
                var degrees = WidgetMonitorMetrics.Degrees(celsius);
                var labelWidth = MeasureWidgetText(graphics, metric.Label, 12);
                WidgetText(graphics, degrees, new(cell.X + 26 + labelWidth + 6, cell.Y, Math.Max(0, cell.Width - labelWidth - 84), 20),
                    muted, 10.5f);
            }
            if (wide) WidgetText(graphics, metric.Detail, new(cell.X, cell.Y + 24, cell.Width, 20), muted, 10.5f);
            var track = new RectangleF(cell.X, cell.Bottom - (wide ? 2 : 7), cell.Width, 3);
            using var path = RoundedRectangle(track, 1.5f);
            using var background = new SolidBrush(Color.FromArgb(36, tone)); graphics.FillPath(background, path);
            if (metric.Percentage is { } percent && percent > 0 && double.IsFinite(percent))
            {
                var fillBounds = track; fillBounds.Width *= (float)Math.Clamp(percent / 100, 0, 1);
                using var fillPath = RoundedRectangle(fillBounds, Math.Min(1.5f, fillBounds.Width / 2));
                using var fill = new SolidBrush(tone); graphics.FillPath(fill, fillPath);
            }
        }
    }

    private RectangleF NetworkSortRectangle(DesktopWidget widget, bool upload)
    {
        if (widget.NetworkDisplayMode != NetworkWidgetDisplayMode.Details) return RectangleF.Empty;
        var layout = WidgetMonitorPresentation.NetworkFields(MonitorBody(widget));
        return MonitorRectangle(widget, upload ? layout.SortUpload : layout.SortDownload);
    }

    private void DrawNetworkWidget(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        if (widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Speeds)
        { DrawNetworkSpeeds(graphics, widget, text, muted, accent); return; }
        // The retired combined style is migrated to Curve on load, so a saved
        // value can still arrive here and must draw the curve rather than fall
        // through to the detail rows it no longer resembles.
        if (widget.NetworkDisplayMode is NetworkWidgetDisplayMode.Curve or NetworkWidgetDisplayMode.LegacyChart)
        { DrawNetworkCurveOnly(graphics, widget, text, muted, accent); return; }
        var layout = WidgetMonitorPresentation.NetworkFields(MonitorBody(widget)); var snapshot = widget.Network;
        void Speed(LayoutRect rect, LucideRuntimeIcon icon, double? bytes)
        {
            var bounds = MonitorRectangle(widget, rect);
            var asset = icon == LucideRuntimeIcon.ArrowUpToLine ? "content-upload" : "content-download";
            DrawContentWash(graphics, RectangleF.Inflate(bounds, 3, 2), ContentTone(asset, text),
                widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid ? 10 : 18);
            LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y + (bounds.Height - 16) / 2, 16, 16), ContentTone(asset, text), 16);
            DrawSpeedValue(graphics, bytes, new(bounds.X + 22, bounds.Y, bounds.Width - 22, bounds.Height), text, 21);
        }
        Speed(layout.Download, LucideRuntimeIcon.ArrowDownToLine, snapshot?.DownloadBytesPerSecond);
        Speed(layout.Upload, LucideRuntimeIcon.ArrowUpToLine, snapshot?.UploadBytesPerSecond);
        var connection = MonitorRectangle(widget, layout.Connection);
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Network, new(connection.X, connection.Y + 1, 16, 16),
            Color.FromArgb(snapshot?.IsConnected is false ? 115 : 255, ContentTone("content-network", text)), 16);
        WidgetText(graphics, WidgetMonitorPresentation.ConnectionText(snapshot), new(connection.X + 21, connection.Y, Math.Max(0, connection.Width - 21), connection.Height), muted, 11.5f);
        var latency = snapshot?.LatencyMilliseconds is { } milliseconds ? $"延迟 {milliseconds} ms" :
            "延迟 " + (snapshot?.LatencyStatus ?? "等待测量");
        var latencyBounds = MonitorRectangle(widget, layout.Latency);
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Clock, new(latencyBounds.X, latencyBounds.Y + 1, 16, 16),
            ContentTone("content-latency", text), 16);
        WidgetText(graphics, latency, new(latencyBounds.X + 21, latencyBounds.Y, Math.Max(0, latencyBounds.Width - 21), latencyBounds.Height), muted, 11);
        WidgetText(graphics, "进程排行 · 全部网卡", MonitorRectangle(widget, layout.RankingTitle), muted, 10.5f);
        foreach (var upload in new[] { false, true })
        {
            var selected = widget.NetworkProcessSort == (upload ? NetworkProcessSort.Upload : NetworkProcessSort.Download);
            var button = NetworkSortRectangle(widget, upload);
            if (selected) DrawContentWash(graphics, button, ContentTone(upload ? "content-upload" : "content-download", text), 28);
            WidgetText(graphics, upload ? "上传" : "下载", button, selected ? text : muted, 10.5f, bold: selected, centered: true);
        }
        var processes = snapshot?.Processes ?? WidgetNetworkProcesses.Waiting;
        var top = processes.Top(widget.NetworkProcessSort);
        if (processes.Status != NetworkProcessStatus.Ready || top.Count == 0)
        {
            var first = MonitorRectangle(widget, layout.Processes[0]);
            WidgetText(graphics, processes.Message(widget.NetworkProcessSort), first with { Height = 24 }, muted, 11);
            if (processes.Status == NetworkProcessStatus.PermissionRequired)
                WidgetText(graphics, "请以管理员身份运行 CrabDesk",
                    MonitorRectangle(widget, layout.Processes[2]), muted, 10);
            return;
        }
        for (var index = 0; index < top.Count; index++)
        {
            var row = MonitorRectangle(widget, layout.Processes[index]);
            WidgetText(graphics, (index + 1).ToString(), new(row.X, row.Y, 16, row.Height), muted, 10);
            WidgetText(graphics, top[index].Name, new(row.X + 19, row.Y, Math.Max(0, row.Width - 113), row.Height), text, 11);
            DrawSpeedValue(graphics, top[index].Rate(widget.NetworkProcessSort),
                new(row.Right - 90, row.Y, 90, row.Height), text, 10.5f, rightAligned: true);
        }
    }

    private static Color NetworkUploadColor(Color text) => ContentTone("content-upload", text);

    private void DrawNetworkSpeed(Graphics graphics, DesktopWidget widget, LayoutRect rect, double? bytes,
        bool upload, bool compact, Color text, Color muted, Color ink)
    {
        var bounds = MonitorRectangle(widget, rect);
        var asset = upload ? "content-upload" : "content-download";
        var icon = upload ? LucideRuntimeIcon.ArrowUpToLine : LucideRuntimeIcon.ArrowDownToLine;
        if (compact)
        {
            LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y, 18, bounds.Height), ContentTone(asset, text), 18);
            DrawSpeedValue(graphics, bytes, new(bounds.X + 22, bounds.Y, bounds.Width - 22, bounds.Height), text, 18, rightAligned: true);
            return;
        }
        DrawContentWash(graphics, RectangleF.Inflate(bounds, 3, 2), ContentTone(asset, text),
            widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid ? 10 : 18);
        LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y, 16, 16), ContentTone(asset, text), 16);
        WidgetText(graphics, upload ? "上传" : "下载", new(bounds.X + 19, bounds.Y, bounds.Width - 19, 14), muted, 10.5f);
        DrawSpeedValue(graphics, bytes, new(bounds.X, bounds.Y + 14, bounds.Width, 26), text, 21);
    }

    /// <summary>
    /// Draws a speed so the number stays put while the value is resampled.
    ///
    /// The row is split into two slots of fixed width, both derived from the
    /// widest reading the card can show rather than from the live value: the
    /// number ends at a constant right edge and the unit keeps its own slot.
    /// A value that gains a digit therefore grows leftwards inside
    /// its own slot and cannot move the unit beside it.
    /// </summary>
    internal static void DrawSpeedValue(Graphics graphics, double? bytes, RectangleF bounds, Color text, float size, bool rightAligned = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var slot = MeasureSpeedSlots(graphics, bounds, size, rightAligned);
        using var font = slot.Font;
        // Typographic formatting for drawing as well as measuring: the default
        // StringFormat adds roughly a sixth of an em of padding on each side, so
        // a slot sized from a plain measurement trimmed the very glyphs it was
        // sized to hold and "921.6" came out as "92...".
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var brush = new SolidBrush(text);
        var (number, unit) = WidgetMonitorMetrics.SpeedParts(bytes);
        var numberRight = bounds.X + slot.NumberWidth;
        // Number: right-aligned in its slot, so extra digits extend leftwards
        // rather than pushing the unit. The slot stops short of the card's left
        // edge so the widest reading never touches it.
        graphics.DrawString(number, font, brush,
            new RectangleF(bounds.X, bounds.Y, slot.NumberWidth, bounds.Height), format);
        // The compact card pins the unit's right edge to its outer padding.
        // Changing units never moves the separately anchored number.
        format.Alignment = rightAligned ? StringAlignment.Far : StringAlignment.Near;
        graphics.DrawString(unit, font, brush,
            new RectangleF(numberRight + slot.Gap, bounds.Y, Math.Max(0, bounds.Width - slot.NumberWidth - slot.Gap), bounds.Height),
            format);
    }

    internal readonly record struct SpeedSlots(Font Font, float NumberWidth, float Gap);

    /// <summary>
    /// Constant slot widths for a row, plus the font that keeps the widest
    /// reading inside them. Both come from fixed sample strings, so consecutive
    /// frames lay out identically and the figure cannot visibly drift.
    /// </summary>
    internal static SpeedSlots MeasureSpeedSlots(Graphics graphics, RectangleF bounds, float size, bool rightAligned = false)
    {
        var availableWidth = bounds.Width;
        size *= (float)WidgetTypography.NormalizeScale(WidgetTextRendering.Current(graphics)?.FontScale ?? 1);
        using var measure = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        // Keep the widest reading off the card's edge; the row is flush with it
        // otherwise and "921.6" looked clipped.
        bounds = new RectangleF(bounds.X, bounds.Y, Math.Max(0, bounds.Width - size * 0.3f), bounds.Height);
        var current = size;
        while (true)
        {
            var font = WidgetTextRendering.CreateFont(graphics, current, bold: true, scale: false);
            // DrawString pads inside the layout rectangle, so both slots carry
            // headroom; without it the widest sample is trimmed to an ellipsis.
            // Measured with the same typographic format used to draw, so these
            // widths are what actually reaches the row.
            var number = graphics.MeasureString(WidgetMonitorMetrics.WidestSpeedNumber, font, int.MaxValue, measure).Width;
            var unit = graphics.MeasureString(WidgetMonitorMetrics.WidestSpeedUnit, font, int.MaxValue, measure).Width;
            // A visible gap: typographic formatting removes the padding that used
            // to separate "921.6" from "KB/s", so without this they read as one
            // token.
            var gap = current * 0.3f;
            if (number + gap + unit <= bounds.Width || current <= 9f)
            {
                // The compact card spends the remaining width before the unit,
                // keeping its outer padding even and both text anchors stable.
                var numberWidth = rightAligned ? Math.Max(0, availableWidth - unit - gap) : Math.Min(number, bounds.Width);
                return new SpeedSlots(font, numberWidth, gap);
            }
            font.Dispose();
            current -= 0.5f;
        }
    }

    private void DrawNetworkSpeeds(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var fields = WidgetNetworkGraph.SpeedLayout(widget.Bounds.Width, widget.Bounds.Height,
            WidgetCornerRadius);
        var compact = widget.Bounds.Width < 300;
        DrawNetworkSpeed(graphics, widget, fields.Download, widget.Network?.DownloadBytesPerSecond, false, compact, text, muted, accent);
        DrawNetworkSpeed(graphics, widget, fields.Upload, widget.Network?.UploadBytesPerSecond, true, compact, text, muted, NetworkUploadColor(text));
    }

    /// <summary>
    /// Curve-only style: the two speed series fill the card with no headline
    /// figures. It exists for the desktop corner where the numbers are already
    /// known and only the trend matters.
    /// </summary>
    private void DrawNetworkCurveOnly(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var layout = WidgetNetworkGraph.CurveLayout(widget.Bounds.Width, widget.Bounds.Height,
            WidgetCornerRadius);
        // Current speeds sit above the plot in the compact form: the curve shows
        // the trend, these two numbers answer "how fast is it right now".
        DrawNetworkSpeed(graphics, widget, layout.Download, widget.Network?.DownloadBytesPerSecond, false, false, text, muted, accent);
        DrawNetworkSpeed(graphics, widget, layout.Upload, widget.Network?.UploadBytesPerSecond, true, false, text, muted, NetworkUploadColor(text));
        var plot = MonitorRectangle(widget, layout.Plot);
        var graph = WidgetNetworkGraph.Project(widget.NetworkHistory, layout.Plot, DateTimeOffset.Now);
        using (var grid = new Pen(Color.FromArgb(24, text), 1) { DashStyle = DashStyle.Dash })
        {
            for (var row = 0; row <= 2; row++)
            {
                var y = plot.Y + plot.Height * row / 2;
                graphics.DrawLine(grid, plot.X, y, plot.Right, y);
            }
        }
        DrawNetworkSeries(graphics, widget, graph.Download, ContentTone("content-download", text));
        DrawNetworkSeries(graphics, widget, graph.Upload, NetworkUploadColor(text));
        if (graph.Download.Count == 0 && graph.Upload.Count == 0)
        {
            WidgetText(graphics, widget.Network is { IsConnected: false } ? "未连接网络" : "正在积累数据…",
                plot, muted, 11, centered: true);
        }
        var footer = MonitorRectangle(widget, layout.Footer);
        WidgetText(graphics, "最近 60 秒", new(footer.X, footer.Y, footer.Width / 2, footer.Height), muted, 10);
        WidgetText(graphics, "上限 " + WidgetMonitorMetrics.Speed(graph.Maximum),
            new(footer.X + footer.Width / 2, footer.Y, footer.Width / 2, footer.Height), muted, 10, rightAligned: true);
    }

    /// <summary>Draws one projected series, breaking the line wherever a sample is missing.</summary>
    private static void DrawNetworkSeries(Graphics graphics, DesktopWidget widget,
        System.Collections.Generic.IReadOnlyList<WidgetNetworkGraph.Point[]> segments, Color color)
    {
        using var pen = new Pen(color, 2) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(color);
        foreach (var segment in segments)
        {
            var points = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(segment, point =>
                new PointF((float)(widget.Bounds.X + point.X), (float)(widget.Bounds.Y + point.Y))));
            if (points.Length > 1) graphics.DrawLines(pen, points);
            else if (points.Length == 1) graphics.FillEllipse(dot, points[0].X - 2, points[0].Y - 2, 4, 4);
        }
    }

    // The readings are reference information, not commands. They live in a
    // child menu so the top level stays at the height of the actions the user
    // actually picks from; three long values used to stretch it well past them.
    // The parent stays enabled because a disabled item cannot open its child.
    private static void AddHardwareDetails(FluentContextMenuStrip menu, WidgetPerformanceSnapshot? snapshot)
    {
        var details = new FluentToolStripMenuItem("硬件信息");
        foreach (var line in WidgetMonitorPresentation.HardwareDetailLines(snapshot))
        {
            details.DropDownItems.Add(new FluentToolStripMenuItem(line) { Enabled = false });
        }
        if (details.DropDownItems.Count > 0) details.DropDownItems.Add(new Forms.ToolStripSeparator());
        details.DropDownItems.Add(new FluentToolStripMenuItem("物理内存：" + (snapshot is { MemoryTotalBytes: > 0 }
            ? $"{WidgetMonitorMetrics.Bytes(snapshot.MemoryUsedBytes)} / {WidgetMonitorMetrics.Bytes(snapshot.MemoryTotalBytes)}" : "等待采样")) { Enabled = false });
        details.DropDownItems.Add(new FluentToolStripMenuItem($"磁盘读 {WidgetMonitorMetrics.Speed(snapshot?.DiskReadBytesPerSecond)} · 写 {WidgetMonitorMetrics.Speed(snapshot?.DiskWriteBytesPerSecond)}") { Enabled = false });
        details.DropDownItems.Add(new FluentToolStripMenuItem($"固定磁盘容量：{WidgetMonitorMetrics.Bytes(snapshot?.DiskUsedBytes)} / {WidgetMonitorMetrics.Bytes(snapshot?.DiskTotalBytes)}") { Enabled = false });
        menu.Items.Add(details);
    }

    /// <summary>
    /// Builds the monitor section on its own so the menu shape can be asserted
    /// without a live desktop surface.
    /// </summary>
    internal static FluentContextMenuStrip BuildMonitorContextMenu(DesktopWidget widget)
    {
        var menu = new FluentContextMenuStrip();
        AddMonitorItems(menu, widget, action => action(), (_, _) => { }, _ => { }, (_, _) => { });
        return menu;
    }

    private void AddMonitorContextMenu(FluentContextMenuStrip menu, DesktopWidget widget) =>
        AddMonitorItems(menu, widget, TryAction,
            (target, mode) => _runtime.SetNetworkDisplayMode(target, mode),
            target => _ = _runtime.QueryWidgetPublicIpAsync(target),
            (target, direction) => _runtime.SetNetworkProcessSort(target, direction));

    private static void AddMonitorItems(FluentContextMenuStrip menu, DesktopWidget widget, Action<Action> run,
        Action<DesktopWidget, NetworkWidgetDisplayMode> setDisplayMode, Action<DesktopWidget> queryPublicIp,
        Action<DesktopWidget, NetworkProcessSort> setProcessSort)
    {
        if (widget.Kind == DesktopWidgetKind.Performance)
        {
            AddHardwareDetails(menu, widget.Performance);
        }
        else if (widget.Kind == DesktopWidgetKind.Network)
        {
            var styles = new FluentToolStripMenuItem("显示样式");
            // Offered styles only: the retired combined style still loads from
            // saved files but must not appear in the menu.
            foreach (var mode in DesktopWidgets.OfferedNetworkDisplayModes)
            {
                var item = new FluentToolStripMenuItem(DesktopWidgets.NetworkDisplayName(mode)) { Checked = widget.NetworkDisplayMode == mode };
                item.Click += (_, _) => setDisplayMode(widget, mode);
                styles.DropDownItems.Add(item);
            }
            menu.Items.Add(styles);
            var ranking = new FluentToolStripMenuItem("进程排行");
            foreach (var direction in Enum.GetValues<NetworkProcessSort>())
            {
                var item = new FluentToolStripMenuItem(direction == NetworkProcessSort.Upload ? "按上传排序" : "按下载排序")
                { Checked = widget.NetworkProcessSort == direction };
                item.Click += (_, _) => setProcessSort(widget, direction);
                ranking.DropDownItems.Add(item);
            }
            menu.Items.Add(ranking);
            menu.Items.Add(widget.IsPublicIpLoading ? "正在查询公网 IP…" : "查询 / 刷新公网 IP", null, (_, _) => queryPublicIp(widget));
            foreach (var (label, value) in new[] { ("IPv4", widget.Network?.IPv4), ("IPv6", widget.Network?.IPv6), ("公网 IP", widget.PublicIp), ("网关", widget.Network?.Gateway) })
            {
                var item = new FluentToolStripMenuItem("复制 " + label) { Enabled = !string.IsNullOrEmpty(value) };
                item.Click += (_, _) => { if (!string.IsNullOrEmpty(value)) run(() => Forms.Clipboard.SetText(value)); };
                menu.Items.Add(item);
            }
            menu.Items.Add("打开网络设置", null, (_, _) => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }));
        }
    }
}
