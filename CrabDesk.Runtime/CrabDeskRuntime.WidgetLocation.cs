using CrabDesk.Core;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    private readonly IWidgetLocationService _widgetLocationService = new WidgetLocationService();

    public Task LocateWidgetWeatherAsync(DesktopWidget widget, CancellationToken cancellationToken = default) =>
        LocateWidgetWeatherAsync(widget, null, cancellationToken);

    internal async Task LocateWidgetWeatherAsync(DesktopWidget widget, WidgetLocationAnchor? anchor, CancellationToken cancellationToken = default)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Weather || widget.IsLocating || !State.Widgets.Contains(widget)) return;
        using var cancellation = BeginWidgetRequest(widget, cancellationToken);
        widget.IsLocating = true;
        widget.LocationAccessDenied = false;
        widget.Error = string.Empty;
        WidgetChanged(widget);
        var hasPosition = false;
        try
        {
            var position = await _widgetLocationService.GetPositionAsync(anchor, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            hasPosition = true;
            var location = await ResolveWidgetCityAsync(position.Latitude, position.Longitude, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || !State.Widgets.Contains(widget)) return;
            // Hand off to a normal weather request without cancelling this
            // location operation when AddWidgetCity updates the active city.
            EndWidgetRequest(widget, cancellation);
            AddWidgetCity(widget, location, currentLocation: true);
            await RefreshWidgetWeatherAsync(widget, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested) widget.Error = "获取位置超时，请重试";
        }
        catch (UnauthorizedAccessException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                widget.LocationAccessDenied = true;
                widget.Error = "系统定位未获授权，请开启位置服务和桌面应用的位置访问。";
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or TimeoutException or
            System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            if (!cancellation.IsCancellationRequested)
                widget.Error = hasPosition ? "位置已获取，城市识别失败，请稍后重试" : "暂时无法获取当前位置，请检查系统定位设置后重试";
        }
        finally
        {
            var ownsRequest = _widgetRequests.TryGetValue(widget.Id, out var current) && current == cancellation;
            EndWidgetRequest(widget, cancellation);
            if (ownsRequest) WidgetChanged(widget);
        }
    }
}
