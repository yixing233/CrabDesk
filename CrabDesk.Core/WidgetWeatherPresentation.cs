namespace CrabDesk.Core;

/// <summary>Weather content and hit targets share the same radius-aware layout.</summary>
public static class WidgetWeatherPresentation
{
    public readonly record struct Layout(LayoutRect Body, LayoutRect Location, LayoutRect City, LayoutRect Locations,
        LayoutRect Menu, LayoutRect Temperature, LayoutRect Icon, LayoutRect Forecast, LayoutRect FeelsLike,
        LayoutRect Humidity, LayoutRect Wind, LayoutRect Status, LayoutRect Previous, LayoutRect Next, LayoutRect Counter);

    public static Layout Arrange(double width, double height, double cornerRadius, bool multipleCities)
    {
        var inset = 12 + Math.Clamp(cornerRadius, 0, 20) * .2;
        var headerInset = inset - 4;
        var body = new LayoutRect(inset, 36, width - inset * 2, height - 36 - inset);
        var wide = width >= 300;
        var overview = Math.Min(body.Width, 164);
        var compact = multipleCities && !wide;
        var temperatureHeight = compact ? 44 : 50;
        var forecastTop = body.Y + temperatureHeight + (compact ? 0 : 2);
        var metricTop = forecastTop + (compact ? 20 : 22);
        var column = (body.Width - 10) / 2;
        var footer = multipleCities ? 20 : 0;
        var statusHeight = multipleCities ? 22 : 28;
        var status = new LayoutRect(body.X, body.Y + body.Height - footer - statusHeight, body.Width, statusHeight);
        return new(body,
            new(headerInset, 6, 24, 24),
            new(headerInset + 28, 6, width - headerInset * 2 - 84, 24),
            new(width - headerInset - 56, 6, 24, 24),
            new(width - headerInset - 28, 6, 24, 24),
            new(body.X, body.Y, overview - 52, temperatureHeight),
            new(body.X + overview - 44, body.Y + 2, 44, compact ? 40 : 44),
            new(body.X, forecastTop, overview - 56, 20),
            new(body.X + overview - 52, forecastTop, 52, 20),
            wide ? new(body.X + body.Width - 128, body.Y + 4, 128, 24) : new(body.X, metricTop, column, 20),
            wide ? new(body.X + body.Width - 128, body.Y + 40, 128, 24) : new(body.X + column + 10, metricTop, column, 20),
            status,
            new(body.X, body.Y + body.Height - 18, 28, 18),
            new(body.X + body.Width - 28, body.Y + body.Height - 18, 28, 18),
            new(body.X + 28, body.Y + body.Height - 18, body.Width - 56, 18));
    }
}
