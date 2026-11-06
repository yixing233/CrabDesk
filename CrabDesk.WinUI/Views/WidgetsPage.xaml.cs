using CrabDesk.Core;
using System.Collections.ObjectModel;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage : Page
{
    private readonly CrabDeskRuntime _runtime = App.GetService<CrabDeskRuntime>();
    private DesktopWidget? _selected;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _citySearch;
    private bool _loading;
    private bool _saving;
    private bool _editing;
    private bool _galleryRefreshQueued;
    private bool _galleryIsCatalog;
    private readonly ObservableCollection<WidgetGalleryItem> _galleryItems = [];
    private readonly Dictionary<Guid, CheckBox> _taskChecks = [];
    private readonly Dictionary<Guid, TextBlock> _taskDateLabels = [];

    public WidgetsPage()
    {
        InitializeComponent();
        WidgetGallery.ItemsSource = _galleryItems;
        Loaded += (_, _) =>
        {
            _lifetime = new CancellationTokenSource();
            _runtime.Changed += Runtime_Changed;
            _runtime.WidgetMonitorUpdated += Runtime_WidgetMonitorUpdated;
            ReloadInstances();
        };
        Unloaded += (_, _) =>
        {
            _runtime.Changed -= Runtime_Changed;
            _runtime.WidgetMonitorUpdated -= Runtime_WidgetMonitorUpdated;
            _citySearch?.Cancel();
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        };
    }

    private void Runtime_Changed(object? sender, EventArgs args)
    {
        if (_saving || !IsLoaded || _galleryRefreshQueued) return;
        _galleryRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _galleryRefreshQueued = false;
            if (IsLoaded) ReloadInstances();
        })) _galleryRefreshQueued = false;
    }

    private void ReloadInstances()
    {
        if (_runtime.TryConsumeWidgetNavigation(out var requestedId))
        {
            var requested = _runtime.State.Widgets.FirstOrDefault(w => w.Id == requestedId);
            if (requested is not null) { LoadEditor(requested); return; }
            ShowOverview();
        }
        else if (_selected is not null && !_runtime.State.Widgets.Contains(_selected)) ShowOverview();
        else if (_editing) UpdateOnlineStatus();
        RefreshGallery();
    }

    private WidgetPreviewAppearance PreviewAppearance() => new(_runtime.State.Boxes.FirstOrDefault()?.Appearance
        ?? _runtime.State.Settings.DefaultBoxAppearance, _runtime.State.Settings.Appearance);

    private void RefreshGallery(bool refreshPreviews = true, DesktopWidget? changedWidget = null)
    {
        var catalog = GalleryTabs.SelectedItem == AddWidgetsTab;
        var appearance = PreviewAppearance();
        if (catalog != _galleryIsCatalog)
        {
            _galleryItems.Clear();
            _galleryIsCatalog = catalog;
        }
        var widgets = catalog
            ? Enum.GetValues<DesktopWidgetKind>().Select(kind => _galleryItems.FirstOrDefault(item => item.Widget.Kind == kind)?.Widget ?? CreateSample(kind)).ToArray()
            : _runtime.State.Widgets.ToArray();
        for (var index = 0; index < widgets.Length; index++)
        {
            var widget = widgets[index];
            var item = _galleryItems.FirstOrDefault(entry => ReferenceEquals(entry.Widget, widget));
            var caption = catalog ? widget.Kind switch
            {
                DesktopWidgetKind.Clock => "黑白表盘与可选秒针", DesktopWidgetKind.Calendar => "浏览月份，标记今天",
                DesktopWidgetKind.Note => "随手记下灵感", DesktopWidgetKind.Todo => "记录并完成每一件事",
                DesktopWidgetKind.Translation => "随时翻译一段文字", DesktopWidgetKind.Weather => "关心城市的阴晴冷暖",
                DesktopWidgetKind.Performance => "实时查看 CPU、GPU、内存与磁盘",
                DesktopWidgetKind.ControlCenter => "开关、音量、亮度与媒体控制",
                DesktopWidgetKind.Lyrics => "随音乐滚动，逐字点亮歌词",
                _ => "完整信息、仅速度或实时曲线"
            } : (widget.Kind == DesktopWidgetKind.Network ? DesktopWidgets.NetworkDisplayName(widget.NetworkDisplayMode) + " · " : "") + DesktopWidgets.SizeLabel(widget) + (widget.IsLocked ? " · 已锁定" : "");
            var count = catalog ? _runtime.State.Widgets.Count(existing => existing.Kind == widget.Kind) : 0;
            if (item is null)
            {
                item = new WidgetGalleryItem(widget, appearance, catalog, caption, count);
                _galleryItems.Insert(index, item);
            }
            else
            {
                var previousIndex = _galleryItems.IndexOf(item);
                if (previousIndex != index) _galleryItems.Move(previousIndex, index);
                item.Refresh(appearance, caption, count, refreshPreviews && (changedWidget is null || item.Widget == changedWidget));
            }
        }
        while (_galleryItems.Count > widgets.Length) _galleryItems.RemoveAt(_galleryItems.Count - 1);
        UpdateGallerySelection();
        WidgetGallery.Visibility = Show(widgets.Length > 0);
        EmptyState.Visibility = Show(!catalog && widgets.Length == 0);
        GalleryCaption.Text = catalog ? "选择组件，添加到桌面"
            : $"已添加 {widgets.Length} 个组件";
    }

    private void UpdateGallerySelection()
    {
        foreach (var item in _galleryItems) item.IsSelected = !item.IsCatalog && item.Widget == _selected;
        WidgetGallery.SelectedItem = _galleryItems.FirstOrDefault(item => item.IsSelected);
    }

    private static DesktopWidget CreateSample(DesktopWidgetKind kind)
    {
        var sample = new DesktopWidget
        {
            Kind = kind, Size = DesktopWidgets.DefaultSize(kind),
            Text = "今天的灵感\n把重要的事记下来。\n留一点时间给自己。",
            Tasks = [new() { Text = "整理桌面", IsCompleted = true }, new() { Text = "记下今天的计划" }, new() { Text = "读完一本好书" }],
            Translation = "你好，世界。\n让沟通更简单。", TargetLanguage = "简体中文",
            Location = new("北京", "北京市", "中国", 39.9, 116.4), Weather = new(22, 16, 25, 2, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName },
            Performance = kind == DesktopWidgetKind.Performance ? new(DateTimeOffset.Now, 24, 12, 9L << 30, 16L << 30, 8, 2.4 * (1 << 20), 384 * 1024, 240L << 30, 512L << 30) : null,
            Network = kind == DesktopWidgetKind.Network ? new(DateTimeOffset.Now) { IsConnected = true, InterfaceName = "以太网", NetworkType = "以太网", DownloadBytesPerSecond = 2.4 * (1 << 20), UploadBytesPerSecond = 384 * 1024, IPv4 = "192.168.1.100", IPv6 = "2001:db8::10", LatencyMilliseconds = 18, LatencyTarget = "1.1.1.1", LatencyStatus = "" } : null,
            PublicIp = kind == DesktopWidgetKind.Network ? "203.0.113.24" : ""
        };
        if (kind == DesktopWidgetKind.Network)
        {
            var now = DateTimeOffset.Now;
            sample.NetworkHistory.AddRange(Enumerable.Range(0, 61).Select(index =>
                new WidgetNetworkRatePoint(now.AddSeconds(index - 60),
                    (1.4 + Math.Sin(index * .24) * .7 + Math.Sin(index * .73) * .25) * (1 << 20),
                    (240 + Math.Sin(index * .18) * 110 + Math.Cos(index * .51) * 45) * 1024)));
        }
        return sample;
    }

    private void CloseEditor()
    {
        _citySearch?.Cancel();
        CityQuery.IsSuggestionListOpen = false;
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        _selected = null;
        _editing = false;
        EditorHost.IsPaneOpen = false;
        EditorView.Visibility = Visibility.Collapsed;
        WidgetGallery.SelectedItem = null;
        UpdateGallerySelection();
    }

    private void ShowOverview()
    {
        CloseEditor();
        GalleryTabs.SelectedItem = MyWidgetsTab;
    }

    private void BackToGallery_Click(object sender, RoutedEventArgs args) => ShowOverview();
    private void BrowseWidgets_Click(object sender, RoutedEventArgs args) => GalleryTabs.SelectedItem = AddWidgetsTab;
    private void GalleryTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    { if (IsLoaded) { CloseEditor(); RefreshGallery(); } }

    private void EditorHost_PaneClosed(SplitView sender, object args)
    { if (_editing && !EditorHost.IsPaneOpen) CloseEditor(); }

    private void EditorHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        EditorHost.DisplayMode = args.NewSize.Width >= 800 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        EditorHost.OpenPaneLength = Math.Min(372, Math.Max(280, args.NewSize.Width - 24));
        EditorPane.Margin = EditorHost.DisplayMode == SplitViewDisplayMode.Inline ? new Thickness(16, 0, 0, 0) : new Thickness(0);
    }

    private void WidgetGallery_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not WidgetGalleryItem item) return;
        if (item.IsCatalog && _runtime.State.Widgets.Count(widget => widget.Kind == item.Widget.Kind) > 1)
        { ShowOverview(); return; }
        Try(() => LoadEditor(item.IsCatalog ? _runtime.AddWidget(item.Widget.Kind) : item.Widget));
    }

    private void WidgetGallery_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (WidgetGallery.ItemsPanelRoot is not ItemsWrapGrid panel || args.NewSize.Width <= 0) return;
        var columns = Math.Clamp((int)((args.NewSize.Width + 12) / 212), 1, 3);
        panel.ItemWidth = Math.Floor(args.NewSize.Width / columns);
    }

    private void LoadEditor(DesktopWidget? widget)
    {
        if (widget is null) { ShowOverview(); return; }
        GalleryTabs.SelectedItem = MyWidgetsTab;
        _citySearch?.Cancel();
        _lifetime?.Cancel(); _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        _selected = widget;
        _editing = true;
        EditorView.Visibility = Visibility.Visible;
        EditorHost.IsPaneOpen = true;
        EditorName.Text = widget.DisplayTitle;
        CardSettings.IsExpanded = DesktopWidgets.SupportsFreeResize(widget.Kind);
        Notice.IsOpen = false;
        _loading = true;
        TitleInput.Text = widget.Title;
        SizePicker.ItemsSource = DesktopWidgets.SupportedSizes(widget).Select(size => new SizeChoice(size, DesktopWidgets.SizeLabel(size))).ToArray();
        SizePicker.DisplayMemberPath = nameof(SizeChoice.Label);
        SizePicker.SelectedItem = SizePicker.Items.Cast<SizeChoice>().First(s => s.Size == widget.Size);
        MonitorPicker.ItemsSource = _runtime.Monitors.Select((monitor, index) => new MonitorChoice(monitor.Id, $"显示器 {index + 1}" + (monitor.IsPrimary ? "（主显示器）" : ""))).ToArray();
        MonitorPicker.SelectedItem = MonitorPicker.Items.OfType<MonitorChoice>().FirstOrDefault(m => m.Id == widget.MonitorId);
        PositionLock.IsOn = widget.IsLocked;
        Seconds.IsOn = widget.ShowSeconds;
        var translation = _runtime.State.Settings.WidgetTranslation;
        TranslationBaseUrl.Text = translation.BaseUrl;
        TranslationApiKey.Password = translation.ApiKey;
        TranslationModel.Text = translation.Model;
        TranslationPrompt.Text = translation.PromptTemplate;
        TranslationReuseConversation.IsOn = translation.ReuseConversation;
        TranslationConversationIdle.Value = translation.ConversationIdleMinutes;
        TranslationConversationIdle.IsEnabled = translation.ReuseConversation;
        NewTaskInput.Text = string.Empty;
        CityQuery.Text = string.Empty;
        CityQuery.ItemsSource = null;
        CityQuery.IsSuggestionListOpen = false;
        CitySearchStatus.Text = string.Empty;
        LocationSettings.Visibility = Visibility.Collapsed;
        ClockEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Clock);
        CalendarHelp.Visibility = Show(widget.Kind == DesktopWidgetKind.Calendar);
        NoteHelp.Visibility = Show(widget.Kind == DesktopWidgetKind.Note);
        TodoEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Todo);
        TranslationEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Translation);
        WeatherEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Weather);
        PerformanceEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Performance);
        NetworkEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Network);
        LoadLyricsSettings(widget);
        LoadMonitorSettings(widget);
        _loading = false;
        RenderTasks();
        UpdateOnlineStatus();
        EditorScroll.ChangeView(null, 0, null, true);
        RefreshGallery(refreshPreviews: false);
    }

    private static Visibility Show(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
    private void Save(Action<DesktopWidget> change)
    {
        if (_loading || _selected is not { } widget) return;
        change(widget);
        _saving = true;
        try { _runtime.WidgetChanged(widget); }
        finally { _saving = false; }
        EditorName.Text = widget.DisplayTitle;
        RefreshGallery(changedWidget: widget);
    }

    private void Error(string message) { Notice.Message = message; Notice.IsOpen = true; }
    private void Try(Action action) { try { action(); } catch (InvalidOperationException exception) { Error(exception.Message); } }

    private void TitleInput_TextChanged(object sender, TextChangedEventArgs args)
    { if (_selected?.Title != TitleInput.Text) Save(w => w.Title = TitleInput.Text); }
    private void PositionLock_Toggled(object sender, RoutedEventArgs args) => Save(w => w.IsLocked = PositionLock.IsOn);
    private void ClockOption_Toggled(object sender, RoutedEventArgs args)
    { if (_selected?.Kind == DesktopWidgetKind.Clock) Save(w => w.ShowSeconds = Seconds.IsOn); }

    private void SizePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && _selected is { } widget && SizePicker.SelectedItem is SizeChoice size) Try(() => _runtime.ResizeWidget(widget, size.Size));
    }

    private void MonitorPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && _selected is { } widget && MonitorPicker.SelectedItem is MonitorChoice monitor) Try(() => _runtime.MoveWidgetToMonitor(widget, monitor.Id));
    }

    private void AddTask_Click(object sender, RoutedEventArgs args) => AddTask();
    private void NewTaskInput_KeyDown(object sender, KeyRoutedEventArgs args)
    { if (args.Key == VirtualKey.Enter) { AddTask(); args.Handled = true; } }
    private void AddTask()
    {
        var text = NewTaskInput.Text.Trim();
        if (_selected?.Kind != DesktopWidgetKind.Todo || text.Length == 0) return;
        Save(w => w.Tasks.Add(new WidgetTodoItem { Text = text }));
        NewTaskInput.Text = string.Empty;
        RenderTasks();
    }

    private void RenderTasks()
    {
        TaskRows.Children.Clear();
        _taskChecks.Clear();
        _taskDateLabels.Clear();
        if (_selected is not { Kind: DesktopWidgetKind.Todo } widget) return;
        foreach (var task in widget.Tasks)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = task.IsCompleted, VerticalAlignment = VerticalAlignment.Center };
            _taskChecks[task.Id] = check;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "完成待办：" + task.Text);
            check.Click += (_, _) => Save(w => { if (w == widget) task.IsCompleted = check.IsChecked == true; });
            var input = new TextBox { Text = task.Text, MaxLength = 500 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "待办内容");
            input.TextChanged += (_, _) => Save(w => { if (w == widget) task.Text = input.Text; });
            var remove = new Button { Content = "删除" };
            remove.Click += (_, _) => { Save(w => { if (w == widget) w.Tasks.Remove(task); }); RenderTasks(); };
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(input);
            var dateLabel = new TextBlock { Text = task.DateRangeText, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SecondaryTextBrush"], TextWrapping = TextWrapping.Wrap,
                Visibility = task.HasDateRange ? Visibility.Visible : Visibility.Collapsed };
            _taskDateLabels[task.Id] = dateLabel; content.Children.Add(dateLabel);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var dates = new Button { Content = new FontIcon { Glyph = "\uE787", FontSize = 14 } };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dates, "设置日期范围：" + task.Text);
            ToolTipService.SetToolTip(dates, "日期范围（可选）");
            dates.Click += (_, _) => _runtime.EditWidgetTaskDates(widget, task.Id);
            actions.Children.Add(dates); actions.Children.Add(remove);
            row.Children.Add(check); row.Children.Add(content); row.Children.Add(actions);
            Grid.SetColumn(content, 1); Grid.SetColumn(actions, 2);
            TaskRows.Children.Add(row);
        }
    }

    private void TranslationSettings_TextChanged(object sender, TextChangedEventArgs args) => SaveTranslationSettings();
    private void TranslationApiKey_PasswordChanged(object sender, RoutedEventArgs args) => SaveTranslationSettings();
    private void TranslationReuseConversation_Toggled(object sender, RoutedEventArgs args)
    {
        TranslationConversationIdle.IsEnabled = TranslationReuseConversation.IsOn;
        SaveTranslationSettings();
    }
    private void TranslationConversationIdle_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveTranslationSettings();
    private void SaveTranslationSettings()
    {
        if (_loading || _selected?.Kind != DesktopWidgetKind.Translation) return;
        _saving = true;
        try
        {
            var idle = TranslationConversationIdle.Value;
            _runtime.ConfigureWidgetTranslation(TranslationBaseUrl.Text, TranslationApiKey.Password, TranslationModel.Text, TranslationPrompt.Text,
                TranslationReuseConversation.IsOn, double.IsFinite(idle) ? (int)Math.Clamp(Math.Round(idle), 1, 1440) : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Error("无法保存翻译接口配置，请检查本机配置目录的访问权限。"); }
        finally { _saving = false; }
    }

    private async void ResetTranslationPrompt_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Translation } widget || TranslationPrompt.Text == WidgetTranslationSettings.DefaultPromptTemplate) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "恢复默认提示词？", Content = "当前自定义提示词将替换为默认翻译提示词。",
            PrimaryButtonText = "恢复默认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsLoaded || _selected != widget || !_runtime.State.Widgets.Contains(widget)) return;
        TranslationPrompt.Text = WidgetTranslationSettings.DefaultPromptTemplate;
    }

    private void CityQuery_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _citySearch?.Cancel();
        sender.ItemsSource = null;
        CitySearchStatus.Text = string.Empty;
    }

    private async void CityQuery_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is WeatherLocation location) await AddCityAsync(location);
        else await SearchCitiesAsync(args.QueryText);
    }

    private async Task SearchCitiesAsync(string query)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null) return;
        _citySearch?.Cancel();
        CityQuery.ItemsSource = null;
        if (string.IsNullOrWhiteSpace(query)) { CitySearchStatus.Text = "输入中文或英文城市名后搜索。"; return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _citySearch = request;
        CitySearchStatus.Text = "正在搜索城市…";
        Notice.IsOpen = false;
        try
        {
            var locations = await _runtime.SearchWidgetCitiesAsync(query, request.Token);
            if (IsLoaded && _selected == widget && !request.IsCancellationRequested)
            {
                CityQuery.ItemsSource = locations;
                CityQuery.IsSuggestionListOpen = locations.Count > 0;
                CitySearchStatus.Text = locations.Count == 0 ? "未找到城市，换个名称试试。" : "选择搜索结果即可添加城市。";
            }
        }
        catch (OperationCanceledException)
        { if (IsLoaded && _selected == widget && !request.IsCancellationRequested) CitySearchStatus.Text = "搜索超时，请重试。"; }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or System.IO.InvalidDataException or FormatException or KeyNotFoundException)
        { if (IsLoaded && _selected == widget && !request.IsCancellationRequested) CitySearchStatus.Text = "搜索失败，请检查网络后重试。"; }
        finally
        {
            if (_citySearch == request) _citySearch = null;
        }
    }

    private async Task AddCityAsync(WeatherLocation location)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null) return;
        _citySearch?.Cancel();
        CityQuery.ItemsSource = null;
        CityQuery.Text = string.Empty;
        CityQuery.IsSuggestionListOpen = false;
        CitySearchStatus.Text = string.Empty;
        _runtime.AddWidgetCity(widget, location);
        var work = _runtime.RefreshWidgetWeatherAsync(widget, force: false, cancellationToken: _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private async void SavedCities_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Weather } widget ||
            SavedCities.SelectedItem is not WidgetWeatherCity city || widget.ActiveCityId == city.Id || _lifetime is null) return;
        _runtime.SelectWidgetCity(widget, city.Id);
        var work = _runtime.RefreshWidgetWeatherAsync(widget, force: false, cancellationToken: _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private async void RemoveCity_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather, ActiveCity: { } city } widget) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "移除城市？", Content = $"将从这个组件移除“{city.Location.Name}”。",
            PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsLoaded || _selected != widget || !_runtime.State.Widgets.Contains(widget)) return;
        _runtime.RemoveWidgetCity(widget, city.Id);
        UpdateOnlineStatus();
        _ = _runtime.RefreshWidgetWeatherAsync(widget, force: false);
    }

    private async void LocateCity_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null || widget.IsLocating) return;
        Notice.IsOpen = false;
        var work = _runtime.LocateWidgetWeatherAsync(widget, _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private async void RefreshWeather_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null) return;
        var work = _runtime.RefreshWidgetWeatherAsync(widget, cancellationToken: _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private void UpdateOnlineStatus()
    {
        if (_selected is not { } widget) return;
        UpdateMonitorStatus();
        _loading = true;
        PositionLock.IsOn = widget.IsLocked;
        SyncSizeChoices(widget);
        SyncCustomSizeSettings(widget);
        SyncNetworkDisplaySettings(widget);
        MonitorPicker.SelectedItem = MonitorPicker.Items.OfType<MonitorChoice>().FirstOrDefault(m => m.Id == widget.MonitorId);
        foreach (var task in widget.Tasks)
        {
            if (_taskChecks.TryGetValue(task.Id, out var check)) check.IsChecked = task.IsCompleted;
            if (_taskDateLabels.TryGetValue(task.Id, out var label))
            {
                label.Text = task.DateRangeText;
                label.Visibility = task.HasDateRange ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        if (widget.Kind == DesktopWidgetKind.Weather)
        {
            DesktopWidgets.NormalizeWeatherCities(widget);
            var listed = SavedCities.Items.OfType<WidgetWeatherCity>().ToArray();
            if (!listed.SequenceEqual(widget.Cities)) SavedCities.ItemsSource = widget.Cities.ToArray();
            SavedCities.SelectedItem = widget.ActiveCity;
            RemoveCityButton.IsEnabled = widget.ActiveCity is not null;
            LocateCityButton.IsEnabled = !widget.IsLocating;
            LocationSettings.Visibility = Show(widget.LocationAccessDenied);
        }
        _loading = false;
        RefreshWeatherButton.IsEnabled = !widget.IsLoading && !widget.IsLocating && widget.Location is not null;
        CurrentCity.Text = widget.Location?.Name ?? "尚未添加城市";
        var weatherProvider = widget.Weather?.Provider ?? WidgetWeatherService.ProviderName;
        WeatherSource.Content = "数据来自" + weatherProvider;
        WeatherSource.NavigateUri = new Uri(weatherProvider == "Open-Meteo" ? "https://open-meteo.com/" : "https://privacy.mi.com/weather/zh_CN/");
        WeatherDetails.Text = widget.Weather is { } weather
            ? $"{weather.Temperature:0.#}°C · {weather.Description} · {weather.Minimum:0.#}° / {weather.Maximum:0.#}°\n更新于 {weather.FetchedAt:MM/dd HH:mm}"
            : "添加城市后显示天气";
        OperationStatus.Text = widget.IsLocating ? "正在获取 Windows 系统位置…" : widget.IsLoading ? "正在加载…" : widget.Error;
    }

    private async void RemoveWidget_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { } widget) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "移除组件？", Content = $"将从桌面移除“{widget.DisplayTitle}”及其保存的内容。",
            PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { _runtime.RemoveWidget(widget); ReloadInstances(); }
    }

    private sealed record SizeChoice(DesktopWidgetSize Size, string Label);
    private sealed record MonitorChoice(string Id, string Label);
}
