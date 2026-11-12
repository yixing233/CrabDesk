using System.Globalization;
using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Controls;

public sealed partial class WidgetPreview
{
    private void WideWeather(DesktopWidget widget, WidgetWeatherPresentation.Layout layout)
    {
        var panel = new Canvas();
        void Place(FrameworkElement element, LayoutRect bounds)
        {
            if (element is TextBlock label)
            {
                label.VerticalAlignment = VerticalAlignment.Center;
                var wrapper = new Grid();
                wrapper.Children.Add(label);
                element = wrapper;
            }
            element.Width = bounds.Width;
            element.Height = bounds.Height;
            Canvas.SetLeft(element, bounds.X);
            Canvas.SetTop(element, bounds.Y);
            panel.Children.Add(element);
        }
        void Label(string value, double size, LayoutRect bounds, bool secondary = false, bool bold = false, bool centered = false)
        {
            var label = Text(value, size, secondary, bold);
            if (centered) label.TextAlignment = TextAlignment.Center;
            Place(label, bounds);
        }
        var now = DateTimeOffset.Now;
        var weather = widget.Weather;
        Place(new WidgetColorIcon { AssetName = WidgetIconCatalog.GetWeatherName(weather, now) }, layout.Icon);
        Label(weather is null ? "—°" : $"{weather.Temperature:0.#}°", 36, layout.Temperature, bold: true);
        if (weather is not null)
        {
            Label($"高 {weather.Maximum:0}°", 13, layout.Forecast with { Height = layout.City.Height }, true);
            Label($"低 {weather.Minimum:0}°", 11.5, layout.Forecast with { Y = layout.Description.Y, Height = layout.Description.Height }, true);
        }
        Label(weather?.Description ?? (widget.IsLoading ? "正在获取天气…" : "暂无天气数据"), 11.5, layout.Description, true);
        var status = widget.IsLocating ? "正在定位…" : widget.IsLoading ? "正在更新…" :
            widget.LocationAccessDenied ? "定位未授权" : !string.IsNullOrEmpty(widget.Error) ? widget.Error :
            weather?.AirQuality is { } aqi ? $"AQI {aqi:0}" : "查看天气详情";
        var statusIcon = widget.IsLocating || widget.IsLoading ? LucideIconName.RefreshCw :
            widget.LocationAccessDenied || !string.IsNullOrEmpty(widget.Error) ? LucideIconName.CircleAlert : LucideIconName.Gauge;
        Place(new LucideIcon { Icon = statusIcon, FontSize = 12, Foreground = _secondary }, layout.Status with { Width = 12 });
        Label(status, 10, layout.Status with { X = layout.Status.X + 17, Width = layout.Status.Width - 17 }, true);
        IReadOnlyList<WidgetHourlyWeather> hours = weather is null ? [] : WidgetWeatherPresentation.UpcomingHours(weather, now);
        if (hours.Count == 0)
        {
            var first = layout.Hours[0].Time;
            var last = layout.Hours[^1].Temperature;
            Label(widget.IsLoading ? "正在获取逐小时预报…" : "暂无逐小时预报", 12,
                new(first.X, first.Y, last.X + last.Width - first.X, last.Y + last.Height - first.Y), true, centered: true);
        }
        else
            for (var index = 0; index < hours.Count; index++)
            {
                var hour = hours[index];
                var cell = layout.Hours[index];
                Label(hour.Time.ToString("HH时", CultureInfo.InvariantCulture), 10.5, cell.Time, true, centered: true);
                Place(new WidgetColorIcon { AssetName = WidgetIconCatalog.GetHourlyWeatherName(hour, weather!) }, cell.Icon);
                Label($"{hour.Temperature:0}°", 12.5, cell.Temperature, bold: true, centered: true);
            }
        if (widget.Cities.Count > 1)
        {
            var index = widget.Cities.FindIndex(city => city.Id == widget.ActiveCityId) + 1;
            Label($"{index}/{widget.Cities.Count}", 11, layout.Counter, true, centered: true);
            Label("‹", 18, layout.Previous, centered: true);
            Label("›", 18, layout.Next, centered: true);
        }
        CardBody.Children.Add(panel);
    }
}
