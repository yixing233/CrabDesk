using System.Text.Json;
using System.Globalization;

namespace CrabDesk.Core;

public sealed partial class WidgetWeatherService : IDisposable
{
    public const string ProviderName = "小米天气";
    private const string Endpoint = "https://weatherapi.market.xiaomi.com/wtr-v3/";
    // Public client parameters also used by CrabTab's weather integration.
    private const string ClientQuery = "&appKey=weather20151024&sign=zUFJoAR2ZVrDy1vF3D07&isGlobal=false&locale=zh_cn";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    public WidgetWeatherService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<IReadOnlyList<WeatherLocation>> SearchAsync(string city, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(city)) return [];
        using var json = await GetJsonAsync("location/city/search?name=" + Uri.EscapeDataString(city.Trim()), cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("城市搜索返回无效数据。");
        return json.RootElement.EnumerateArray().Take(30).Select(ParseLocation).ToArray();
    }

    public async Task<WeatherLocation> ResolveAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        using var json = await GetJsonAsync(FormattableString.Invariant($"location/city/geo?latitude={latitude}&longitude={longitude}"), cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            throw new InvalidDataException("未找到当前位置对应的天气城市。");
        return ParseLocation(json.RootElement[0]) with { Latitude = latitude, Longitude = longitude };
    }

    public async Task<WidgetWeatherSnapshot> GetWeatherAsync(WeatherLocation location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location.ProviderKey)) location = await ResolveAsync(location.Latitude, location.Longitude, cancellationToken).ConfigureAwait(false);
        using var json = await GetJsonAsync(FormattableString.Invariant($"weather/all?latitude={location.Latitude}&longitude={location.Longitude}") +
            "&locationKey=" + Uri.EscapeDataString(location.ProviderKey!) + "&days=7", cancellationToken).ConfigureAwait(false);
        var current = json.RootElement.GetProperty("current");
        var daily = json.RootElement.GetProperty("forecastDaily").GetProperty("temperature").GetProperty("value")[0];
        var first = Number(daily.GetProperty("from"));
        var second = Number(daily.GetProperty("to"));
        var code = (int)Number(current.GetProperty("weather"));
        return AddDetails(json.RootElement, new WidgetWeatherSnapshot(Number(current.GetProperty("temperature").GetProperty("value")), Math.Min(first, second),
            Math.Max(first, second), code, DateTimeOffset.Now) { Provider = ProviderName, Condition = Describe(code) });
    }

    private static string Describe(int code) => code switch
        {
            0 => "晴", 1 => "多云", 2 => "阴", 3 => "阵雨", 4 => "雷阵雨", 5 => "雷阵雨伴冰雹", 6 => "雨夹雪",
            7 => "小雨", 8 => "中雨", 9 => "大雨", 10 => "暴雨", 11 => "大暴雨", 12 => "特大暴雨",
            13 => "阵雪", 14 => "小雪", 15 => "中雪", 16 => "大雪", 17 => "暴雪", 18 or 32 => "雾",
            19 => "冻雨", 20 or 31 => "沙尘暴", 21 => "小到中雨", 22 => "中到大雨", 23 => "大到暴雨",
            24 => "暴雨到大暴雨", 25 => "大暴雨到特大暴雨", 26 => "小到中雪", 27 => "中到大雪", 28 => "大到暴雪",
            29 => "浮尘", 30 => "扬沙", _ => "天气未知"
        };

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(Endpoint + path + ClientQuery, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private static WeatherLocation ParseLocation(JsonElement item) => new(
        item.GetProperty("name").GetString() ?? "", item.TryGetProperty("affiliation", out var region) ? region.GetString() ?? "" : "", "",
        Number(item.GetProperty("latitude")), Number(item.GetProperty("longitude")))
        { ProviderKey = item.GetProperty("locationKey").GetString() };

    private static double Number(JsonElement value)
    {
        var number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.Parse(value.GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new InvalidDataException("天气接口返回了无效数值。");
        return number;
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
