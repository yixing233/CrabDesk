namespace CrabDesk.Core;

/// <summary>Weather content and hit targets share the same radius-aware layout.</summary>
public static class WidgetWeatherPresentation
{
    public readonly record struct Layout(LayoutRect Body, LayoutRect Location, LayoutRect City, LayoutRect Locations,
        LayoutRect Menu, LayoutRect Temperature, LayoutRect Icon, LayoutRect Forecast, LayoutRect FeelsLike,
        LayoutRect Humidity, LayoutRect Wind, LayoutRect Status, LayoutRect Previous, LayoutRect Next, LayoutRect Counter)
    {
        public LayoutRect Description { get; init; }
        public IReadOnlyList<HourlyLayout> Hours { get; init; } = [];
    }

    public readonly record struct HourlyLayout(LayoutRect Time, LayoutRect Icon, LayoutRect Temperature);

    public static IReadOnlyList<WidgetHourlyWeather> UpcomingHours(WidgetWeatherSnapshot weather, DateTimeOffset now) =>
        weather.Hourly.Where(hour => hour.Time >= now && double.IsFinite(hour.Temperature))
            .OrderBy(hour => hour.Time).DistinctBy(hour => hour.Time).Take(6).ToArray();

    public static Layout Arrange(double width, double height, double cornerRadius, bool multipleCities)
    {
        var inset = 12 + Math.Clamp(cornerRadius, 0, 20) * .2;
        if (width >= 300) return ArrangeWide(width, height, inset, multipleCities);
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

    private static Layout ArrangeWide(double width, double height, double inset, bool multipleCities)
    {
        var right = width - inset;
        var bottom = height - inset;
        var cityLeft = Math.Max(inset + 216, right - 120);
        var hoursTop = 88d;
        var hoursBottom = Math.Min(158, bottom - (multipleCities ? 20 : 0));
        var hoursHeight = hoursBottom - hoursTop;
        var column = (width - inset * 2) / 6;
        var iconSize = Math.Min(30, hoursHeight - 36);
        var empty = new LayoutRect(inset, 20, 0, 0);
        return new(new(inset, 20, width - inset * 2, bottom - 20),
            // The wide card carries no header location buttons: the city name
            // switches cities and the hover menu owns the rest.
            empty, new(cityLeft, 28, right - cityLeft, 18),
            empty, new(right - 24, 4, 24, 24),
            // Icon artwork sits low in its canvas; align its visible center with the text ink.
            new(inset + 48, 24, 94, 46), new(inset, 22, 42, 42),
            new(inset + 146, 28, cityLeft - inset - 154, 38), empty, empty, empty,
            new(inset, 70, cityLeft - inset - 10, 16),
            new(inset, bottom - 18, 28, 18), new(right - 28, bottom - 18, 28, 18),
            new(inset + 28, bottom - 18, width - inset * 2 - 56, 18))
        {
            Description = new(cityLeft, 48, right - cityLeft, 18),
            Hours = Enumerable.Range(0, 6).Select(index =>
            {
                var left = inset + index * column;
                return new HourlyLayout(new(left, hoursTop, column, 14),
                    new(left + (column - iconSize) / 2, hoursTop + 16, iconSize, iconSize),
                    new(left, hoursBottom - 18, column, 18));
            }).ToArray()
        };
    }
}
