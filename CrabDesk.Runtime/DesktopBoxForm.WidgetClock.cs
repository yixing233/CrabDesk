using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    internal static void DrawClockWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted, DateTime now)
    {
        var local = WidgetClockPresentation.Face(body.Width, body.Height);
        if (local.Width <= 0) return;
        var face = new LayoutRect(body.X + local.X, body.Y + local.Y, local.Width, local.Height);
        var circle = new RectangleF((float)face.X, (float)face.Y, (float)face.Width, (float)face.Height);
        using var fill = new SolidBrush(text.GetBrightness() > .5f ? Color.FromArgb(26, 26, 26) : Color.FromArgb(250, 250, 250));
        using var rim = new Pen(Color.FromArgb(35, text), 1);
        graphics.FillEllipse(fill, circle); graphics.DrawEllipse(rim, circle);
        PointF Point(double degrees, double fraction)
        {
            var point = WidgetClockPresentation.PointOnFace(face, degrees, fraction);
            return new((float)point.X, (float)point.Y);
        }
        for (var tick = 0; tick < 60; tick++)
        {
            var major = tick % 5 == 0;
            using var pen = new Pen(Color.FromArgb(major ? 150 : 65, text), major ? 1.5f : .8f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(pen, Point(tick * 6, major ? .79 : .85), Point(tick * 6, .9));
        }
        var labelSize = (float)Math.Clamp(face.Width * .085, 8, 12);
        var boxSize = (float)(face.Width * .2);
        foreach (var hour in new[] { 12, 3, 6, 9 })
        {
            var point = Point(hour * 30, .62);
            WidgetText(graphics, hour.ToString(CultureInfo.InvariantCulture), new(point.X - boxSize / 2, point.Y - boxSize / 2, boxSize, boxSize),
                Color.FromArgb(210, text), labelSize, centered: true);
        }
        var hands = WidgetClockPresentation.Hands(now);
        var center = Point(0, 0);
        using var hourPen = new Pen(text, (float)Math.Clamp(face.Width * .027, 2.2, 4)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var minutePen = new Pen(Color.FromArgb(230, text), (float)Math.Clamp(face.Width * .018, 1.6, 2.8)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(hourPen, center, Point(hands.Hour, .46));
        graphics.DrawLine(minutePen, center, Point(hands.Minute, .72));
        if (widget.ShowSeconds)
        {
            using var secondPen = new Pen(Color.FromArgb(160, text), 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(secondPen, Point(hands.Second, -.17), Point(hands.Second, .82));
        }
        var pivot = (float)Math.Clamp(face.Width * .024, 2.2, 3.5);
        using var pivotFill = new SolidBrush(text);
        graphics.FillEllipse(pivotFill, center.X - pivot, center.Y - pivot, pivot * 2, pivot * 2);
        using var pivotCenter = new SolidBrush(ResolveAutoTextColor(text));
        graphics.FillEllipse(pivotCenter, center.X - 1, center.Y - 1, 2, 2);
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        if (WidgetClockPresentation.IsLandscape(body.Width, body.Height))
        {
            var left = (float)(face.X + face.Width + 18); var available = body.Right - left;
            WidgetText(graphics, now.ToString(widget.ShowSeconds ? "HH:mm:ss" : "HH:mm", culture),
                new(left, body.Y + body.Height / 2 - 29, available, 34), text, 26, bold: true);
            WidgetText(graphics, now.ToString("M月d日 · ddd", culture),
                new(left, body.Y + body.Height / 2 + 12, available, 20), muted, 10.5f);
        }
    }
}
