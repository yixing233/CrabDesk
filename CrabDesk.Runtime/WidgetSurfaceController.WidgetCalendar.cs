using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected Bitmap? _calendarToolTipBitmap;
    protected RectangleF _calendarToolTipBounds;
    protected Guid? _calendarToolTipWidgetId;
    protected DateOnly? _hoveredCalendarDate;
    protected readonly Forms.Timer _calendarToolTipTimer = new() { Interval = 450 };
    protected string _calendarToolTipText = string.Empty;
    protected readonly Guid _calendarToolTipBackdropId = Guid.NewGuid();
    protected readonly Forms.Timer _calendarToolTipMotion = new() { Interval = DesktopAnimationFrameClock.IntervalMilliseconds };
    protected long _calendarToolTipStarted;
    protected double _calendarToolTipProgress = 1;

    protected RectangleF CalendarToolTipVisualBounds
    {
        get
        {
            var bounds = _calendarToolTipBounds;
            bounds.Offset(0, (float)(8 * Math.Pow(1 - _calendarToolTipProgress, 3)));
            return bounds;
        }
    }

    protected float CalendarToolTipOpacity => (float)(_calendarToolTipProgress * _calendarToolTipProgress * (3 - 2 * _calendarToolTipProgress));

    protected void AnimateCalendarToolTip(object? sender, EventArgs args)
    {
        using var widgetView = EnterWidgetView();
        _calendarToolTipProgress = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_calendarToolTipStarted).TotalMilliseconds / 260, 0, 1);
        if (_calendarToolTipProgress == 1) _calendarToolTipMotion.Stop();
        RequestVisualLayerRender();
    }

    protected void ShowCalendarToolTip(object? sender, EventArgs args)
    {
        using var widgetView = EnterWidgetView();
        _calendarToolTipTimer.Stop();
        if (!Visible || !HostHasActiveWidgets || _pressedWidget is not null || _hoveredCalendarDate is not { } date) return;
        var point = ToDip(PointToClient(Forms.Cursor.Position));
        var widget = WidgetAt(point);
        if (widget is null || widget.Id != _calendarToolTipWidgetId || CalendarDateAtPoint(widget, WidgetMonth(widget), point) != date) return;
        PresentCalendarToolTip(widget, date, point);
    }

    protected void PresentCalendarToolTip(DesktopWidget widget, DateOnly date, PointF point)
    {
        using var widgetView = EnterWidgetView();
        var details = CalendarDateDetails.ForDate(date);
        const float width = 212;
        const float padding = 12;
        var appearance = ResolveWidgetSurface(widget);
        var background = ParseOpaqueColor(appearance.Background);
        var text = ResolveAutoTextColor(background);
        var muted = Color.FromArgb(190, text);
        var accent = ParseOpaqueColor(appearance.Accent);
        var badges = details.Festivals.Concat(string.IsNullOrEmpty(details.SolarTerm) ? [] : new[] { details.SolarTerm }).Distinct().ToArray();
        using var badgeFont = WidgetTextRendering.CreateFont(widget, 11.5f);
        var chipLayout = new List<(string Text, RectangleF Bounds)>();
        using (var measure = Graphics.FromHwnd(Handle))
        {
            var chipX = padding; var chipY = 64f;
            foreach (var badge in badges)
            {
                var chipWidth = Math.Min(width - padding * 2, measure.MeasureString(badge, badgeFont).Width + 16);
                if (chipX > padding && chipX + chipWidth > width - padding) { chipX = padding; chipY += 28; }
                chipLayout.Add((badge, new(chipX, chipY, chipWidth, 22)));
                chipX += chipWidth + 6;
            }
        }
        var height = chipLayout.Count == 0 ? 66f : chipLayout[^1].Bounds.Bottom + 12;
        const float shadow = 5;
        using var bitmap = DesktopLayerBitmapFactory.Create(ToPixel(width + shadow * 2), ToPixel(height + shadow * 2));
        using (var graphics = Graphics.FromImage(bitmap))
        {
            using var typography = WidgetTextRendering.Begin(graphics, widget);
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform((float)_scale, (float)_scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var card = new RectangleF(shadow, shadow, width, height);
            var radius = (float)Math.Min(12, WidgetCornerRadius);
            using var path = RoundedRectangle(card, radius);
            var shadowClip = graphics.Save();
            graphics.SetClip(path, CombineMode.Exclude);
            for (var spread = 4; spread >= 1; spread--)
            {
                var shadowBounds = RectangleF.Inflate(card, spread, spread); shadowBounds.Offset(0, 1);
                using var shadowPath = RoundedRectangle(shadowBounds, radius + spread);
                using var shadowInk = new SolidBrush(Color.FromArgb(5, Color.Black));
                graphics.FillPath(shadowInk, shadowPath);
            }
            graphics.Restore(shadowClip);
            using var fill = new SolidBrush(ApplyOpacity(background, appearance.Opacity));
            graphics.FillPath(fill, path);
            graphics.TranslateTransform(shadow, shadow);
            WidgetText(graphics, date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture), new(padding, 10, 140, 22), text, 14, bold: true);
            WidgetText(graphics, date.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN")), new(width - padding - 40, 10, 40, 22), muted, 11, centered: true);
            WidgetText(graphics, "农历", new(padding, 34, 28, 20), muted, 11);
            WidgetText(graphics, details.LunarDate.Length > 0 ? details.LunarDate : "暂无农历信息", new(padding + 34, 34, width - padding * 2 - 34, 20), text, 12);
            foreach (var badge in chipLayout)
            {
                using var chip = RoundedRectangle(badge.Bounds, 6);
                using var tint = new SolidBrush(Color.FromArgb(25, accent));
                graphics.FillPath(tint, chip);
                WidgetText(graphics, badge.Text, badge.Bounds, accent, 11.5f, centered: true);
            }
        }
        var area = _monitor.PixelWorkArea;
        var cursor = WidgetPointToScreen(new(point.X, point.Y));
        var x = cursor.X + ToPixel(12);
        var y = cursor.Y + ToPixel(18);
        if (x + bitmap.Width > area.X + area.Width) x = cursor.X - bitmap.Width - ToPixel(12);
        if (y + bitmap.Height > area.Y + area.Height) y = cursor.Y - bitmap.Height - ToPixel(12);
        var location = new Point((int)Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - bitmap.Width)),
            (int)Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - bitmap.Height)));
        var client = ToDip(PointToClient(location));
        _calendarToolTipBitmap?.Dispose();
        _calendarToolTipBitmap = (Bitmap)bitmap.Clone();
        _calendarToolTipBounds = new(client.X, client.Y,
            (float)(bitmap.Width / _scale), (float)(bitmap.Height / _scale));
        AccessibleDescription = details.ToolTipText;
        _calendarToolTipMotion.Stop();
        _calendarToolTipProgress = _runtime.State.Settings.Appearance.AnimationEnabled && System.Windows.SystemParameters.ClientAreaAnimation ? 0 : 1;
        _calendarToolTipStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_calendarToolTipProgress < 1) _calendarToolTipMotion.Start();
        RequestVisualLayerRender();
    }

    protected void DrawCalendarToolTip(Graphics graphics)
    {
        using var widgetView = EnterWidgetView();
        if (_calendarToolTipBitmap is not { } bitmap) return;
        var bounds = CalendarToolTipVisualBounds;
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = CalendarToolTipOpacity });
        graphics.DrawImage(bitmap, new[] { bounds.Location, new PointF(bounds.Right, bounds.Top), new PointF(bounds.Left, bounds.Bottom) },
            new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel, attributes);
    }

    protected static RectangleF CalendarBodyRectangle(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        body.Width = Math.Min(body.Width, 164);
        return body;
    }

    internal static RectangleF CalendarDayRectangle(DesktopWidget widget, int index)
    {
        var body = CalendarBodyRectangle(widget);
        var width = body.Width / 7; var height = (body.Height - 14) / 6;
        return new(body.X + index % 7 * width, body.Y + 14 + index / 7 * height, width, height);
    }

    internal static DateOnly? CalendarDateAtPoint(DesktopWidget widget, DateOnly month, PointF point)
    {
        if (widget.Kind != DesktopWidgetKind.Calendar) return null;
        var body = CalendarBodyRectangle(widget);
        var grid = new RectangleF(body.X, body.Y + 14, body.Width, body.Height - 14);
        if (grid.Width <= 0 || grid.Height <= 0 || !grid.Contains(point)) return null;
        var column = Math.Clamp((int)((point.X - grid.X) / (grid.Width / 7)), 0, 6);
        var row = Math.Clamp((int)((point.Y - grid.Y) / (grid.Height / 6)), 0, 5);
        return DesktopWidgets.CalendarDays(month)[row * 7 + column];
    }

    protected void DrawCalendarUpcomingEvents(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        using var widgetView = EnterWidgetView();
        var bounds = WidgetRectangle(widget);
        if (bounds.Width < 300) return;
        var x = bounds.X + 192;
        var width = bounds.Width - 200;
        WidgetText(graphics, "近期节日 · 节气", new(x, bounds.Y, width - 28, 28), muted, 11.5f, bold: true);
        using var divider = new Pen(Color.FromArgb(24, text), 1);
        graphics.DrawLine(divider, x - 8, bounds.Y + 38, x - 8, bounds.Bottom - 10);
        var events = CalendarDateDetails.GetUpcomingEvents(DateOnly.FromDateTime(DateTime.Today));
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            var y = bounds.Y + 36 + index * 34;
            WidgetText(graphics, item.Name, new(x, y, width - 46, 18), text, 12);
            WidgetText(graphics, item.DateLabel + " · " + item.KindLabel, new(x, y + 18, width - 46, 14), muted, 9.5f);
            var countdown = new RectangleF(x + width - 40, y, 40, 32);
            if (item.DaysRemaining == 0) WidgetText(graphics, "今天", countdown, accent, 12, bold: true, centered: true);
            else
            {
                WidgetText(graphics, item.DaysRemaining.ToString(CultureInfo.InvariantCulture), new(countdown.X, y, 40, 20), accent, 18, bold: true, centered: true);
                WidgetText(graphics, "天后", new(countdown.X, y + 20, 40, 12), muted, 9, centered: true);
            }
        }
        if (events.Count == 0) WidgetText(graphics, "暂无近期日历", new(x, bounds.Y + 40, width, 24), muted, 11);
    }

    protected void UpdateCalendarHover(DesktopWidget? widget, PointF point)
    {
        using var widgetView = EnterWidgetView();
        var date = widget is { Kind: DesktopWidgetKind.Calendar } ? CalendarDateAtPoint(widget, WidgetMonth(widget), point) : null;
        var id = date is not null ? widget!.Id : (Guid?)null;
        if (_calendarToolTipWidgetId == id && _hoveredCalendarDate == date) return;
        HideCalendarHover();
        if (date is not { } hovered) return;
        _calendarToolTipWidgetId = id; _hoveredCalendarDate = hovered;
        _calendarToolTipText = CalendarDateDetails.ForDate(hovered).ToolTipText;
        _calendarToolTipTimer.Start();
        RequestVisualLayerRender();
    }

    protected void HideCalendarHover()
    {
        using var widgetView = EnterWidgetView();
        if (_hoveredCalendarDate is null && _calendarToolTipBitmap is null) return;
        _calendarToolTipTimer.Stop(); _calendarToolTipText = string.Empty;
        _calendarToolTipMotion.Stop();
        _calendarToolTipWidgetId = null; _hoveredCalendarDate = null;
        _calendarToolTipBitmap?.Dispose(); _calendarToolTipBitmap = null;
        _calendarToolTipBounds = RectangleF.Empty;
        AccessibleDescription = null;
        if (!_resourcesDisposed && !IsDisposed) RequestVisualLayerRender();
    }
}
