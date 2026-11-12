using System.Drawing;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetLocationTests
{
    [Fact]
    public async Task LocationRefreshesOneCurrentCityAndPreservesManualCities()
    {
        using var fixture = new Fixture();
        var manual = fixture.Widget.ActiveCity!;
        var cached = manual.Weather;
        await fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        var current = Assert.Single(fixture.Widget.Cities.Where(city => city.IsCurrentLocation));
        Assert.Same(current, fixture.Widget.ActiveCity);
        Assert.Equal(24, current.Weather!.Temperature);
        Assert.Same(cached, manual.Weather);
        Assert.False(fixture.Widget.IsLocating);
        Assert.False(fixture.Widget.IsLoading);
        Assert.False(fixture.Runtime.TryConsumeWidgetNavigation(out _));
        await fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        Assert.Equal(current.Id, fixture.Widget.ActiveCityId);
        Assert.Equal(2, fixture.Widget.Cities.Count);
        Assert.Equal(2, fixture.LocationCalls);
    }

    [Fact]
    public async Task DenialKeepsTheSelectedCityAndCacheWithoutSendingCoordinates()
    {
        using var fixture = new Fixture();
        fixture.Position = _ => Task.FromException<WidgetCoordinates>(new UnauthorizedAccessException());
        var city = fixture.Widget.ActiveCity;
        var cached = fixture.Widget.Weather;
        await fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        Assert.Same(city, fixture.Widget.ActiveCity);
        Assert.Same(cached, fixture.Widget.Weather);
        Assert.True(fixture.Widget.LocationAccessDenied);
        Assert.False(fixture.Widget.IsLocating);
        Assert.Contains("未获授权", fixture.Widget.Error);
        Assert.Empty(fixture.Requests);
        await fixture.Runtime.RefreshWidgetWeatherAsync(fixture.Widget);
        Assert.False(fixture.Widget.LocationAccessDenied);
        Assert.Empty(fixture.Widget.Error);
    }

    [Fact]
    public async Task ASecondClickAndWeatherRefreshDoNotInterruptPendingLocation()
    {
        using var fixture = new Fixture();
        var position = new TaskCompletionSource<WidgetCoordinates>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Position = _ => position.Task;
        var pending = fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        Assert.True(fixture.Widget.IsLocating);
        await fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        await fixture.Runtime.RefreshWidgetWeatherAsync(fixture.Widget);
        Assert.Equal(1, fixture.LocationCalls);
        Assert.Empty(fixture.Requests);
        position.SetResult(new(39.9, 116.4));
        await pending;
        Assert.Equal(24, fixture.Widget.Weather!.Temperature);
        Assert.False(fixture.Widget.IsLocating);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("switch")]
    [InlineData("pause")]
    public async Task RemovedSwitchedOrPausedWidgetsIgnoreLateLocationResults(string action)
    {
        using var fixture = new Fixture();
        var position = new TaskCompletionSource<WidgetCoordinates>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Position = _ => position.Task;
        var pending = fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        if (action == "remove") fixture.Runtime.RemoveWidget(fixture.Widget);
        else if (action == "pause") fixture.Runtime.SetPaused(true);
        else
        {
            var another = DesktopWidgets.AddWeatherCity(fixture.Widget, new("手动城市二", "", "中国", 31, 121));
            fixture.Runtime.SelectWidgetCity(fixture.Widget, fixture.Widget.Cities.First().Id);
            fixture.Runtime.SelectWidgetCity(fixture.Widget, another.Id);
        }
        position.SetResult(new(39.9, 116.4));
        await pending;
        Assert.DoesNotContain(fixture.Widget.Cities, city => city.IsCurrentLocation);
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Widget.Error);
    }

    [Fact]
    public async Task ACancelledLocationCannotClearTheNextRequestsState()
    {
        using var fixture = new Fixture();
        var first = new TaskCompletionSource<WidgetCoordinates>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<WidgetCoordinates>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Position = _ => fixture.LocationCalls == 1 ? first.Task : second.Task;
        var oldRequest = fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        fixture.Runtime.CancelWidgetRequest(fixture.Widget.Id);
        var newRequest = fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        first.SetException(new UnauthorizedAccessException());
        await oldRequest;
        Assert.True(fixture.Widget.IsLocating);
        Assert.False(fixture.Widget.LocationAccessDenied);
        Assert.Empty(fixture.Widget.Error);
        second.SetResult(new(39.9, 116.4));
        await newRequest;
        Assert.False(fixture.Widget.IsLocating);
        Assert.True(fixture.Widget.ActiveCity!.IsCurrentLocation);
    }

    [Fact]
    public async Task CityResolutionFailurePreservesExistingWeather()
    {
        using var fixture = new Fixture { FailResolution = true };
        var city = fixture.Widget.ActiveCity;
        var weather = fixture.Widget.Weather;
        await fixture.Runtime.LocateWidgetWeatherAsync(fixture.Widget);
        Assert.Same(city, fixture.Widget.ActiveCity);
        Assert.Same(weather, fixture.Widget.Weather);
        Assert.Contains("城市识别失败", fixture.Widget.Error);
        Assert.False(fixture.Widget.IsLocating);
    }

    [Fact]
    public void CardLocationControlsDoNotDragOrOpenSettingsOrWeatherDetails() => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Position = _ => Task.FromException<WidgetCoordinates>(new UnauthorizedAccessException());
        fixture.Widget.Cities.Clear();
        fixture.Widget.Location = null;
        fixture.Widget.Weather = null;
        var openedDetails = 0;
        fixture.Runtime.WidgetWeatherDetailsRequested += _ => openedDetails++;
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        foreach (var button in new[] { DesktopBoxForm.WeatherLocationButtonRectangle(fixture.Widget), DesktopBoxForm.WeatherEmptyLocationRectangle(fixture.Widget) })
        {
            var point = new PointF(button.X + button.Width / 2, button.Y + button.Height / 2);
            var args = new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0);
            var bounds = fixture.Widget.Bounds;
            Call(surface, "HandleWidgetMouseDown", args, point);
            Call(surface, "HandleWidgetMouseMove", new PointF(point.X + 40, point.Y + 40));
            Assert.Equal(bounds, fixture.Widget.Bounds);
            Call(surface, "HandleWidgetMouseUp", args, point);
        }
        Assert.Equal(2, fixture.LocationCalls);
        Assert.Equal(0, openedDetails);
        Assert.False(fixture.Runtime.TryConsumeWidgetNavigation(out _));
        Assert.True(fixture.Widget.LocationAccessDenied);
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void LocationControlsFitSupportedCardsAndAvoidTheMenu(int width)
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather, Bounds = new(20, 20, width, 180) };
        var header = DesktopBoxForm.WeatherLocationButtonRectangle(widget);
        var empty = DesktopBoxForm.WeatherEmptyLocationRectangle(widget);
        var card = DesktopBoxForm.WidgetRectangle(widget);
        if (width >= 300)
        {
            // Wide cards drop the header location controls and keep the empty
            // card call to action.
            Assert.Equal(0, header.Width);
            Assert.Equal(0, DesktopBoxForm.WeatherLocationsButtonRectangle(widget).Width);
            Assert.True(card.Contains(empty));
            return;
        }
        Assert.True(card.Contains(header));
        Assert.True(card.Contains(empty));
        Assert.False(header.IntersectsWith(empty));
        Assert.True(header.Right < card.Right - 32);
    }

    private sealed class Fixture : IWidgetLocationService, IDisposable
    {
        internal CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        internal DesktopWidget Widget { get; } = new() { Kind = DesktopWidgetKind.Weather, MonitorId = "location-test", Bounds = new(20, 20, 180, 180) };
        internal MonitorLayout Monitor { get; } = new() { Id = "location-test", DeviceName = "test", IsPrimary = true,
            Bounds = new(0, 0, 1200, 800), WorkArea = new(0, 0, 1200, 800), PixelBounds = new(0, 0, 1200, 800), PixelWorkArea = new(0, 0, 1200, 800), DpiScale = 1 };
        internal Func<CancellationToken, Task<WidgetCoordinates>> Position { get; set; } = _ => Task.FromResult(new WidgetCoordinates(39.9, 116.4));
        internal int LocationCalls { get; private set; }
        internal bool FailResolution { get; set; }
        internal List<string> Requests { get; } = [];
        private readonly HttpClient _client;
        private readonly WidgetWeatherService _weather;
        private readonly RuntimeTimer _timer = new(TimeSpan.FromHours(1), false, _ => { }, () => { });
        private readonly RuntimeTimer _retry = new(TimeSpan.FromHours(1), false, _ => { }, () => { });

        internal Fixture()
        {
            _client = new HttpClient(new Handler(request =>
            {
                Requests.Add(request.RequestUri!.AbsolutePath);
                var resolve = request.RequestUri.AbsolutePath.EndsWith("/geo");
                return new(resolve && FailResolution ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    Content = new StringContent(resolve
                        ? "[{\"name\":\"定位城市\",\"latitude\":39.9,\"longitude\":116.4,\"locationKey\":\"weathercn:test\"}]"
                        : "{\"current\":{\"temperature\":{\"value\":24},\"weather\":0},\"forecastDaily\":{\"temperature\":{\"value\":[{\"from\":28,\"to\":20}]}}}")
                };
            }));
            _weather = new WidgetWeatherService(_client);
            var state = new CrabDeskState { Widgets = [Widget] };
            DesktopWidgets.AddWeatherCity(Widget, new("手动城市", "", "中国", 30, 120) { ProviderKey = "weathercn:manual" }).Weather = new(18, 12, 22, 0, DateTimeOffset.Now);
            Set(Runtime, "<State>k__BackingField", state);
            Set(Runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(Runtime, "<Monitors>k__BackingField", new[] { Monitor });
            Set(Runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
            Set(Runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
            Set(Runtime, "_widgetLocationService", this);
            Set(Runtime, "_widgetWeatherService", _weather);
            Set(Runtime, "_saveTimer", _timer);
            Set(Runtime, "_takeoverRetryTimer", _retry);
            Set(Runtime, "_desktopHost", new DesktopHostService());
            Set(Runtime, "_desktopInputMonitor", Mock.Of<IDesktopInputMonitor>());
            Set(Runtime, "_originalFileAttributes", new Dictionary<string, FileAttributes>());
            Set(Runtime, "_hiddenShellIconOriginals", new Dictionary<string, int?>());
        }

        public Task<WidgetCoordinates> GetPositionAsync(WidgetLocationAnchor? anchor, CancellationToken cancellationToken)
        { LocationCalls++; return Position(cancellationToken); }
        public void Dispose() { _timer.Dispose(); _retry.Dispose(); _weather.Dispose(); _client.Dispose(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
