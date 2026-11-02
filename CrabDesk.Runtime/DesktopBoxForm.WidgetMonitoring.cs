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

    private LayoutRect MonitorBody(DesktopWidget widget) => WidgetMonitorPresentation.Body(widget.Bounds.Width,
        widget.Bounds.Height, _runtime.State.Settings.Appearance.CornerRadius);

    private void DrawPerformanceWidget(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var body = MonitorBody(widget); var wide = body.Width >= 300;
        var rows = WidgetMonitorPresentation.PerformanceMetrics(widget.Performance);
        for (var index = 0; index < rows.Length; index++)
        {
            var metric = rows[index]; var cell = MonitorRectangle(widget, WidgetMonitorPresentation.PerformanceCell(body, index));
            WidgetText(graphics, metric.Label, new(cell.X, cell.Y, cell.Width - 52, 20), muted, 12);
            WidgetText(graphics, metric.Value, new(cell.Right - 52, cell.Y, 52, 20), text, 13.5f, bold: true, centered: true);
            if (wide) WidgetText(graphics, metric.Detail, new(cell.X, cell.Y + 24, cell.Width, 20), muted, 10.5f);
            var track = new RectangleF(cell.X, cell.Bottom - (wide ? 4 : 7), cell.Width, 3);
            using var path = RoundedRectangle(track, 1.5f);
            using var background = new SolidBrush(Color.FromArgb(24, text)); graphics.FillPath(background, path);
            if (metric.Percentage is { } percent && percent > 0 && double.IsFinite(percent))
            {
                var fillBounds = track; fillBounds.Width *= (float)Math.Clamp(percent / 100, 0, 1);
                using var fillPath = RoundedRectangle(fillBounds, Math.Min(1.5f, fillBounds.Width / 2));
                using var fill = new SolidBrush(accent); graphics.FillPath(fill, fillPath);
            }
        }
    }

    private RectangleF NetworkPublicIpRectangle(DesktopWidget widget) => widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Details
        ? MonitorRectangle(widget, WidgetMonitorPresentation.NetworkFields(MonitorBody(widget)).PublicIp) : RectangleF.Empty;

    private void DrawNetworkWidget(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        if (widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Speeds)
        { DrawNetworkSpeeds(graphics, widget, text, muted, accent); return; }
        if (widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Chart)
        { DrawNetworkChart(graphics, widget, text, muted, accent); return; }
        var layout = WidgetMonitorPresentation.NetworkFields(MonitorBody(widget)); var snapshot = widget.Network;
        void Speed(LayoutRect rect, string label, LucideRuntimeIcon icon, double? bytes)
        {
            var bounds = MonitorRectangle(widget, rect);
            LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y, 14, 14), accent, 12);
            WidgetText(graphics, label, new(bounds.X + 19, bounds.Y, bounds.Width - 19, 14), muted, 10.5f);
            WidgetText(graphics, WidgetMonitorMetrics.Speed(bytes), new(bounds.X, bounds.Y + 14, bounds.Width, 26), text, 21, bold: true);
        }
        Speed(layout.Download, "下载", LucideRuntimeIcon.ArrowDown, snapshot?.DownloadBytesPerSecond);
        Speed(layout.Upload, "上传", LucideRuntimeIcon.ArrowUp, snapshot?.UploadBytesPerSecond);
        WidgetText(graphics, WidgetMonitorPresentation.ConnectionText(snapshot), MonitorRectangle(widget, layout.Connection), muted, 11.5f);
        var latency = snapshot?.LatencyMilliseconds is { } milliseconds ? $"延迟 {milliseconds} ms" :
            "延迟 " + (snapshot?.LatencyStatus ?? "等待测量");
        WidgetText(graphics, latency, MonitorRectangle(widget, layout.Latency), muted, 11);
        WidgetText(graphics, "IPv4  " + (string.IsNullOrEmpty(snapshot?.IPv4) ? "—" : snapshot.IPv4), MonitorRectangle(widget, layout.IPv4), text, 11.5f);
        WidgetText(graphics, "IPv6  " + (string.IsNullOrEmpty(snapshot?.IPv6) ? "—" : snapshot.IPv6), MonitorRectangle(widget, layout.IPv6), muted, 11);
        var button = NetworkPublicIpRectangle(widget);
        using var buttonPath = RoundedRectangle(button, 6);
        using var buttonFill = new SolidBrush(Color.FromArgb(14, text)); graphics.FillPath(buttonFill, buttonPath);
        var publicIp = widget.IsPublicIpLoading ? "查询中…" : !string.IsNullOrEmpty(widget.PublicIpError) ? widget.PublicIpError :
            string.IsNullOrEmpty(widget.PublicIp) ? "点击查询" : widget.PublicIp;
        WidgetText(graphics, "公网 IP  " + publicIp, new(button.X + 8, button.Y, button.Width - 34, button.Height), muted, 11.5f);
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.RefreshCw, new(button.Right - 24, button.Y, 18, button.Height),
            widget.IsPublicIpLoading ? muted : accent, 12);
    }

    private static Color NetworkUploadColor(Color text) => text.R > 128 ? Color.FromArgb(88, 211, 187) : Color.FromArgb(0, 116, 100);

    private void DrawNetworkSpeed(Graphics graphics, DesktopWidget widget, LayoutRect rect, double? bytes,
        bool upload, bool compact, Color text, Color muted, Color ink)
    {
        var bounds = MonitorRectangle(widget, rect);
        var icon = upload ? LucideRuntimeIcon.ArrowUp : LucideRuntimeIcon.ArrowDown;
        if (compact)
        {
            LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y, 14, bounds.Height), ink, 14);
            WidgetText(graphics, WidgetMonitorMetrics.Speed(bytes), new(bounds.X + 22, bounds.Y, bounds.Width - 22, bounds.Height), text, 18, bold: true);
            return;
        }
        LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y, 14, 14), ink, 12);
        WidgetText(graphics, upload ? "上传" : "下载", new(bounds.X + 19, bounds.Y, bounds.Width - 19, 14), muted, 10.5f);
        WidgetText(graphics, WidgetMonitorMetrics.Speed(bytes), new(bounds.X, bounds.Y + 14, bounds.Width, 26), text, 21, bold: true);
    }

    private void DrawNetworkSpeeds(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var fields = WidgetNetworkGraph.SpeedLayout(widget.Bounds.Width, widget.Bounds.Height,
            _runtime.State.Settings.Appearance.CornerRadius);
        var compact = widget.Bounds.Width < 300;
        DrawNetworkSpeed(graphics, widget, fields.Download, widget.Network?.DownloadBytesPerSecond, false, compact, text, muted, accent);
        DrawNetworkSpeed(graphics, widget, fields.Upload, widget.Network?.UploadBytesPerSecond, true, compact, text, muted, NetworkUploadColor(text));
    }

    private void DrawNetworkChart(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var layout = WidgetNetworkGraph.ChartLayout(widget.Bounds.Width, widget.Bounds.Height,
            _runtime.State.Settings.Appearance.CornerRadius);
        var uploadColor = NetworkUploadColor(text);
        DrawNetworkSpeed(graphics, widget, layout.Download, widget.Network?.DownloadBytesPerSecond, false, false, text, muted, accent);
        DrawNetworkSpeed(graphics, widget, layout.Upload, widget.Network?.UploadBytesPerSecond, true, false, text, muted, uploadColor);
        var plot = MonitorRectangle(widget, layout.Plot);
        var graph = WidgetNetworkGraph.Project(widget.NetworkHistory, layout.Plot, DateTimeOffset.Now);
        using var grid = new Pen(Color.FromArgb(24, text), 1) { DashStyle = DashStyle.Dash };
        for (var row = 0; row <= 2; row++)
        {
            var y = plot.Y + plot.Height * row / 2;
            graphics.DrawLine(grid, plot.X, y, plot.Right, y);
        }
        void DrawSeries(System.Collections.Generic.IReadOnlyList<WidgetNetworkGraph.Point[]> segments, Color color)
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
        DrawSeries(graph.Download, accent); DrawSeries(graph.Upload, uploadColor);
        if (graph.Download.Count == 0 && graph.Upload.Count == 0)
            WidgetText(graphics, widget.Network is { IsConnected: false } ? "未连接网络" : "正在积累数据…", plot, muted, 11, centered: true);
        var footer = MonitorRectangle(widget, layout.Footer);
        WidgetText(graphics, "最近 60 秒", new(footer.X, footer.Y, footer.Width / 2, footer.Height), muted, 10);
        WidgetText(graphics, "上限 " + WidgetMonitorMetrics.Speed(graph.Maximum),
            new(footer.X + footer.Width / 2, footer.Y, footer.Width / 2, footer.Height), muted, 10, rightAligned: true);
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
        AddMonitorItems(menu, widget, action => action(), (_, _) => { }, _ => { });
        return menu;
    }

    private void AddMonitorContextMenu(FluentContextMenuStrip menu, DesktopWidget widget) =>
        AddMonitorItems(menu, widget, TryAction,
            (target, mode) => _runtime.SetNetworkDisplayMode(target, mode),
            target => _ = _runtime.QueryWidgetPublicIpAsync(target));

    private static void AddMonitorItems(FluentContextMenuStrip menu, DesktopWidget widget, Action<Action> run,
        Action<DesktopWidget, NetworkWidgetDisplayMode> setDisplayMode, Action<DesktopWidget> queryPublicIp)
    {
        if (widget.Kind == DesktopWidgetKind.Performance)
        {
            AddHardwareDetails(menu, widget.Performance);
        }
        else if (widget.Kind == DesktopWidgetKind.Network)
        {
            var styles = new FluentToolStripMenuItem("显示样式");
            foreach (var mode in Enum.GetValues<NetworkWidgetDisplayMode>())
            {
                var item = new FluentToolStripMenuItem(DesktopWidgets.NetworkDisplayName(mode)) { Checked = widget.NetworkDisplayMode == mode };
                item.Click += (_, _) => setDisplayMode(widget, mode);
                styles.DropDownItems.Add(item);
            }
            menu.Items.Add(styles);
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
