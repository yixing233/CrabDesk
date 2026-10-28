using System.Drawing;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    internal static RectangleF WeatherLocationButtonRectangle(DesktopWidget widget) =>
        new((float)widget.Bounds.X + 4, (float)widget.Bounds.Y + 2, 24, 24);

    internal static RectangleF WeatherLocationsButtonRectangle(DesktopWidget widget) =>
        new((float)widget.Bounds.X + (float)widget.Bounds.Width - 60, (float)widget.Bounds.Y + 2, 24, 24);

    internal static RectangleF WeatherCityButtonRectangle(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        return new(body.X, body.Y, body.Width - (widget.Weather?.FeelsLike.HasValue == true ? 60 : 0), 18);
    }

    private FluentContextMenuStrip ShowWeatherCityMenu(DesktopWidget widget)
    {
        var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)_scale };
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Closed += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose); else menu.Dispose(); };
        foreach (var city in widget.Cities)
        {
            var item = new FluentToolStripMenuItem(city.DisplayName) { Checked = city.Id == widget.ActiveCityId };
            item.Click += async (_, _) => { _runtime.SelectWidgetCity(widget, city.Id); await _runtime.RefreshWidgetWeatherAsync(widget, force: false); };
            menu.Items.Add(item);
        }
        if (widget.Cities.Count > 0) menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("添加 / 管理位置", null, (_, _) => _runtime.OpenWidgetWeatherLocations(widget));
        var anchor = WeatherCityButtonRectangle(widget);
        menu.Show(this, new Point(ToPixel(anchor.X), ToPixel(anchor.Bottom)));
        return menu;
    }

    internal static RectangleF WeatherEmptyLocationRectangle(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        return new(body.X + 8, body.Y + 40, body.Width - 16, 28);
    }

    private static bool IsWeatherLocationPoint(DesktopWidget widget, PointF point) => widget.Kind == DesktopWidgetKind.Weather &&
        (WeatherLocationButtonRectangle(widget).Contains(point) || widget.Location is null && WeatherEmptyLocationRectangle(widget).Contains(point));

    private void LocateWeatherFromCard(DesktopWidget widget)
    {
        if (widget.IsLocating) return;
        var body = WidgetBodyRectangle(widget);
        var location = PointToScreen(new Point(ToPixel(body.X), ToPixel(body.Y)));
        var appearance = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
        var background = ParseOpaqueColor(appearance.Background);
        var anchor = new WidgetLocationAnchor(new(location, new Size(ToPixel(body.Width), ToPixel(body.Height))), background, ResolveAutoTextColor(background));
        _ = _runtime.LocateWidgetWeatherAsync(widget, anchor);
    }

    private void DrawWeatherLocationButton(Graphics graphics, DesktopWidget widget, Color text, Color accent)
    {
        var button = WeatherLocationButtonRectangle(widget);
        var hovered = _hoveredWeatherLocationId == widget.Id;
        if (hovered)
        {
            using var path = RoundedRectangle(RectangleF.Inflate(button, -2, -2), 6);
            using var fill = new SolidBrush(Color.FromArgb(20, text));
            graphics.FillPath(fill, path);
        }
        var color = widget.IsLocating || hovered ? accent : text;
        using var pen = new Pen(Color.FromArgb(widget.IsLocating || hovered ? 240 : 190, color), 1.3f);
        var x = button.X + button.Width / 2;
        var y = button.Y + button.Height / 2;
        graphics.DrawEllipse(pen, x - 4.5f, y - 4.5f, 9, 9);
        graphics.DrawLine(pen, x - 8, y, x - 4.5f, y);
        graphics.DrawLine(pen, x + 4.5f, y, x + 8, y);
        graphics.DrawLine(pen, x, y - 8, x, y - 4.5f);
        graphics.DrawLine(pen, x, y + 4.5f, x, y + 8);
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.MapPin, WeatherLocationsButtonRectangle(widget), accent, 14);
    }

    private static void DrawWeatherMetric(Graphics graphics, LucideRuntimeIcon icon, string value, RectangleF bounds, Color color)
    {
        LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y, 14, bounds.Height), color, 12);
        WidgetText(graphics, value, new(bounds.X + 19, bounds.Y, bounds.Width - 19, bounds.Height), color, 10.5f);
    }
}
