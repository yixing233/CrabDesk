using System.Drawing;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private static WidgetWeatherPresentation.Layout WeatherPresentation(DesktopWidget widget, double radius) =>
        WidgetWeatherPresentation.Arrange(widget.Bounds.Width, widget.Bounds.Height, radius, widget.Cities.Count > 1);

    private static RectangleF WeatherRectangle(DesktopWidget widget, LayoutRect rect) => new(
        (float)(widget.Bounds.X + rect.X), (float)(widget.Bounds.Y + rect.Y), (float)rect.Width, (float)rect.Height);

    internal static RectangleF WeatherLocationButtonRectangle(DesktopWidget widget, double radius = 8) =>
        WeatherRectangle(widget, WeatherPresentation(widget, radius).Location);

    internal static RectangleF WeatherLocationsButtonRectangle(DesktopWidget widget, double radius = 8) =>
        WeatherRectangle(widget, WeatherPresentation(widget, radius).Locations);

    internal static RectangleF WeatherCityButtonRectangle(DesktopWidget widget, double radius = 8) =>
        WeatherRectangle(widget, WeatherPresentation(widget, radius).City);

    internal readonly record struct WeatherLayout(RectangleF Temperature, RectangleF Icon, RectangleF Forecast,
        RectangleF FeelsLike, RectangleF Humidity, RectangleF Wind, RectangleF Status);

    internal static WeatherLayout GetWeatherLayout(DesktopWidget widget, double radius = 8)
    {
        var layout = WeatherPresentation(widget, radius);
        return new(WeatherRectangle(widget, layout.Temperature), WeatherRectangle(widget, layout.Icon),
            WeatherRectangle(widget, layout.Forecast), WeatherRectangle(widget, layout.FeelsLike),
            WeatherRectangle(widget, layout.Humidity), WeatherRectangle(widget, layout.Wind), WeatherRectangle(widget, layout.Status));
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
        var anchor = WeatherCityButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius);
        menu.Show(this, new Point(ToPixel(anchor.X), ToPixel(anchor.Bottom)));
        return menu;
    }

    internal static RectangleF WeatherEmptyLocationRectangle(DesktopWidget widget, double radius = 8)
    {
        var body = WeatherRectangle(widget, WeatherPresentation(widget, radius).Body);
        return new(body.X + 8, body.Y + 40, body.Width - 16, 28);
    }

    private bool IsWeatherLocationPoint(DesktopWidget widget, PointF point) => widget.Kind == DesktopWidgetKind.Weather &&
        (WeatherLocationButtonRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point) ||
         widget.Location is null && WeatherEmptyLocationRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius).Contains(point));

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
        var radius = _runtime.State.Settings.Appearance.CornerRadius;
        var button = WeatherLocationButtonRectangle(widget, radius);
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
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.MapPin, WeatherLocationsButtonRectangle(widget, radius), accent, 15);
        var city = WeatherCityButtonRectangle(widget, radius);
        WidgetText(graphics, widget.Location?.Name ?? widget.DisplayTitle,
            new(city.X, city.Y, city.Width - 14, city.Height), text, 13, bold: true);
        if (widget.Location is not null)
        {
            using var font = new Font(BoxAppearance.DefaultFontFamily, 13, FontStyle.Bold, GraphicsUnit.Pixel);
            var arrowX = Math.Min(city.Right - 12, city.X + graphics.MeasureString(widget.Location.Name, font).Width + 2);
            LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.ChevronsUpDown,
                new(arrowX, city.Y, 12, city.Height), text, 10);
        }
    }

    private static void DrawWeatherMetric(Graphics graphics, LucideRuntimeIcon icon, string value, RectangleF bounds, Color color)
    {
        LucideRuntimeIcons.Draw(graphics, icon, new(bounds.X, bounds.Y, 14, bounds.Height), color, 13);
        WidgetText(graphics, value, new(bounds.X + 19, bounds.Y, bounds.Width - 19, bounds.Height), color, 12.5f);
    }
}
