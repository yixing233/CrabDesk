using CrabDesk.Core;

namespace CrabDesk.Tests;

public class WidgetIconCatalogTests
{
    [Theory]
    [InlineData(DesktopWidgetKind.Clock, "clock")]
    [InlineData(DesktopWidgetKind.Calendar, "calendar")]
    [InlineData(DesktopWidgetKind.Note, "note")]
    [InlineData(DesktopWidgetKind.Todo, "todo")]
    [InlineData(DesktopWidgetKind.Translation, "translation")]
    [InlineData(DesktopWidgetKind.Weather, "weather")]
    [InlineData(DesktopWidgetKind.Performance, "performance")]
    [InlineData(DesktopWidgetKind.Network, "network")]
    [InlineData(DesktopWidgetKind.ControlCenter, "control-center")]
    [InlineData(DesktopWidgetKind.Lyrics, "lyrics")]
    [InlineData(DesktopWidgetKind.FileShelf, "file-shelf")]
    public void WidgetIdentityMatchesAsset(DesktopWidgetKind kind, string name)
        => Assert.Equal(name, WidgetIconCatalog.GetName(kind));

    [Theory]
    [InlineData("晴", "weather-clear")]
    [InlineData("大部晴朗", "weather-partly-cloudy")]
    [InlineData("多云", "weather-partly-cloudy")]
    [InlineData("阴", "weather-cloudy")]
    [InlineData("雨", "weather-rain")]
    [InlineData("雨夹雪", "weather-snow")]
    [InlineData("雷雨", "weather-thunderstorm")]
    [InlineData("雾", "weather-fog")]
    [InlineData("霾", "weather-fog")]
    [InlineData("雷阵雨伴有冰雹", "weather-hail")]
    [InlineData("天气未知", "weather-unavailable")]
    public void WeatherUsesMatchingShape(string condition, string name)
        => Assert.Equal(name, WidgetIconCatalog.GetWeatherName(new(20, 10, 25, 0, DateTimeOffset.UtcNow) { Condition = condition }, DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(5, "weather-clear-night")]
    [InlineData(6, "weather-clear")]
    [InlineData(12, "weather-clear")]
    [InlineData(18, "weather-clear-night")]
    [InlineData(23, "weather-clear-night")]
    public void ClearWeatherUsesCitySunriseAndSunset(int hour, string name)
    {
        var offset = TimeSpan.FromHours(8);
        var weather = new WidgetWeatherSnapshot(20, 10, 25, 0, DateTimeOffset.UtcNow) { Condition = "晴",
            Sunrise = new(2026, 11, 10, 6, 0, 0, offset), Sunset = new(2026, 11, 10, 18, 0, 0, offset) };
        var now = new DateTimeOffset(2026, 11, 10, hour, 0, 0, offset).ToUniversalTime();
        Assert.Equal(name, WidgetIconCatalog.GetWeatherName(weather, now));
    }

    [Fact]
    public void MissingOrStaleSolarDataKeepsClearFallback()
    {
        var now = new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.FromHours(8));
        var morning = new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("weather-clear", WidgetIconCatalog.GetWeatherName(new(20, 10, 25, 0, now) { Condition = "晴" }, now));
        Assert.Equal("weather-clear", WidgetIconCatalog.GetWeatherName(new(20, 10, 25, 0, now) { Condition = "晴",
            Sunrise = morning.AddDays(-1), Sunset = morning.AddDays(-1).AddHours(12) }, now));
        Assert.Equal("weather-clear", WidgetIconCatalog.GetWeatherName(new(20, 10, 25, 0, now) { Condition = "晴",
            Sunrise = now.AddHours(2), Sunset = now.AddHours(-2) }, now));
        Assert.Equal("weather-clear", WidgetIconCatalog.GetWeatherName(new(20, 10, 25, 0, now) { Condition = "晴",
            Sunrise = morning, Sunset = morning.AddHours(20) }, now));
        Assert.Equal("weather-unavailable", WidgetIconCatalog.GetWeatherName(null, now));
    }
}
