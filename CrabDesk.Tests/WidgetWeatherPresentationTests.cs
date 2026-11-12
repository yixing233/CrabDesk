using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetWeatherPresentationTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(20, false)] [InlineData(0, true)] [InlineData(20, true)]
    public void WideSummaryHoursAndCityControlsFitWithoutOverlapping(double radius, bool multipleCities)
    {
        var layout = WidgetWeatherPresentation.Arrange(372, 180, radius, multipleCities);
        Assert.Equal(6, layout.Hours.Count);
        Assert.Equal(layout.City.Y, layout.Forecast.Y);
        Assert.Equal(layout.City.Height, layout.Description.Height);
        Assert.Equal(layout.Temperature.Y + layout.Temperature.Height / 2, layout.Icon.Y + layout.Icon.Height / 2);
        Assert.Equal(layout.Temperature.Y + layout.Temperature.Height / 2, layout.Forecast.Y + layout.Forecast.Height / 2);
        // The wide card has no header location controls, so those slots collapse.
        Assert.False(layout.Location.Width > 0);
        Assert.False(layout.Locations.Width > 0);
        var regions = new List<LayoutRect> { layout.City, layout.Menu,
            layout.Temperature, layout.Icon, layout.Forecast, layout.Description, layout.Status };
        regions.AddRange(layout.Hours.SelectMany(hour => new[] { hour.Time, hour.Icon, hour.Temperature }));
        if (multipleCities) regions.AddRange([layout.Previous, layout.Counter, layout.Next]);
        foreach (var rect in regions)
        {
            Assert.True(rect.Width > 0 && rect.Height > 0);
            Assert.InRange(rect.X, 0, 372 - rect.Width);
            Assert.InRange(rect.Y, 0, 180 - rect.Height);
        }
        for (var index = 0; index < regions.Count; index++)
            for (var other = index + 1; other < regions.Count; other++)
                Assert.False(regions[index].Intersects(regions[other]), $"{regions[index]} intersects {regions[other]}");
    }

    [Fact]
    public void ForecastUsesNextSixRealHoursInChronologicalOrder()
    {
        var now = new DateTimeOffset(2026, 11, 12, 17, 30, 0, TimeSpan.FromHours(8));
        var future = Enumerable.Range(1, 8).Select(index => new WidgetHourlyWeather(now.AddMinutes(30).AddHours(index - 1),
            25 - index, "小雨", null)).ToArray();
        var snapshot = new WidgetWeatherSnapshot(27, 24, 29, 0, now)
        { Hourly = [future[3], new(now.AddHours(-1), 26, "晴", null), .. future.Reverse(), future[0], new(now.AddMinutes(1), double.NaN, "晴", null)] };
        Assert.Equal(future.Take(6), WidgetWeatherPresentation.UpcomingHours(snapshot, now));
        Assert.Empty(WidgetWeatherPresentation.UpcomingHours(snapshot, now.AddDays(2)));
        Assert.Empty(WidgetWeatherPresentation.UpcomingHours(snapshot with { Hourly = [] }, now));
    }

    [Fact]
    public void HourlyIconUsesForecastConditionAndForecastTime()
    {
        var now = new DateTimeOffset(2026, 11, 12, 17, 30, 0, TimeSpan.FromHours(8));
        var current = new WidgetWeatherSnapshot(27, 24, 29, 0, now)
        { Condition = "阴", Sunrise = new(2026, 11, 12, 6, 0, 0, now.Offset), Sunset = new(2026, 11, 12, 18, 0, 0, now.Offset) };
        Assert.Equal("weather-rain", WidgetIconCatalog.GetHourlyWeatherName(new(now.AddHours(1), 26, "小雨", null), current));
        Assert.Equal("weather-clear-night", WidgetIconCatalog.GetHourlyWeatherName(new(now.AddHours(2), 25, "晴", null), current));
    }
}
