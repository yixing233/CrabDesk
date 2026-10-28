using System.Globalization;
using System.Text.Json;

namespace CrabDesk.Core;

public sealed partial class WidgetWeatherService
{
    private static JsonElement Field(JsonElement item, params string[] path)
    {
        foreach (var name in path)
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out item)) return default;
        return item;
    }
    private static JsonElement At(JsonElement array, int index) => array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength() ? array[index] : default;
    private static IEnumerable<JsonElement> Items(JsonElement array) => array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
    private static double? OptionalNumber(JsonElement value) => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    private static DateTimeOffset? Time(JsonElement value) => DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private static WidgetWeatherSnapshot AddDetails(JsonElement root, WidgetWeatherSnapshot snapshot)
    {
        var current = Field(root, "current");
        var hourly = Field(root, "forecastHourly");
        var daily = Field(root, "forecastDaily");
        var hourStart = Time(Field(hourly, "temperature", "pubTime"));
        var hourlyValues = new List<WidgetHourlyWeather>();
        if (hourStart is { } start)
        {
            var temperatures = Items(Field(hourly, "temperature", "value")).Take(24).ToArray();
            for (var i = 0; i < temperatures.Length; i++)
                if (OptionalNumber(temperatures[i]) is { } temperature)
                    hourlyValues.Add(new(start.AddHours(i), temperature,
                        Describe((int)(OptionalNumber(At(Field(hourly, "weather", "value"), i)) ?? -1)),
                        OptionalNumber(At(Field(hourly, "aqi", "value"), i))));
        }
        var dailyValues = new List<WidgetDailyWeather>();
        var dateStart = Time(Field(daily, "pubTime")) ?? snapshot.FetchedAt;
        var dates = Items(Field(daily, "temperature", "value")).Take(7).ToArray();
        for (var i = 0; i < dates.Length; i++)
        {
            if (OptionalNumber(Field(dates[i], "from")) is not { } high || OptionalNumber(Field(dates[i], "to")) is not { } low) continue;
            var condition = At(Field(daily, "weather", "value"), i);
            dailyValues.Add(new(dateStart.AddDays(i), Math.Min(high, low), Math.Max(high, low),
                Describe((int)(OptionalNumber(Field(condition, "from")) ?? -1)),
                OptionalNumber(At(Field(daily, "precipitationProbability", "value"), i))));
        }
        var indices = new List<WidgetWeatherIndex>();
        foreach (var item in Items(Field(root, "indices", "indices")))
        {
            var type = Field(item, "type").ToString().Trim();
            var raw = Field(item, "value").ToString().Trim();
            if (type.Length == 0 || raw.Length == 0) continue;
            var name = type switch { "uvIndex" => "紫外线", "humidity" => "湿度", "feelsLike" => "体感", "pressure" => "气压", "carWash" => "洗车", "sports" => "运动", _ => type };
            var value = type switch
            {
                "carWash" or "sports" => raw switch { "0" => "适宜", "1" => "较适宜", "2" => "不宜", _ => raw },
                "humidity" or "feelsLike" or "pressure" when OptionalNumber(Field(item, "value")) is { } number =>
                    number.ToString("0.#", CultureInfo.InvariantCulture) + (type switch { "humidity" => "%", "feelsLike" => "°C", _ => " hPa" }),
                _ => raw
            };
            indices.Add(new(name, value) { Type = type });
        }
        return snapshot with
        {
            FeelsLike = OptionalNumber(Field(current, "feelsLike", "value")), Humidity = OptionalNumber(Field(current, "humidity", "value")),
            WindSpeed = OptionalNumber(Field(current, "wind", "speed", "value")), WindDirection = OptionalNumber(Field(current, "wind", "direction", "value")),
            Pressure = OptionalNumber(Field(current, "pressure", "value")), UvIndex = OptionalNumber(Field(current, "uvIndex")),
            PublishedAt = Time(Field(current, "pubTime")), AirQuality = OptionalNumber(Field(root, "aqi", "aqi")),
            Pm25 = OptionalNumber(Field(root, "aqi", "pm25")), AirQualityAdvice = Field(root, "aqi", "suggest").ToString(),
            Sunrise = Time(Field(At(Field(daily, "sunRiseSet", "value"), 0), "from")),
            Sunset = Time(Field(At(Field(daily, "sunRiseSet", "value"), 0), "to")),
            Hourly = hourlyValues, Daily = dailyValues, Indices = indices,
            Alerts = Items(Field(root, "alerts")).Where(item => Time(Field(item, "pubTime")) is not { } published || snapshot.FetchedAt - published < TimeSpan.FromDays(1))
                .Select(item => new WidgetWeatherAlert(Field(item, "title").ToString(), Field(item, "detail").ToString()))
                .Where(item => !string.IsNullOrWhiteSpace(item.Title)).ToArray()
        };
    }
}
