using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.Windows;

public sealed partial class WeatherDetailsWindow
{
    public WeatherLocationsView Locations { get; } = new();
    private readonly Flyout _locationsFlyout = new();
    private readonly CancellationTokenSource _locationsLifetime = new();
    private CancellationTokenSource? _locationSearch;
    private bool _showLocationsOnLoad, _locationsClosed;
    private Guid? _locationActiveId;
    private (string Background, double Opacity, bool Acrylic, double Radius)? _locationsMaterial;

    private void ApplyLocationsMaterial(BoxAppearance appearance, GlobalAppearanceSettings settings)
    {
        if (_locationsOnly) return;
        var material = (appearance.Background, appearance.ResolveBackgroundOpacity(settings.UseAcrylicBoxes),
            settings.UseAcrylicBoxes, WindowCornerRadius(settings.CornerRadius));
        if (_locationsMaterial == material) return;
        _locationsMaterial = material;
        var color = Converters.HexColor.TryParse(appearance.Background, out var parsed) ? parsed :
            global::Windows.UI.Color.FromArgb(255, 42, 45, 50);
        Brush fill;
        if (settings.UseAcrylicBoxes)
            fill = new AcrylicBrush { TintColor = color, TintOpacity = material.Item2, FallbackColor = color };
        else { color.A = (byte)Math.Round(255 * material.Item2); fill = new SolidColorBrush(color); }
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, fill));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(material.Item4)));
        _locationsFlyout.FlyoutPresenterStyle = style;
    }

    private void InitializeLocations()
    {
        if (!_locationsOnly)
        {
            _locationsFlyout.Content = Locations;
            View.LocationsButton.Flyout = _locationsFlyout;
            View.Loaded += (_, _) => { if (_showLocationsOnLoad) { _showLocationsOnLoad = false; _locationsFlyout.ShowAt(View.LocationsButton); } };
            _locationsFlyout.Opened += (_, _) => Locations.Search.Focus(FocusState.Programmatic);
            _locationsFlyout.Closed += (_, _) => { _locationSearch?.Cancel(); Locations.Search.ItemsSource = null; Locations.SetStatus(string.Empty); };
        }
        Locations.Search.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            _locationSearch?.Cancel(); sender.ItemsSource = null; Locations.SetStatus(string.Empty);
        };
        Locations.Search.QuerySubmitted += async (_, args) =>
        {
            if (args.ChosenSuggestion is WeatherLocation location) await AddLocationAsync(location);
            else await SearchLocationsAsync(args.QueryText);
        };
        Locations.Saved.SelectionChanged += async (_, _) =>
        {
            if (_updating || Locations.Saved.SelectedItem is not WidgetWeatherCity city || city.Id == _widget.ActiveCityId) return;
            _runtime.SelectWidgetCity(_widget, city.Id);
            await _runtime.RefreshWidgetWeatherAsync(_widget, force: false);
        };
        Locations.Locate.Click += async (_, _) =>
        {
            await _runtime.LocateWidgetWeatherAsync(_widget, _locationsLifetime.Token);
            if (!_locationsClosed) Locations.SetStatus(_widget.Error);
        };
        Locations.MoreSettings.Click += (_, _) => { _locationsFlyout.Hide(); Close(); _runtime.OpenWidgetEditor(_widget.Id); };
        Closed += (_, _) =>
        {
            _locationsClosed = true;
            _locationSearch?.Cancel(); _locationsLifetime.Cancel(); _locationsLifetime.Dispose();
        };
    }

    public void ShowLocations()
    {
        if (_locationsClosed || _locationsOnly) return;
        if (View.IsLoaded) _locationsFlyout.ShowAt(View.LocationsButton);
        else _showLocationsOnLoad = true;
    }

    private void UpdateLocations()
    {
        if (_locationActiveId != _widget.ActiveCity?.Id) { _locationActiveId = _widget.ActiveCity?.Id; Locations.SetStatus(string.Empty); }
        Locations.Update(_widget, _root.RequestedTheme);
    }

    private async Task SearchLocationsAsync(string text)
    {
        if (_locationsClosed) return;
        _locationSearch?.Cancel(); Locations.Search.ItemsSource = null;
        if (string.IsNullOrWhiteSpace(text)) { Locations.SetStatus("输入城市名后搜索。"); return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_locationsLifetime.Token);
        _locationSearch = request; Locations.SetStatus("正在搜索位置…");
        try
        {
            var results = await _runtime.SearchWidgetCitiesAsync(text.Trim(), request.Token);
            if (_locationsClosed || request.IsCancellationRequested) return;
            Locations.Search.ItemsSource = results;
            Locations.Search.IsSuggestionListOpen = results.Count > 0;
            Locations.SetStatus(results.Count == 0 ? "未找到位置，换个名称试试。" : "选择搜索结果即可添加位置。");
        }
        catch (OperationCanceledException)
        { if (!_locationsClosed && !request.IsCancellationRequested) Locations.SetStatus("搜索超时，请重试。"); }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or System.IO.InvalidDataException or FormatException or KeyNotFoundException)
        { if (!_locationsClosed && !request.IsCancellationRequested) Locations.SetStatus("搜索失败，请检查网络后重试。"); }
        finally { if (_locationSearch == request) _locationSearch = null; }
    }

    private async Task AddLocationAsync(WeatherLocation location)
    {
        if (_locationsClosed || !_runtime.State.Widgets.Contains(_widget)) return;
        _locationSearch?.Cancel();
        Locations.Search.IsSuggestionListOpen = false; Locations.Search.ItemsSource = null; Locations.Search.Text = string.Empty;
        _runtime.AddWidgetCity(_widget, location);
        await _runtime.RefreshWidgetWeatherAsync(_widget, force: false, cancellationToken: _locationsLifetime.Token);
        if (!_locationsClosed) Locations.SetStatus(string.IsNullOrEmpty(_widget.Error) ? $"已切换到 {location.Name}" : _widget.Error);
    }
}
