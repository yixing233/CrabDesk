using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopWidgetRuntimeTests
{
    [Theory]
    [InlineData(DesktopWidgetSize.Medium, false, 1)]
    [InlineData(DesktopWidgetSize.Medium, false, 1.25)]
    [InlineData(DesktopWidgetSize.Medium, true, 2)]
    [InlineData(DesktopWidgetSize.Landscape, false, 1)]
    [InlineData(DesktopWidgetSize.Landscape, true, 2)]
    public void AnalogClockSecondsToggleKeepsDialAndDateStable(DesktopWidgetSize size, bool light, double scale)
    {
        var (width, height) = DesktopWidgets.Dimensions(size);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = size };
        var text = light ? Color.FromArgb(31, 35, 41) : Color.White;
        var background = light ? Color.FromArgb(242, 244, 247) : Color.FromArgb(42, 45, 50);
        var body = new RectangleF(8, 8, (float)width - 16, (float)height - 16);
        Bitmap Render(bool seconds)
        {
            widget.ShowSeconds = seconds;
            var bitmap = new Bitmap((int)(width * scale), (int)(height * scale));
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(background);
            graphics.ScaleTransform((float)scale, (float)scale);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            DesktopBoxForm.DrawClockWidget(graphics, widget, body, text, text, new(2026, 10, 31, 3, 0, 30));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"clock-{size}-{(light ? "light" : "dark")}-{seconds}.png"));
            return bitmap;
        }
        using var without = Render(false);
        using var with = Render(true);
        var face = WidgetClockPresentation.Face(body.Width, body.Height);
        var cx = (body.X + face.X + face.Width / 2) * scale;
        var cy = (body.Y + face.Y + face.Height / 2) * scale;
        var changed = 0;
        for (var y = 0; y < with.Height; y++)
        for (var x = 0; x < with.Width; x++)
        {
            if (without.GetPixel(x, y) == with.GetPixel(x, y)) continue;
            changed++;
            // The horizontal layout also adds seconds to its numeric time.
            if (size == DesktopWidgetSize.Landscape &&
                x >= (body.X + face.X + face.Width + 18) * scale && x < body.Right * scale &&
                y >= (body.Y + body.Height / 2 - 29) * scale && y < (body.Y + body.Height / 2 + 5) * scale) continue;
            Assert.InRange(Math.Abs(x - cx), 0, 3 * scale);
            Assert.InRange(y, cy - face.Width / 2 * .17 * scale - 3 * scale, cy + face.Width / 2 * .82 * scale + 3 * scale);
        }
        Assert.True(changed > 10);
        Assert.Equal(background.ToArgb(), with.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public async Task TranslationUsesItsOwnEndpointCredentialAndModel()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        runtime.State.Settings.AiClassification = new() { BaseUrl = "https://organizer.test/v1", ApiKey = "organizer-key", Model = "organizer" };
        runtime.State.Settings.WidgetTranslation = new() { BaseUrl = "https://translator.test/v1", ApiKey = "translator-key", Model = "translator" };
        var requests = 0;
        using var client = new HttpClient(new TranslationRequestHandler(async request =>
        {
            requests++;
            Assert.Equal("https://translator.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("translator-key", request.Headers.Authorization!.Parameter);
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("translator", body.RootElement.GetProperty("model").GetString());
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}") };
        }));
        using var service = new WidgetTranslationService(client);
        Set(runtime, "_widgetTranslationService", service);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Text = "Hello" };
        runtime.State.Widgets.Add(widget);
        await runtime.TranslateWidgetAsync(widget);
        Assert.Equal("你好", widget.Translation);
        Assert.Equal(1, requests);
        runtime.State.Settings.WidgetTranslation = new();
        await runtime.TranslateWidgetAsync(widget);
        Assert.Equal(1, requests);
        Assert.Contains("翻译组件设置", widget.Error);
        Assert.Equal("organizer", runtime.State.Settings.AiClassification.Model);
    }

    [Fact]
    public async Task TranslationCredentialsMigrateAndPersistSeparatelyIncludingExplicitClearing()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.TranslationSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (runtime, _) = Fixture();
            using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
            Set(runtime, "_saveTimer", timer);
            var store = new JsonLayoutStore(root);
            Set(runtime, "_layoutStore", store);
            await File.WriteAllTextAsync(store.StatePath, "{\"SchemaVersion\":23,\"Settings\":{\"AiClassification\":{\"BaseUrl\":\"https://legacy.test/v1\",\"Model\":\"legacy-model\"}}}");
            Set(runtime, "<State>k__BackingField", await store.LoadAsync());
            runtime.State.Settings.AiClassification.ApiKey = "legacy-key";
            Call(runtime, "LoadWidgetTranslationApiKey");
            var keyPath = Path.Combine(root, "translation-api-key.dat");
            Assert.Equal("legacy-key", AiApiKeyStore.Load(keyPath));
            Assert.Equal("legacy-key", runtime.State.Settings.WidgetTranslation.ApiKey);
            using var request = new CancellationTokenSource();
            var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, IsLoading = true, Text = "Hello", Translation = "已有译文" };
            runtime.State.Widgets.Add(widget);
            Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource> { [widget.Id] = request });
            runtime.ConfigureWidgetTranslation(" https://translator.test/v1 ", "translator-key", " translator ");
            Assert.True(request.IsCancellationRequested);
            Assert.False(widget.IsLoading);
            Assert.Equal("已有译文", widget.Translation);
            Assert.Equal("legacy-model", runtime.State.Settings.AiClassification.Model);
            Assert.Equal("legacy-key", runtime.State.Settings.AiClassification.ApiKey);
            Assert.Equal("translator-key", AiApiKeyStore.Load(keyPath));
            Assert.DoesNotContain("translator-key", await File.ReadAllTextAsync(keyPath));
            await store.SaveAsync(runtime.State);
            Set(runtime, "<State>k__BackingField", await store.LoadAsync());
            runtime.State.Settings.AiClassification.ApiKey = "changed-organizer-key";
            Call(runtime, "LoadWidgetTranslationApiKey");
            Assert.Equal("translator-key", runtime.State.Settings.WidgetTranslation.ApiKey);
            Assert.Equal("translator", runtime.State.Settings.WidgetTranslation.Model);
            runtime.ConfigureWidgetTranslation("https://translator.test/v1", "", "translator");
            Assert.False(File.Exists(keyPath));
            await store.SaveAsync(runtime.State);
            Set(runtime, "<State>k__BackingField", await store.LoadAsync());
            runtime.State.Settings.AiClassification.ApiKey = "changed-organizer-key";
            Call(runtime, "LoadWidgetTranslationApiKey");
            Assert.Empty(runtime.State.Settings.WidgetTranslation.ApiKey);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ChangingOnlyTranslationPromptCancelsTheOldRequestAndPreservesIndependentSettings()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var settings = runtime.State.Settings.WidgetTranslation;
        settings.BaseUrl = "https://translator.test/v1";
        settings.Model = "translator";
        settings.ApiKey = "unchanged-key";
        runtime.State.Settings.AiClassification.CustomPrompt = "整理提示词";
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, IsLoading = true, Translation = "已有译文" };
        runtime.State.Widgets.Add(widget);
        using var pending = new CancellationTokenSource();
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource> { [widget.Id] = pending });

        runtime.ConfigureWidgetTranslation(settings.BaseUrl, settings.ApiKey, settings.Model, "译为{targetLanguage}，保持专业术语。");

        Assert.True(pending.IsCancellationRequested);
        Assert.False(widget.IsLoading);
        Assert.Equal("已有译文", widget.Translation);
        Assert.Equal("译为{targetLanguage}，保持专业术语。", settings.PromptTemplate);
        Assert.Equal("unchanged-key", settings.ApiKey);
        Assert.Equal("整理提示词", runtime.State.Settings.AiClassification.CustomPrompt);
        runtime.ConfigureWidgetTranslation(settings.BaseUrl, settings.ApiKey, "another-model");
        Assert.Equal("译为{targetLanguage}，保持专业术语。", settings.PromptTemplate);
        runtime.ConfigureWidgetTranslation(settings.BaseUrl, settings.ApiKey, settings.Model, "");
        Assert.Empty(settings.PromptTemplate);
        runtime.ConfigureWidgetTranslation(settings.BaseUrl, settings.ApiKey, settings.Model, WidgetTranslationSettings.DefaultPromptTemplate);
        Assert.Equal(WidgetTranslationSettings.DefaultPromptTemplate, settings.PromptTemplate);
    }

    [Fact]
    public async Task TranslationSessionSettingsResetContextCancelPendingAndSurviveOtherProfileEdits()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var requests = new Dictionary<Guid, CancellationTokenSource>();
        Set(runtime, "_widgetRequests", requests);
        var messageCounts = new List<int>();
        using var client = new HttpClient(new TranslationRequestHandler(async request =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            messageCounts.Add(body.RootElement.GetProperty("messages").GetArrayLength());
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"translated\"}}]}") };
        }));
        using var service = new WidgetTranslationService(client);
        Set(runtime, "_widgetTranslationService", service);
        var settings = runtime.State.Settings.WidgetTranslation;
        settings.BaseUrl = "https://translator.test/v1"; settings.Model = "test-model";
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Text = "first", TargetLanguage = "英语" };
        runtime.State.Widgets.Add(widget);
        await runtime.TranslateWidgetAsync(widget);
        runtime.UpdateWidgetTranslationInput(widget, "second");
        await runtime.TranslateWidgetAsync(widget);

        runtime.ConfigureWidgetTranslation(settings.BaseUrl, "", settings.Model, conversationIdleMinutes: 7);
        runtime.UpdateWidgetTranslationInput(widget, "third");
        await runtime.TranslateWidgetAsync(widget);
        runtime.SetWidgetTranslationLanguage(widget, "日语");
        runtime.SetWidgetTranslationLanguage(widget, "英语");
        runtime.UpdateWidgetTranslationInput(widget, "fourth");
        await runtime.TranslateWidgetAsync(widget);
        Assert.Equal(new[] { 2, 4, 2, 2 }, messageCounts);

        using var pending = new CancellationTokenSource();
        requests[widget.Id] = pending; widget.IsLoading = true;
        runtime.ConfigureWidgetTranslation(settings.BaseUrl, "", settings.Model, reuseConversation: false);
        Assert.True(pending.IsCancellationRequested);
        Assert.False(widget.IsLoading);
        Assert.False(settings.ReuseConversation);
        Assert.Equal(7, settings.ConversationIdleMinutes);
        runtime.ConfigureWidgetTranslation(settings.BaseUrl, "", "another-model");
        Assert.False(settings.ReuseConversation);
        Assert.Equal(7, settings.ConversationIdleMinutes);
    }

    private sealed class TranslationRequestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [Fact]
    public void NoteEditsNormalizeAndSaveWithoutChangingOtherWidgetContents()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var note = new DesktopWidget { Kind = DesktopWidgetKind.Note, Text = "old" };
        var translation = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Text = "source" };
        runtime.State.Widgets.AddRange([note, translation]);
        var changes = 0;
        runtime.Changed += (_, _) => changes++;
        var content = "中文\r\n第二行\r" + new string('字', 13000);
        runtime.UpdateWidgetNoteText(note, content);
        Assert.Equal(DesktopWidgets.NormalizeText(content), note.Text);
        Assert.Equal(1, changes);
        runtime.UpdateWidgetNoteText(note, content);
        runtime.UpdateWidgetNoteText(translation, "must not replace source");
        Assert.Equal("source", translation.Text);
        Assert.Equal(1, changes);
        runtime.UpdateWidgetNoteText(note, "");
        Assert.Empty(note.Text);
        runtime.State.Widgets.Remove(note);
        runtime.UpdateWidgetNoteText(note, "late edit");
        Assert.Empty(note.Text);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    public void DesktopNoteEditsStayInCardAndSurviveDismissal(double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id,
            Bounds = new(20, 20, 180, 180), IsLocked = true, Text = "已保存的便签" };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000);
        _ = surface.Handle;
        void Click() => Call(surface, "HandleWidgetClick", widget, new PointF(70, 90));
        Click();
        var editor = (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField("_noteInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.True(editor.Visible);
        var origin = surface.PointToScreen(new Point((int)(20 * scale), (int)(20 * scale)));
        Assert.InRange(editor.Left, origin.X, origin.X + (int)(180 * scale));
        Assert.InRange(editor.Right, editor.Left + 1, origin.X + (int)(180 * scale));
        Assert.InRange(editor.Top, origin.Y + (int)(28 * scale), origin.Y + (int)(180 * scale));
        Assert.True(editor.InputBox.AcceptsReturn);
        Assert.Equal(0, editor.InputBox.MaxLength);
        Assert.Equal(widget.Text, DesktopWidgets.NormalizeText(editor.InputBox.Text));
        editor.InputBox.Text = "桌面编辑\r\n中文与 emoji 📝";
        Assert.Equal("桌面编辑\n中文与 emoji 📝", widget.Text);
        Assert.False(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
        Assert.False(editor.HandleKey(System.Windows.Input.Key.ImeProcessed, System.Windows.Input.ModifierKeys.Control));
        Assert.False(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.Control));
        Assert.True(editor.Visible);
        Assert.True(editor.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));
        Assert.False(editor.Visible);
        Click();
        Assert.Equal(widget.Text, DesktopWidgets.NormalizeText(editor.InputBox.Text));
        surface.DismissWidgetInputs();
        Assert.False(editor.Visible);
        Assert.Equal("桌面编辑\n中文与 emoji 📝", widget.Text);
        Click();
        widget.Bounds = widget.Bounds with { X = 30 };
        surface.RefreshWidgets();
        Assert.False(editor.Visible);
        Click();
        runtime.State.Widgets.Remove(widget);
        surface.RefreshWidgets();
        Assert.False(editor.Visible);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Theory]
    [InlineData(DesktopWidgetKind.Clock)]
    [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.Weather)]
    public void ClickingWidgetContentDoesNotNavigateToSettings(DesktopWidgetKind kind) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var size = DesktopWidgets.DefaultSize(kind);
        var (width, height) = DesktopWidgets.Dimensions(size);
        var widget = new DesktopWidget { Kind = kind, Size = size, MonitorId = monitor.Id, Bounds = new(20, 20, width, height) };
        runtime.State.Widgets.Add(widget);
        DesktopWidget? requestedWeather = null;
        runtime.WidgetWeatherDetailsRequested += requested => requestedWeather = requested;
        using var surface = new DesktopBoxForm(runtime, monitor);
        var point = kind == DesktopWidgetKind.Translation
            ? new PointF(DesktopBoxForm.GetTranslationLayout(widget).Result.X + 8, DesktopBoxForm.GetTranslationLayout(widget).Result.Y + 8)
            : new PointF((float)(20 + width / 2), (float)(20 + (kind == DesktopWidgetKind.Weather ? height - 28 : height / 2)));
        Call(surface, "HandleWidgetClick", widget, point);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
        if (kind == DesktopWidgetKind.Weather) Assert.Same(widget, requestedWeather);
    });

    [Fact]
    public void TranslationEditorPreservesSelectionAndUndoWhenReopened() => OnSta(() =>
    {
        using var editor = new DesktopWidgetTextInput("翻译输入验证");
        var content = "第一行\n第二行";
        var bounds = new Rectangle(-12000, -12000, 220, 100);
        void Open() => editor.Present(bounds, content, Color.FromArgb(35, 49, 65), Color.White, Color.CornflowerBlue,
            value => content = value, cornerRadius: 8);
        Open();
        editor.InputBox.Select(0, 3);
        editor.InputBox.SelectedText = "新内容";
        Assert.True(editor.InputBox.CanUndo);
        editor.InputBox.Select(1, 2);
        editor.Dismiss();
        Open();
        Assert.Equal(1, editor.InputBox.SelectionStart); Assert.Equal(2, editor.InputBox.SelectionLength);
        Assert.True(editor.InputBox.CanUndo);
        editor.InputBox.Undo();
        Assert.Equal("第一行\n第二行", content);
    });

    [Theory]
    [InlineData("你好", 2, false)]
    [InlineData("你好", 2, true)]
    [InlineData("hello", 5, true)]
    [InlineData("你好🌍", 4, true)]
    [InlineData("你好\n世界", 2, true)]
    [InlineData("你好\r\n世界", 2, true)]
    [InlineData("", 0, false)]
    public void TranslationEditorClickAfterLineTextPlacesCaretAtLineEnd(string text, int expected, bool reopen) => OnSta(() =>
    {
        using var editor = new DesktopWidgetTextInput("翻译光标验证");
        void Open(PointF? point) => editor.Present(new(-12000, -12000, 240, 100), text, Color.Black, Color.White, Color.White,
            _ => { }, verticalOffset: 0, cornerRadius: 8, caretPoint: point);
        if (reopen) { Open(null); editor.Dismiss(); }
        Open(new(140, 16));
        Assert.Equal(expected, editor.InputBox.CaretIndex);
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(expected, editor.InputBox.CaretIndex);
    });

    [Theory]
    [InlineData("你好", .2, 0)]
    [InlineData("你好", .8, 1)]
    [InlineData("hello", .2, 0)]
    [InlineData("hello", .8, 1)]
    [InlineData("שלום", .2, 0)]
    [InlineData("שלום", .8, 1)]
    public void TranslationEditorClickChoosesNearestCaretEdge(string text, double fraction, int expected) => OnSta(() =>
    {
        using var editor = new DesktopWidgetTextInput("翻译光标验证");
        void Open(PointF? point) => editor.Present(new(-12000, -12000, 240, 100), text, Color.Black, Color.White, Color.White,
            _ => { }, verticalOffset: 0, cornerRadius: 8, caretPoint: point);
        Open(null);
        var leading = editor.InputBox.GetRectFromCharacterIndex(0, false);
        var trailing = editor.InputBox.GetRectFromCharacterIndex(0, true);
        Assert.False(leading.IsEmpty || trailing.IsEmpty);
        var fieldPoint = editor.InputBox.TranslatePoint(new(leading.X + (trailing.X - leading.X) * fraction, leading.Y + leading.Height / 2),
            (System.Windows.UIElement)editor.InputBox.Parent);
        editor.Dismiss();
        Open(new((float)fieldPoint.X, (float)fieldPoint.Y));
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(expected, editor.InputBox.CaretIndex);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslationEditorClickAfterScrollingUsesTheVisibleLine(bool wrapped) => OnSta(() =>
    {
        using var editor = new DesktopWidgetTextInput("翻译滚动定位验证");
        var text = wrapped ? string.Concat(Enumerable.Repeat("长文本会自动折行并保持正确的光标位置。", 30))
            : string.Join('\n', Enumerable.Range(0, 30).Select(index => $"第{index:00}行你好"));
        void Open(PointF? point, double offset) => editor.Present(new(-12000, -12000, 240, 100), text, Color.Black, Color.White, Color.White,
            _ => { }, verticalOffset: offset, cornerRadius: 8, caretPoint: point);
        Open(null, 0);
        // Finish presentation's queued viewport restore before simulating a scroll.
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        editor.InputBox.ScrollToLine(8);
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var line = editor.InputBox.GetFirstVisibleLineIndex();
        Assert.True(line > 0);
        line++; // Use a fully visible row rather than a potentially clipped top row.
        var end = editor.InputBox.GetCharacterIndexFromLineIndex(line) + editor.InputBox.GetLineText(line).TrimEnd('\r', '\n').Length;
        var trailing = editor.InputBox.GetRectFromCharacterIndex(end - 1, true);
        var point = editor.InputBox.TranslatePoint(new(editor.InputBox.ActualWidth - 9, trailing.Y + trailing.Height / 2),
            (System.Windows.UIElement)editor.InputBox.Parent);
        var offset = editor.InputBox.VerticalOffset;
        editor.Dismiss();
        Open(new((float)point.X, (float)point.Y), offset);
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(end, editor.InputBox.CaretIndex);
        Assert.InRange(Math.Abs(editor.InputBox.VerticalOffset - offset), 0, 1);
    });

    [Fact]
    public void EmptyTranslationDoesNotSubmitOrDismissAndImeConfirmationRemainsNative() => OnSta(() =>
    {
        using var editor = new DesktopWidgetTextInput("翻译输入验证");
        var requests = 0;
        editor.Present(new(-12000, -12000, 220, 100), "  ", Color.Black, Color.White, Color.CornflowerBlue,
            _ => { }, () => requests++, cornerRadius: 8);
        Assert.True(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.Control));
        Assert.True(editor.Visible); Assert.Equal(0, requests);
        Assert.False(editor.HandleKey(System.Windows.Input.Key.ImeProcessed, System.Windows.Input.ModifierKeys.Control));
        Assert.False(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
    });

    [Fact]
    public void TranslationSourceAndResultScrollIndependently() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, MonitorId = monitor.Id, Bounds = new(20, 20, 372, 180) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var layout = DesktopBoxForm.GetTranslationLayout(widget);
        Call(surface, "TryScrollWidget", new PointF(layout.Source.X + 12, layout.Source.Y + 12), -120);
        var sourceOffsets = (Dictionary<Guid, float>)typeof(DesktopBoxForm).GetField("_translationSourceScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var resultOffsets = (Dictionary<Guid, float>)typeof(DesktopBoxForm).GetField("_widgetScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.Equal(36, sourceOffsets[widget.Id]); Assert.False(resultOffsets.ContainsKey(widget.Id));
        Call(surface, "TryScrollWidget", new PointF(layout.Result.X + 12, layout.Result.Y + 12), -120);
        Assert.Equal(36, resultOffsets[widget.Id]); Assert.Equal(36, sourceOffsets[widget.Id]);
    });

    [Fact]
    public void WidgetCheckboxAndCityArrowStillWorkWithoutOpeningSettings() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var todo = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180), Tasks = [new() { Text = "任务" }] };
        var weather = new DesktopWidget { Kind = DesktopWidgetKind.Weather, MonitorId = monitor.Id, Bounds = new(220, 20, 180, 180) };
        var first = DesktopWidgets.AddWeatherCity(weather, new("北京", "", "中国", 39.9, 116.4));
        first.Weather = new(18, 12, 22, 2, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName };
        var second = DesktopWidgets.AddWeatherCity(weather, new("上海", "", "中国", 31.2, 121.5));
        second.Weather = first.Weather;
        weather.ActiveCityId = first.Id;
        runtime.State.Widgets.AddRange([todo, weather]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var todoList = DesktopBoxForm.GetTodoLayout(todo).List;
        Call(surface, "HandleWidgetClick", todo, new PointF(todoList.X + 10, todoList.Y + 14));
        Assert.True(todo.Tasks[0].IsCompleted);
        Call(surface, "HandleWidgetClick", weather, new PointF(385, 184));
        Assert.Equal(second.Id, weather.ActiveCityId);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void WeatherLocationControlsOpenManagementAndSwitchSavedCities(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var menuFont = new Font("Segoe UI", 10);
        Set(runtime, "_menuFont", menuFont);
        Set(runtime, "_lightTrayRenderer", new FluentMenuRenderer(false));
        Set(runtime, "_darkTrayRenderer", new FluentMenuRenderer(true));
        Set(runtime, "_configuredSubmenus", new ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object>());
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather, MonitorId = monitor.Id,
            Size = width == 372 ? DesktopWidgetSize.Large : DesktopWidgetSize.Medium, Bounds = new(20, 20, width, 180) };
        var first = DesktopWidgets.AddWeatherCity(widget, new("西安", "陕西", "中国", 34.3, 108.9));
        first.Weather = new(11, 8, 15, 2, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName };
        var second = DesktopWidgets.AddWeatherCity(widget, new("北京", "北京市", "中国", 39.9, 116.4)); second.Weather = first.Weather;
        widget.ActiveCityId = first.Id; runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var locations = DesktopBoxForm.WeatherLocationsButtonRectangle(widget);
        Assert.True(DesktopBoxForm.WidgetRectangle(widget).Contains(locations));
        Assert.False(locations.IntersectsWith(DesktopBoxForm.WeatherLocationButtonRectangle(widget)));
        Assert.False(locations.IntersectsWith(new RectangleF(DesktopBoxForm.WidgetRectangle(widget).Right - 32, (float)widget.Bounds.Y, 28, 28)));
        DesktopWidget? requested = null;
        runtime.WidgetWeatherLocationsRequested += selected => requested = selected;
        var point = new PointF(locations.X + 12, locations.Y + 12);
        Call(surface, "HandleWidgetClick", widget, point); Assert.Same(widget, requested);
        Call(surface, "HandleWidgetMouseDown", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0), point);
        Assert.False((bool)typeof(DesktopBoxForm).GetField("_widgetCanDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!);
        Call(surface, "FinishWidgetPress", false);
        using var cities = (System.Windows.Forms.ContextMenuStrip)Call(surface, "ShowWeatherCityMenu", widget)!;
        var entries = cities.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
        Assert.True(entries.Single(item => item.Text == first.DisplayName).Checked);
        entries.Single(item => item.Text == second.DisplayName).PerformClick();
        Assert.Equal(second.Id, widget.ActiveCityId); Assert.Same(second.Weather, widget.Weather);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
        cities.Close();
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void CalendarHeaderButtonsFitAndTodayReturnsToTheCurrentMonth(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var header = DesktopBoxForm.GetCalendarHeaderLayout(widget);
        var card = DesktopBoxForm.WidgetRectangle(widget);
        var menu = new RectangleF(card.Right - 32, card.Y, 28, 28);
        var controls = new[] { header.Title, header.Previous, header.Today, header.Next, menu };
        foreach (var control in controls) Assert.True(card.Contains(control));
        for (var i = 0; i < controls.Length; i++)
            for (var j = i + 1; j < controls.Length; j++) Assert.False(controls[i].IntersectsWith(controls[j]));
        void Click(RectangleF rectangle) => Call(surface, "HandleWidgetClick", widget, new PointF(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2));
        DateOnly Month() => (DateOnly)Call(surface, "WidgetMonth", widget)!;
        var current = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        Click(header.Previous); Assert.Equal(current.AddMonths(-1), Month());
        Click(header.Title); Assert.Equal(current.AddMonths(-1), Month());
        Click(header.Today); Assert.Equal(current, Month());
        Click(header.Next); Assert.Equal(current.AddMonths(1), Month());
        Click(header.Today); Assert.Equal(current, Month());
        var point = new PointF(header.Today.X + 10, header.Today.Y + 10);
        Call(surface, "HandleWidgetMouseDown", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0), point);
        Call(surface, "HandleWidgetMouseMove", new PointF(point.X + 18, point.Y + 18));
        Assert.Equal(new LayoutRect(20, 20, width, 180), widget.Bounds);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void CalendarHoverMatchesAllFortyTwoCellsAcrossMonthAndYearBoundaries(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var months = (Dictionary<Guid, DateOnly>)typeof(DesktopBoxForm).GetField("_widgetMonths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        foreach (var month in new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 12, 1) })
        {
            months[widget.Id] = month;
            var days = DesktopWidgets.CalendarDays(month);
            for (var index = 0; index < days.Count; index++)
            {
                var cell = DesktopBoxForm.CalendarDayRectangle(widget, index);
                var point = new PointF(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
                Assert.Equal(days[index], DesktopBoxForm.CalendarDateAtPoint(widget, month, point));
            }
        }
        var february = new DateOnly(2026, 2, 1); months[widget.Id] = february;
        var selected = DesktopWidgets.CalendarDays(february).ToList().IndexOf(new(2026, 2, 17));
        var target = DesktopBoxForm.CalendarDayRectangle(widget, selected);
        Call(surface, "HandleWidgetMouseMove", new PointF(target.X + 3, target.Y + 3));
        string HoverText() => (string)typeof(DesktopBoxForm).GetField("_calendarToolTipText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.Contains("春节", HoverText());
        var next = DesktopBoxForm.GetCalendarHeaderLayout(widget).Next;
        Call(surface, "HandleWidgetClick", widget, new PointF(next.X + 3, next.Y + 3));
        Assert.Empty(HoverText());
        Assert.Null(DesktopBoxForm.CalendarDateAtPoint(widget, february, new PointF(next.X, next.Y)));
        Assert.Null(DesktopBoxForm.CalendarDateAtPoint(widget, february, new PointF(target.X, 0)));
    });

    [Fact]
    public void LargeCalendarRetainsTheSmallGridAndNavigationAndDoesNotHitDaysInTheCountdownColumn() => OnSta(() =>
    {
        var small = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, Bounds = new(30, 40, 180, 180) };
        var large = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, Size = DesktopWidgetSize.Large, Bounds = new(30, 40, 372, 180) };
        Assert.Equal(DesktopBoxForm.GetCalendarHeaderLayout(small), DesktopBoxForm.GetCalendarHeaderLayout(large));
        for (var index = 0; index < 42; index++) Assert.Equal(DesktopBoxForm.CalendarDayRectangle(small, index), DesktopBoxForm.CalendarDayRectangle(large, index));
        Assert.Null(DesktopBoxForm.CalendarDateAtPoint(large, new(2026, 10, 1), new((float)large.Bounds.X + 230, 100)));
        var (runtime, monitor) = Fixture();
        large.MonitorId = monitor.Id; runtime.State.Widgets.Add(large);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var events = CalendarDateDetails.GetUpcomingEvents(DateOnly.FromDateTime(DateTime.Today));
        var next = DesktopBoxForm.GetCalendarHeaderLayout(large).Next;
        Call(surface, "HandleWidgetClick", large, new PointF(next.X + 3, next.Y + 3));
        Assert.Equal(events, CalendarDateDetails.GetUpcomingEvents(DateOnly.FromDateTime(DateTime.Today)));
        using var bitmap = new Bitmap(372, 180);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(37, 44, 59));
            graphics.TranslateTransform(-30, -40);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Call(surface, "DrawWidget", graphics, large);
        }
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, "calendar-countdown-layout.png"));
    });

    [Fact]
    public void CalendarHoverPreservesTheAttachedInputSurfaceHandleAndParent() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180) };
        runtime.State.Widgets.Add(widget);
        using var parent = new System.Windows.Forms.Form();
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.AttachToDesktop(parent.Handle);
        var handle = surface.Handle;
        Assert.Equal(parent.Handle, CalendarTestGetParent(handle));
        var cell = DesktopBoxForm.CalendarDayRectangle(widget, 20);
        Call(surface, "HandleWidgetMouseMove", new PointF(cell.X + 3, cell.Y + 3));
        Assert.Equal(handle, surface.Handle);
        Assert.Equal(parent.Handle, CalendarTestGetParent(surface.Handle));
    });

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetParent")]
    private static extern IntPtr CalendarTestGetParent(IntPtr hwnd);

    [Theory]
    [InlineData(true, 1d)]
    [InlineData(true, 1.25d)]
    [InlineData(false, 1d)]
    [InlineData(false, 1.5d)]
    public void CalendarDetailsAreIncludedInTheDesktopFrameAndDoNotChangeTheInputRegion(bool acrylic, double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180) };
        runtime.State.Widgets.Add(widget);
        using var parent = new System.Windows.Forms.Form { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = Point.Empty };
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.AttachToDesktop(parent.Handle);
        surface.SetAcrylicBackground(acrylic);
        Bitmap? frame = null;
        surface.SetAcrylicFramePresenter((bitmap, _, _) => { frame?.Dispose(); frame = (Bitmap)bitmap.Clone(); });
        try
        {
            Assert.True(surface.RefreshWorkspace(), surface.LayerDiagnostic);
            using var before = (Bitmap)frame!.Clone();
            Call(surface, "PresentCalendarToolTip", widget, new DateOnly(2026, 2, 17), new PointF(100, 100));
            var bounds = (RectangleF)typeof(DesktopBoxForm).GetField("_calendarToolTipBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
            Assert.InRange(bounds.Left, 111, 113);
            Assert.InRange(bounds.Top, 117, 119);
            Assert.False(bounds.Contains(new PointF(100, 100)));
            var x = (int)((bounds.Right - 16) * scale); var y = (int)((bounds.Bottom - 16) * scale);
            Assert.Equal(0, before.GetPixel(x, y).A);
            Assert.InRange(frame!.GetPixel(x, y).A, 240, 255);
            Assert.Contains("春节", surface.AccessibleDescription);
            Assert.True(surface.ValidateWindowRegion());
            using var iconFrame = new Bitmap(frame.Width, frame.Height);
            using (var graphics = Graphics.FromImage(iconFrame))
            {
                graphics.ScaleTransform((float)scale, (float)scale);
                surface.RenderStaticOnIconLayer(graphics, new(0, 0, 1000, 700));
            }
            Assert.InRange(iconFrame.GetPixel(x, y).A, 240, 255);
            Call(surface, "HideCalendarHover");
            Assert.Equal(0, frame!.GetPixel(x, y).A);
            Assert.Null(surface.AccessibleDescription);
            Assert.True(surface.ValidateWindowRegion());
        }
        finally { frame?.Dispose(); }
    });

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowRgn")]
    private static extern int RecoveryTestSetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [Fact]
    public void LostInputRegionRecoversWithoutRestartingOrLosingTheSurfaceParent() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 84) };
        runtime.State.Widgets.Add(widget);
        using var parent = new System.Windows.Forms.Form { StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Bounds = new Rectangle(-12000, -12000, 1000, 700) };
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.AttachToDesktop(parent.Handle);
        surface.SetAcrylicFramePresenter((_, _, _) => { });
        parent.Show(); surface.Show();
        Assert.True(surface.RefreshWorkspace(), surface.LayerDiagnostic);
        var handle = surface.Handle;
        Assert.True(surface.ValidateWindowRegion());
        var clock = (System.Windows.Forms.Timer)typeof(DesktopBoxForm).GetField("_widgetTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            RecoveryTestSetWindowRgn(handle, IntPtr.Zero, false);
            Assert.False(surface.ValidateWindowRegion());
            Call(surface, "HandleRegionFailure", "GetWindowRgnBox failed error=0");
            Assert.False(surface.Visible);
            System.Windows.Forms.Application.DoEvents();
            Assert.Equal(handle, surface.Handle);
            Assert.Equal(parent.Handle, CalendarTestGetParent(handle));
            Assert.True(surface.Visible);
            Assert.True(surface.ValidateWindowRegion());
            Assert.True(clock.Enabled);
            Assert.Contains(widget, runtime.State.Widgets);
        }
    });

    [Fact]
    public void TodoTaskUpdatesKeepCompletionAndRejectBlankOrRemovedItems()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo };
        runtime.State.Widgets.Add(widget);
        Assert.Null(runtime.SaveWidgetTask(widget, null, "  \n "));
        var task = runtime.SaveWidgetTask(widget, null, " 第一件\n事情 ")!;
        Assert.Equal("第一件 事情", task.Text);
        task.IsCompleted = true;
        Assert.Same(task, runtime.SaveWidgetTask(widget, task.Id, "修改后的事项"));
        Assert.True(task.IsCompleted); Assert.Single(widget.Tasks);
        Assert.Null(runtime.SaveWidgetTask(widget, Guid.NewGuid(), "不能恢复已删除的事项"));
        runtime.State.Widgets.Remove(widget);
        Assert.Null(runtime.SaveWidgetTask(widget, null, "不能恢复已移除的组件"));
        Assert.Single(widget.Tasks);
    }

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void TodoRowDeleteRemovesTheCorrectScrolledTaskWithoutChangingOtherTasks(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180),
            Tasks = Enumerable.Range(0, 8).Select(i => new WidgetTodoItem { Text = $"任务 {i}",
                EndDate = i % 2 == 0 ? new DateOnly(2026, 11, 3) : null }).ToList() };
        runtime.State.Widgets.Add(widget);
        var original = widget.Tasks.ToArray();
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var list = DesktopBoxForm.GetTodoLayout(widget).List;
        for (var i = 0; i < 2; i++) Call(surface, "TryScrollWidget", new PointF(list.X + 30, list.Y + 10), -120);
        var target = original[2];
        var delete = surface.TodoDeleteRectangle(widget, 0);
        Assert.True(list.Contains(delete));
        Assert.False(delete.IntersectsWith(surface.TodoEditRectangle(widget, 0)));
        Assert.False(delete.IntersectsWith(surface.TodoDatesRectangle(widget, 0)));
        Call(surface, "HandleWidgetMouseMove", new PointF(delete.X + 12, delete.Y + 12));
        using (var bitmap = new Bitmap(width, 180))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(37, 44, 59)); graphics.TranslateTransform(-20, -20);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Call(surface, "DrawWidget", graphics, widget);
            }
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"todo-delete-layout-{width}.png"));
        }
        Call(surface, "HandleWidgetClick", widget, new PointF(list.Right - 16, list.Top + 14));
        Assert.Equal(original, widget.Tasks);
        ConfirmTodoDeletion(surface);
        Assert.DoesNotContain(target, widget.Tasks);
        Assert.Equal(original.Where(task => task != target), widget.Tasks);
        Assert.False(original[0].IsCompleted);
        Assert.Equal(new DateOnly(2026, 11, 3), original[0].EndDate);
        Assert.Null(runtime.SaveWidgetTask(widget, target.Id, "删除前的编辑内容"));
        for (var i = 0; i < original.Length - 1; i++)
        {
            Call(surface, "HandleWidgetClick", widget, new PointF(list.Right - 16, list.Top + 14));
            ConfirmTodoDeletion(surface);
        }
        Assert.Empty(widget.Tasks);
    });

    private static TodoDeleteConfirmationCard TodoDeleteCard(DesktopBoxForm surface) =>
        (TodoDeleteConfirmationCard)typeof(DesktopBoxForm).GetField("_todoDeleteCard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;

    private static FluentContextMenuStrip TodoDeleteMenu(DesktopBoxForm surface) =>
        (FluentContextMenuStrip)typeof(DesktopBoxForm).GetField("_todoDeleteMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;

    private static void ConfirmTodoDeletion(DesktopBoxForm surface)
    {
        TodoDeleteMenu(surface).AnimationsEnabled = false;
        TodoDeleteCard(surface).DeleteButton.PerformClick();
        System.Windows.Forms.Application.DoEvents();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("outside")]
    [InlineData("removed")]
    [InlineData("moved")]
    [InlineData("paused")]
    public void TodoDeleteConfirmationDismissesWithoutDeletingOtherTasks(string action) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var target = new WidgetTodoItem { Text = "需要确认的待办" };
        var other = new WidgetTodoItem { Text = "另一条待办", EndDate = new(2026, 11, 3) };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180), Tasks = [target, other] };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var anchor = surface.TodoDeleteRectangle(widget, 0);
        Call(surface, "HandleWidgetClick", widget, new PointF(anchor.X + 12, anchor.Y + 12));
        var menu = TodoDeleteMenu(surface); var card = TodoDeleteCard(surface);
        Assert.True(menu.Visible); Assert.Equal(new[] { target, other }, widget.Tasks);
        Assert.Contains(card.Controls.OfType<System.Windows.Forms.Label>(), label => label.Text == target.Text);
        Assert.True(new Rectangle((int)monitor.PixelWorkArea.X, (int)monitor.PixelWorkArea.Y,
            (int)monitor.PixelWorkArea.Width, (int)monitor.PixelWorkArea.Height).Contains(menu.Bounds));
        switch (action)
        {
            case "cancel": card.CancelButton.PerformClick(); break;
            case "escape": typeof(System.Windows.Forms.ToolStripDropDown).GetMethod("ProcessDialogKey", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(menu, [System.Windows.Forms.Keys.Escape]); break;
            case "outside": menu.Close(System.Windows.Forms.ToolStripDropDownCloseReason.AppClicked); break;
            case "removed": widget.Tasks.Remove(target); surface.RefreshWidgets(); break;
            case "moved": widget.Bounds = widget.Bounds with { X = 250 }; surface.RefreshWidgets(); break;
            case "paused": typeof(CrabDeskRuntime).GetProperty(nameof(CrabDeskRuntime.IsPaused))!.SetValue(runtime, true); surface.RefreshWidgets(); break;
        }
        Assert.False(menu.Visible);
        card.DeleteButton.PerformClick();
        Assert.Equal(action == "removed" ? new[] { other } : new[] { target, other }, widget.Tasks);
        Assert.Equal(new DateOnly(2026, 11, 3), other.EndDate);
        System.Windows.Forms.Application.DoEvents();
    });

    [Fact]
    public void TodoDeleteConfirmationKeepsTheTaskIdentityWhenRowsReorder() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var target = new WidgetTodoItem { Text = "删除目标" }; var other = new WidgetTodoItem { Text = "保留目标" };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, 372, 180), Tasks = [target, other] };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor); surface.Location = new(-12000, -12000); _ = surface.Handle;
        var anchor = surface.TodoDeleteRectangle(widget, 0);
        Call(surface, "HandleWidgetClick", widget, new PointF(anchor.X + 12, anchor.Y + 12));
        target.IsCompleted = true; surface.RefreshWidgets();
        ConfirmTodoDeletion(surface);
        Assert.Same(other, Assert.Single(widget.Tasks));
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void TodoRowsStayTransparentUntilHoveredAndUseRoundedChecks(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.CornerRadius = 20;
        runtime.State.Settings.Appearance.IconHoverCornerRadius = 0;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(0, 0, width, 180),
            Tasks = [new() { Text = "测试", StartDate = new(2026, 10, 29), EndDate = new(2026, 10, 31) },
                new() { Text = "整理资料" }, new() { Text = "检查结果" }, new() { Text = "同步进度" }, new() { Text = "准备明天" },
                new() { Text = "已经完成的事项", IsCompleted = true }] };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor); _ = surface.Handle;
        var list = DesktopBoxForm.GetTodoLayout(widget).List;
        Bitmap Render(string state)
        {
            var bitmap = new Bitmap(width, 180);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(37, 44, 59));
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Call(surface, "DrawWidget", graphics, widget);
            }
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"todo-refined-{width}-{state}.png"));
            return bitmap;
        }
        using var idle = Render("idle");
        var background = idle.GetPixel((int)list.Right - 84, (int)list.Bottom - 1);
        Assert.Equal(background, idle.GetPixel((int)list.Right - 84, (int)list.Top + 15));
        Assert.Equal(background, idle.GetPixel((int)list.X + 5, (int)list.Y + 8));
        Call(surface, "HandleWidgetMouseMove", new PointF(list.X + 32, list.Y + 15));
        using var hovered = Render("hovered");
        Assert.NotEqual(background, hovered.GetPixel((int)list.Right - 84, (int)list.Top + 15));
        Assert.Equal(idle.GetPixel((int)list.Right - 84, (int)list.Top + 46 + 15),
            hovered.GetPixel((int)list.Right - 84, (int)list.Top + 46 + 15));
        for (var i = 0; i < 5; i++) Call(surface, "TryScrollWidget", new PointF(list.X + 32, list.Y + 15), -120);
        var group = surface.TodoCompletedHeaderRectangle(widget);
        Assert.False(group.IsEmpty);
        Call(surface, "HandleWidgetClick", widget, new PointF(group.X + 10, group.Y + 14));
        var completedPoint = new PointF(list.X + 32, surface.TodoCompletedHeaderRectangle(widget).Bottom + 15);
        Assert.Equal(widget.Tasks.Last().Id, (Guid?)Call(surface, "TodoTaskAtPoint", widget, completedPoint));
        using var expanded = Render("expanded");
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void TodoSectionsEditDeleteAndReopenTheSelectedTaskAfterGrouping(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var first = new WidgetTodoItem { Text = "已完成的第一项", IsCompleted = true };
        var pending = new WidgetTodoItem { Text = "测试事项", StartDate = new(2026, 10, 29), EndDate = new(2026, 10, 31) };
        var last = new WidgetTodoItem { Text = "已完成的最后一项", IsCompleted = true };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180), Tasks = [first, pending, last] };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var list = DesktopBoxForm.GetTodoLayout(widget).List;
        void Click(RectangleF rectangle) => Call(surface, "HandleWidgetClick", widget, new PointF(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2));
        Guid? TaskAt(float y) => (Guid?)Call(surface, "TodoTaskAtPoint", widget, new PointF(list.X + 32, list.Top + y));
        DesktopWidgetTextInput Editor() => (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField("_todoInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.Equal(pending.Id, TaskAt(14)); Assert.Null(TaskAt(90));
        Click(surface.TodoCompletedHeaderRectangle(widget));
        Assert.Equal(first.Id, TaskAt(46 + 28 + 14));
        // Completing the sole pending item moves it into the expanded section.
        Call(surface, "HandleWidgetClick", widget, new PointF(list.X + 10, list.Y + 14));
        Assert.True(pending.IsCompleted);
        Assert.Equal(first.Id, TaskAt(28 + 14)); Assert.Equal(pending.Id, TaskAt(28 + 30 + 14));
        Click(surface.TodoEditRectangle(widget, 2));
        var editor = Editor(); Assert.True(editor.Visible);
        var painted = (RectangleF)Call(surface, "TodoTextRectangle", widget, 2, false)!;
        var hovered = (RectangleF)Call(surface, "TodoTextRectangle", widget, 2, true)!;
        Assert.Equal(painted.Location, hovered.Location);
        Assert.Equal(surface.PointToScreen(new Point((int)painted.X, (int)painted.Y)), editor.Location);
        editor.InputBox.Text = "编辑后的测试事项";
        editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None);
        Assert.Equal("编辑后的测试事项", pending.Text); Assert.True(pending.IsCompleted);
        Assert.Equal(new DateOnly(2026, 10, 31), pending.EndDate);
        Click(surface.TodoDeleteRectangle(widget, 1));
        Assert.Equal(new[] { first, pending, last }, widget.Tasks);
        ConfirmTodoDeletion(surface);
        Assert.Equal(new[] { pending, last }, widget.Tasks);
        Call(surface, "HandleWidgetClick", widget, new PointF(list.X + 10, list.Y + 28 + 14));
        Assert.False(pending.IsCompleted); Assert.True(last.IsCompleted);
        Assert.Equal(pending.Id, TaskAt(14));
        Click(surface.TodoCompletedHeaderRectangle(widget)); Assert.Null(TaskAt(100));
        Click(DesktopBoxForm.GetTodoLayout(widget).Add); editor.InputBox.Text = "新添加的事项";
        editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None);
        var added = widget.Tasks.Last(); Assert.Equal(added.Id, TaskAt(46 + 14));
        Assert.Equal(new[] { pending, last, added }, widget.Tasks);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void TodoListAddsEditsAndCancelsInsideTheDesktopCard(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var layout = DesktopBoxForm.GetTodoLayout(widget);
        var bounds = DesktopBoxForm.WidgetRectangle(widget);
        Assert.True(bounds.Contains(layout.List)); Assert.True(bounds.Contains(layout.Add));
        Assert.False(layout.List.IntersectsWith(layout.Add));
        Assert.True(layout.Add.Bottom <= bounds.Top + 28);
        Assert.False(layout.Add.IntersectsWith(new RectangleF(bounds.Right - 32, bounds.Y, 28, 28)));
        void Click(RectangleF rectangle) => Call(surface, "HandleWidgetClick", widget, new PointF(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2));
        DesktopWidgetTextInput Editor() => (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField("_todoInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Click(layout.Add);
        var editor = Editor();
        Assert.True(editor.Visible); Assert.False(editor.InputBox.AcceptsReturn);
        editor.InputBox.Text = "桌面新建的事项";
        Assert.False(editor.HandleKey(System.Windows.Input.Key.ImeProcessed, System.Windows.Input.ModifierKeys.None));
        Assert.True(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
        Assert.False(editor.Visible);
        var task = Assert.Single(widget.Tasks); Assert.Equal("桌面新建的事项", task.Text);
        Click(layout.Add); editor.InputBox.Text = "取消添加";
        editor.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
        Assert.Single(widget.Tasks);
        var rowText = surface.TodoEditRectangle(widget, 0);
        var rowHover = new PointF(layout.List.X + 32, layout.List.Y + 14);
        Call(surface, "HandleWidgetMouseMove", rowHover);
        Assert.Equal(task.Id, typeof(DesktopBoxForm).GetField("_hoveredTodoTaskId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface));
        Call(surface, "HandleWidgetMouseMove", new PointF(bounds.Right + 20, bounds.Bottom + 20));
        Assert.Null(typeof(DesktopBoxForm).GetField("_hoveredTodoTaskId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface));
        Click(rowText); editor.InputBox.Text = "取消修改";
        editor.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
        Assert.Equal("桌面新建的事项", task.Text);
        Click(rowText); editor.InputBox.Text = "修改后的事项";
        editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None);
        Assert.Equal("修改后的事项", task.Text);
        Call(surface, "HandleWidgetClick", widget, new PointF(layout.List.X + 10, layout.List.Y + 14));
        Assert.True(task.IsCompleted);
        Click(surface.TodoCompletedHeaderRectangle(widget));
        Click(surface.TodoEditRectangle(widget, 1)); editor.InputBox.Text = "失焦时保存"; surface.DismissWidgetInputs();
        Assert.Equal("失焦时保存", task.Text); Assert.True(task.IsCompleted);
        for (var i = 0; i < 5; i++) runtime.SaveWidgetTask(widget, null, "更多事项 " + i);
        Click(layout.Add); editor.InputBox.Text = "完整列表末尾添加";
        editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None);
        Assert.Equal("完整列表末尾添加", widget.Tasks.Last().Text);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Fact]
    public void RepeatedAddReusesTheExistingWidgetAndPreservesItsContentsAndPosition()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var note = runtime.AddWidget(DesktopWidgetKind.Note);
        note.Text = "保留这条便签";
        note.Title = "工作记录";
        note.IsLocked = true;
        var bounds = note.Bounds;
        var monitorId = note.MonitorId;
        for (var index = 0; index < 5; index++)
            Assert.Same(note, runtime.AddWidget(DesktopWidgetKind.Note, "another-monitor"));
        Assert.Same(note, Assert.Single(runtime.State.Widgets));
        Assert.Equal("保留这条便签", note.Text);
        Assert.Equal("工作记录", note.Title);
        Assert.True(note.IsLocked);
        Assert.Equal(bounds, note.Bounds);
        Assert.Equal(monitorId, note.MonitorId);
        var clock = runtime.AddWidget(DesktopWidgetKind.Clock);
        Assert.NotSame(note, clock);
        Assert.Equal(2, runtime.State.Widgets.Count);
    }

    [Fact]
    public void RemovingAWidgetAllowsAddingItsKindAgainAndKeepsLegacyDuplicatesUntilRemoved()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var first = runtime.AddWidget(DesktopWidgetKind.Note);
        var legacy = new DesktopWidget { Kind = DesktopWidgetKind.Note, Text = "旧版本的另一条便签" };
        runtime.State.Widgets.Add(legacy);
        Assert.Same(first, runtime.AddWidget(DesktopWidgetKind.Note));
        Assert.Equal(2, runtime.State.Widgets.Count);
        runtime.RemoveWidget(first);
        Assert.Same(legacy, runtime.AddWidget(DesktopWidgetKind.Note));
        Assert.Equal("旧版本的另一条便签", legacy.Text);
        runtime.RemoveWidget(legacy);
        var replacement = runtime.AddWidget(DesktopWidgetKind.Note);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.NotEqual(legacy.Id, replacement.Id);
        Assert.Same(replacement, Assert.Single(runtime.State.Widgets));
    }

    [Fact]
    public void WidgetOnlyDesktopHasInteractiveCardsAndTransparentGaps() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Widgets.Add(new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Small,
            MonitorId = monitor.Id, Bounds = new(20, 20, 180, 84) });
        using var surface = new DesktopBoxForm(runtime, monitor);
        _ = surface.Handle;
        Assert.True((bool)Call(surface, "UpdateWindowRegion")!);
        Assert.True(surface.ValidateWindowRegion());
        Assert.True(surface.IsPointOverBox(new Point(80, 60)));
        Assert.False(surface.IsPointOverBox(new Point(350, 350)));
        Assert.True((bool)Call(surface, "IsInteractivePointSafe", new PointF(80, 60))!);
        Assert.False((bool)Call(surface, "IsInteractivePointSafe", new PointF(350, 350))!);
        Assert.Single(surface.GetAcrylicRegions());
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8, 1.25)]
    [InlineData(20, 2)]
    public void SixWidgetKindsRenderAtSupportedDpiWithoutPaintingOutsideTheirCards(double radius, double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        runtime.State.Settings.Appearance.CornerRadius = radius;
        var kinds = Enum.GetValues<DesktopWidgetKind>();
        for (var index = 0; index < kinds.Length; index++)
        {
            var widget = new DesktopWidget { Kind = kinds[index], MonitorId = monitor.Id, Size = DesktopWidgets.DefaultSize(kinds[index]), Text = "第一行\n第二行", Translation = "Translated text" };
            var (width, height) = DesktopWidgets.Dimensions(widget.Size);
            widget.Bounds = new(20 + index % 2 * 400, 20 + index / 2 * 200, width, height);
            widget.Tasks.Add(new() { Text = "可勾选的任务", IsCompleted = true });
            widget.Location = new("北京", "", "中国", 39.9, 116.4);
            widget.Weather = new(18, 10, 24, 2, DateTimeOffset.Now);
            runtime.State.Widgets.Add(widget);
        }
        using var surface = new DesktopBoxForm(runtime, monitor);
        using var bitmap = new Bitmap((int)(820 * scale), (int)(620 * scale));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Magenta);
        graphics.ScaleTransform((float)scale, (float)scale);
        surface.RenderOnIconLayer(graphics, new RectangleF(0, 0, 820, 620));
        foreach (var widget in runtime.State.Widgets)
        {
            Assert.NotEqual(Color.Magenta.ToArgb(), bitmap.GetPixel((int)((widget.Bounds.X + 10) * scale), (int)((widget.Bounds.Y + 40) * scale)).ToArgb());
        }
        Assert.Equal(Color.Magenta.ToArgb(), bitmap.GetPixel((int)(10 * scale), (int)(10 * scale)).ToArgb());
    });

    [Fact]
    public void AcrylicRegionUsesMonitorPixelsAndDpi() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(1.5);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(40, 60, 180, 84) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var region = Assert.Single(surface.GetAcrylicRegions());
        Assert.Equal(new RectangleF(60, 90, 270, 126), region.ScreenBounds);
    });

    [Fact]
    public void DesktopWidgetNavigationIsConsumedOnceAndCanReturnToOverview()
    {
        var (runtime, _) = Fixture();
        var id = Guid.NewGuid();
        runtime.OpenWidgetEditor(id);
        Assert.True(runtime.TryConsumeWidgetNavigation(out var target));
        Assert.Equal(id, target);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
        runtime.OpenWidgetEditor();
        Assert.True(runtime.TryConsumeWidgetNavigation(out var overview));
        Assert.Null(overview);
    }

    [Fact]
    public async Task SwitchingCitiesCancelsOldRequestWithoutClearingNewLoadingStateOrCache()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var client = new HttpClient(new DeferredWeatherHandler(() => Interlocked.Increment(ref count) == 1 ? first.Task : second.Task));
        using var service = new WidgetWeatherService(client);
        Set(runtime, "_widgetWeatherService", service);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather };
        var beijing = DesktopWidgets.AddWeatherCity(widget, new("北京", "", "中国", 39.9, 116.4) { ProviderKey = "weathercn:101010100" });
        var shanghai = DesktopWidgets.AddWeatherCity(widget, new("上海", "", "中国", 31.2, 121.5) { ProviderKey = "weathercn:101020100" });
        widget.ActiveCityId = beijing.Id;
        beijing.Weather = new(18, 12, 22, 2, DateTimeOffset.Now);
        runtime.State.Widgets.Add(widget);
        var oldRequest = runtime.RefreshWidgetWeatherAsync(widget, force: false);
        Assert.True(widget.IsLoading);
        runtime.SelectWidgetCity(widget, shanghai.Id);
        var newRequest = runtime.RefreshWidgetWeatherAsync(widget);
        first.SetResult(new(System.Net.HttpStatusCode.ServiceUnavailable));
        await oldRequest;
        Assert.True(widget.IsLoading);
        Assert.Empty(widget.Error);
        second.SetResult(new(System.Net.HttpStatusCode.OK) { Content = new StringContent(
            "{\"current\":{\"temperature\":{\"value\":\"24\"},\"weather\":\"0\"},\"forecastDaily\":{\"temperature\":{\"value\":[{\"from\":\"28\",\"to\":\"20\"}]}}}") });
        await newRequest;
        Assert.False(widget.IsLoading);
        Assert.Equal(24, shanghai.Weather!.Temperature);
        Assert.Equal(18, beijing.Weather!.Temperature);
        Assert.Equal(shanghai.Id, widget.ActiveCityId);
    }

    [Fact]
    public async Task LegacyLifeIndicesRefreshOnceBeforeTheNormalWeatherCacheApplies()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var requests = 0;
        using var client = new HttpClient(new DeferredWeatherHandler(() =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""
                {"current":{"temperature":{"value":24},"weather":0},"forecastDaily":{"temperature":{"value":[{"from":28,"to":20}]}},
                "indices":{"indices":[{"type":"uvIndex","value":"6"},{"type":"sports","value":"0"}]}}
                """) });
        }));
        using var service = new WidgetWeatherService(client);
        Set(runtime, "_widgetWeatherService", service);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather };
        DesktopWidgets.AddWeatherCity(widget, new("测试", "", "", 1, 2) { ProviderKey = "test" }).Weather =
            new(18, 12, 22, 0, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName, Indices = [new("运动", "适宜")] };
        runtime.State.Widgets.Add(widget);
        await runtime.RefreshWidgetWeatherAsync(widget, force: false);
        Assert.Equal("uvIndex", widget.Weather!.Indices[0].Type);
        var refreshed = widget.Weather;
        await runtime.RefreshWidgetWeatherAsync(widget, force: false);
        Assert.Equal(1, requests); Assert.Same(refreshed, widget.Weather);
    }

    private sealed class DeferredWeatherHandler(Func<Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response();
    }

    [Fact]
    public async Task EditingSourceCancelsTranslationAndDiscardsItsLateFailure()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DeferredWeatherHandler(() => response.Task));
        using var service = new WidgetTranslationService(client);
        Set(runtime, "_widgetTranslationService", service);
        runtime.State.Settings.WidgetTranslation.BaseUrl = "https://example.test/v1";
        runtime.State.Settings.WidgetTranslation.Model = "test-model";
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Text = "old input" };
        runtime.State.Widgets.Add(widget);
        var pending = runtime.TranslateWidgetAsync(widget);
        Assert.True(widget.IsLoading);
        runtime.UpdateWidgetTranslationInput(widget, "新原文\r第二行");
        Assert.False(widget.IsLoading);
        response.SetResult(new(System.Net.HttpStatusCode.ServiceUnavailable));
        await pending;
        Assert.Equal("新原文\n第二行", widget.Text);
        Assert.Empty(widget.Translation);
        Assert.Empty(widget.Error);
        widget.Translation = "previous result";
        runtime.SetWidgetTranslationLanguage(widget, "日语");
        Assert.Equal("日语", widget.TargetLanguage);
        Assert.Empty(widget.Translation);
    }

    [Fact]
    public void TranslationCardFieldsAndButtonsStayInsideTheCardAtSupportedDpi() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Size = DesktopWidgetSize.Large,
            MonitorId = monitor.Id, Bounds = new(20, 20, 372, 180) };
        runtime.State.Widgets.Add(widget);
        var layout = DesktopBoxForm.GetTranslationLayout(widget);
        var bounds = DesktopBoxForm.WidgetRectangle(widget);
        foreach (var rectangle in new[] { layout.Source, layout.Result, layout.Translate, layout.Clear, layout.Copy, layout.Language, layout.Status })
            Assert.True(bounds.Contains(rectangle));
        Assert.False(layout.Source.IntersectsWith(layout.Result));
        Assert.False(layout.Translate.IntersectsWith(layout.Copy));
        Assert.False(layout.Language.IntersectsWith(layout.Copy));
        Assert.False(layout.Clear.IntersectsWith(layout.Source));
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "HandleWidgetClick", widget, new PointF(layout.Result.X + 8, layout.Result.Y + 8));
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        using var pending = new CancellationTokenSource();
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource> { [widget.Id] = pending });
        widget.Text = "待清空原文"; widget.Translation = "Existing translation"; widget.Error = "Previous error"; widget.IsLoading = true;
        Call(surface, "HandleWidgetClick", widget, new PointF(layout.Clear.X + 8, layout.Clear.Y + 8));
        Assert.Empty(widget.Text);
        Assert.Empty(widget.Translation);
        Assert.Empty(widget.Error);
        Assert.False(widget.IsLoading);
        Assert.True(pending.IsCancellationRequested);
    });

    [Fact]
    public void WidgetDraggingHonorsLockAndKeepsTheCompactHeight() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Small,
            MonitorId = monitor.Id, IsLocked = true, Bounds = new(20, 20, 180, 84) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        Set(surface, "_pressedWidget", widget);
        Set(surface, "_widgetPressPoint", new PointF(40, 30));
        Set(surface, "_widgetStartBounds", widget.Bounds);
        Set(surface, "_widgetCanDrag", false);
        Call(surface, "HandleWidgetMouseMove", new PointF(80, 50));
        Assert.Equal(new LayoutRect(20, 20, 180, 84), widget.Bounds);
        widget.IsLocked = false;
        Set(surface, "_widgetCanDrag", true);
        Call(surface, "HandleWidgetMouseMove", new PointF(80, 50));
        Assert.Equal(new LayoutRect(60, 40, 180, 84), widget.Bounds);
        Call(surface, "FinishWidgetPress", false);
        Assert.Equal(84, widget.Bounds.Height);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void WidgetDragSnapsToNearbyCardsRendersGuidesAndKeepsAlignmentOnRelease(double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(80, 100, 180, 84) };
        var peer = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(100, 230, 180, 84) };
        runtime.State.Widgets.AddRange([widget, peer]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        Set(surface, "_pressedWidget", widget); Set(surface, "_widgetPressPoint", new PointF(100, 110));
        Set(surface, "_widgetStartBounds", widget.Bounds); Set(surface, "_widgetCanDrag", true);
        IReadOnlyList<DesktopBoxAlignmentGuide> Guides() => (IReadOnlyList<DesktopBoxAlignmentGuide>)
            typeof(DesktopBoxForm).GetField("_boxAlignmentGuides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Call(surface, "HandleWidgetMouseMove", new PointF(124, 110));
        Assert.Equal(new LayoutRect(100, 100, 180, 84), widget.Bounds);
        Assert.Contains(Guides(), guide => guide.IsVertical && guide.Position == 100);
        using (var bitmap = new Bitmap(400, 400))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            surface.RenderStaticOnIconLayer(graphics, new(0, 0, 400, 400));
            Assert.Contains(Enumerable.Range(192, 32), y => bitmap.GetPixel(100, y).A > 0);
        }
        if (scale == 1)
        {
            using var acrylic = new Bitmap(400, 400);
            Call(surface, "PaintAcrylicFrame", acrylic);
            Assert.Contains(Enumerable.Range(192, 32), y => acrylic.GetPixel(100, y).A > 0);
        }
        Call(surface, "HandleWidgetMouseMove", new PointF(133, 110));
        Assert.Equal(113, widget.Bounds.X); Assert.Empty(Guides());
        Call(surface, "HandleWidgetMouseMove", new PointF(124, 110));
        Call(surface, "HandleWidgetMouseUp", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1,
            (int)Math.Round(124 * scale), (int)Math.Round(110 * scale), 0), new PointF(124, 110));
        Assert.Equal(new LayoutRect(100, 100, 180, 84), widget.Bounds);
        Assert.Empty(Guides());
        using var settled = new Bitmap(400, 400);
        using (var graphics = Graphics.FromImage(settled)) surface.RenderStaticOnIconLayer(graphics, new(0, 0, 400, 400));
        Assert.All(Enumerable.Range(192, 32), y => Assert.Equal(0, settled.GetPixel(100, y).A));
    });

    [Fact]
    public void WidgetAlignmentUsesTheVisibleHeightOfCollapsedBoxes() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(100, 90, 180, 180) };
        var peer = new DesktopBox { MonitorId = monitor.Id, Bounds = new(100, 230, 244, 288), ExpandOnHover = true };
        runtime.State.Widgets.Add(widget); runtime.State.Boxes.Add(peer);
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "ApplyWidgetDragPosition", widget, widget.Bounds, monitor);
        Assert.Equal(230 + peer.Appearance.TitleBarHeight - 180, widget.Bounds.Y);
        Assert.Equal(180, widget.Bounds.Height);
    });

    [Fact]
    public void BoxDraggingCanAlignToANearbyWidget() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var box = new DesktopBox { MonitorId = monitor.Id, Bounds = new(104, 100, 244, 180) };
        runtime.State.Boxes.Add(box);
        runtime.State.Widgets.Add(new() { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(100, 324, 180, 84) });
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "ApplyBoxDragPosition", box, box.Bounds, monitor);
        Assert.Equal(100, box.Bounds.X);
        Assert.Equal(100, box.Bounds.Y);
    });

    [Fact]
    public void WidgetAlignmentUsesDistantCardsOnItsMonitorAndKeepsFreeMovementAwayFromAlignmentLines() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(104.2, 100.3, 180, 84) };
        runtime.State.Widgets.AddRange([widget,
            new() { MonitorId = "other-monitor", Bounds = new(100, 230, 180, 84) }]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var freeBounds = widget.Bounds;
        Call(surface, "ApplyWidgetDragPosition", widget, freeBounds, monitor);
        Assert.Equal(freeBounds, widget.Bounds);
        runtime.State.Widgets.Add(new() { MonitorId = monitor.Id, Bounds = new(100, 500, 180, 84) });
        Call(surface, "ApplyWidgetDragPosition", widget, freeBounds, monitor);
        Assert.Equal(freeBounds with { X = 100 }, widget.Bounds);
        var guides = (IReadOnlyList<DesktopBoxAlignmentGuide>)typeof(DesktopBoxForm)
            .GetField("_boxAlignmentGuides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.Equal(584, Assert.Single(guides).End);
        freeBounds = freeBounds with { X = 120.2 };
        Call(surface, "ApplyWidgetDragPosition", widget, freeBounds, monitor);
        Assert.Equal(freeBounds, widget.Bounds);
        Call(surface, "ApplyWidgetDragPosition", widget, new LayoutRect(-5, 690, 180, 84), monitor);
        Assert.Equal(new LayoutRect(0, 616, 180, 84), widget.Bounds);
    });

    [Theory]
    [InlineData("regular")]
    [InlineData("icon")]
    [InlineData("static")]
    [InlineData("acrylic")]
    public void ExpandedBoxKeepsItsContentsAboveCoveredWidgetsInEveryPainter(string painter) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        var box = new DesktopBox { MonitorId = monitor.Id, Title = "展开盒子", Bounds = new(100, 100, 260, 240), ExpandOnHover = true };
        box.Appearance.Opacity = 1;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id,
            Bounds = new(130, 170, 180, 84), Text = "组件文字应显示在盒子下方" };
        runtime.State.Boxes.Add(box); runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "ExpandHoveredBox", box.Id, false);
        Bitmap Render()
        {
            Call(surface, "EnsureGeometry");
            var bitmap = new Bitmap(500, 400);
            if (painter == "acrylic") Call(surface, "PaintAcrylicFrame", bitmap);
            else
            {
                using var graphics = Graphics.FromImage(bitmap);
                if (painter == "regular") Call(surface, "PaintRegularSurface", graphics);
                else if (painter == "icon") surface.RenderOnIconLayer(graphics, new(0, 0, 500, 400));
                else surface.RenderStaticOnIconLayer(graphics, new(0, 0, 500, 400));
            }
            return bitmap;
        }
        using var withWidget = Render();
        runtime.State.Widgets.Clear();
        using var boxOnly = Render();
        for (var y = 180; y < 230; y++)
            for (var x = 140; x < 290; x++)
                Assert.Equal(boxOnly.GetPixel(x, y), withWidget.GetPixel(x, y));
    });

    [Fact]
    public void ExpandedBoxReceivesPointerAndScrollBeforeCoveredWidgetAndCollapseRevealsIt() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        var box = new DesktopBox { MonitorId = monitor.Id, Bounds = new(100, 100, 260, 240), ExpandOnHover = true };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(130, 170, 180, 180), Text = "便签" };
        runtime.State.Boxes.Add(box); runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var point = new PointF(150, 200);
        Assert.Same(widget, Call(surface, "WidgetAt", point));
        Call(surface, "ExpandHoveredBox", box.Id, false);
        Assert.Null(Call(surface, "WidgetAt", point));
        Assert.False((bool)Call(surface, "HandleWidgetMouseMove", point)!);
        Assert.False((bool)Call(surface, "TryScrollWidget", point, -120)!);
        Assert.False((bool)Call(surface, "HandleWidgetMouseDown",
            new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 150, 200, 0), point)!);
        Call(surface, "CollapseHoverExpandedBox", box.Id, false);
        Assert.Same(widget, Call(surface, "WidgetAt", point));
    });

    [Fact]
    public void AcrylicForegroundIncludesWidgetsAndKeepsRoundedCornersTransparent() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.CornerRadius = 20;
        runtime.State.Widgets.Add(new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Small,
            MonitorId = monitor.Id, Bounds = new(20, 20, 180, 84) });
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        using var bitmap = DesktopLayerBitmapFactory.Create(1000, 700);
        Call(surface, "PaintAcrylicFrame", bitmap);
        Assert.InRange(bitmap.GetPixel(30, 78).A, 170, 200);
        Assert.Equal(0, bitmap.GetPixel(20, 20).A);
        Assert.Equal(0, bitmap.GetPixel(350, 350).A);
    });

    [Theory]
    [InlineData(1d, 180)]
    [InlineData(1.25d, 180)]
    [InlineData(1.5d, 180)]
    [InlineData(2d, 180)]
    [InlineData(1d, 372)]
    [InlineData(1.25d, 372)]
    [InlineData(1.5d, 372)]
    [InlineData(2d, 372)]
    public void NoteEditorAndDisplayKeepTextOriginsAndLineSpacingAligned(double scale, int cardWidth) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(20, 20, cardWidth, 180),
            Text = "便签 Text alignment\n第二行中文与 123\n第三行文字"
        };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000);
        _ = surface.Handle;
        Call(surface, "HandleWidgetClick", widget, new PointF(70, 90));
        var editor = (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField("_noteInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var frame = Assert.IsType<System.Windows.Controls.Border>(editor.InputBox.Parent);
        Assert.Equal(Math.Min(8, runtime.State.Settings.Appearance.CornerRadius), frame.CornerRadius.TopLeft);
        Assert.NotNull(editor.Region);
        Assert.False(editor.Region!.IsVisible(0, 0));
        var field = (RectangleF)typeof(DesktopBoxForm).GetMethod("NoteFieldRectangle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [widget])!;
        var body = (RectangleF)typeof(DesktopBoxForm).GetMethod("WidgetBodyRectangle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [widget])!;
        AssertWidgetEditorTextAligned(editor, field, scale, 3, "note", graphics =>
            Call(surface, "DrawTextWidget", graphics, widget, body, Color.White, Color.White));
    });

    [Theory]
    [InlineData(1d, 180)]
    [InlineData(1.25d, 180)]
    [InlineData(1.5d, 180)]
    [InlineData(2d, 180)]
    [InlineData(1d, 372)]
    [InlineData(1.25d, 372)]
    [InlineData(1.5d, 372)]
    [InlineData(2d, 372)]
    public void TodoEditorAndDisplayKeepTextOriginsAligned(double scale, int cardWidth) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var task = new WidgetTodoItem { Text = "待办 Text 123", EndDate = new(2026, 11, 3) };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id,
            Bounds = new(20, 20, cardWidth, 180), Tasks = [task] };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        Call(surface, "BeginWidgetTodoInput", widget, task);
        var editor = (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField("_todoInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var field = (RectangleF)Call(surface, "TodoTextRectangle", widget, 0, false)!;
        var body = (RectangleF)typeof(DesktopBoxForm).GetMethod("WidgetBodyRectangle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [widget])!;
        AssertWidgetEditorTextAligned(editor, field, scale, 1, "todo", graphics =>
            Call(surface, "DrawTodoWidget", graphics, widget, body, Color.White, Color.LightGray, Color.CornflowerBlue));
    });

    private static void AssertWidgetEditorTextAligned(DesktopWidgetTextInput editor, RectangleF field, double scale,
        int lines, string name, Action<Graphics> draw)
    {
        var frame = Assert.IsType<System.Windows.Controls.Border>(editor.InputBox.Parent);
        Assert.Equal(new System.Windows.Thickness(0), frame.BorderThickness);
        var root = System.Windows.PresentationSource.FromVisual(frame)!.RootVisual;
        System.Windows.Media.VisualTreeHelper.SetRootDpi(root, new System.Windows.DpiScale(scale, scale));
        frame.Width = Math.Round(field.Width * scale) / scale; frame.Height = Math.Round(field.Height * scale) / scale;
        frame.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; frame.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        frame.Measure(new System.Windows.Size(frame.Width, frame.Height));
        frame.Arrange(new System.Windows.Rect(0, 0, frame.Width, frame.Height)); frame.UpdateLayout();
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        editor.InputBox.ScrollToVerticalOffset(0); editor.InputBox.ScrollToHorizontalOffset(0);
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        editor.InputBox.CaretBrush = System.Windows.Media.Brushes.Transparent;
        var width = (int)Math.Round(field.Width * scale); var height = (int)Math.Round(field.Height * scale);
        var editing = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        editing.Render(frame);
        var pixels = new byte[width * height * 4];
        editing.CopyPixels(pixels, width * 4, 0);
        using var displayed = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(displayed))
        {
            var background = Assert.IsType<System.Windows.Media.SolidColorBrush>(frame.Background).Color;
            graphics.Clear(Color.FromArgb(background.R, background.G, background.B));
            graphics.ScaleTransform((float)scale, (float)scale);
            graphics.TranslateTransform((float)(-Math.Round(field.X * scale) / scale), (float)(-Math.Round(field.Y * scale) / scale));
            draw(graphics);
        }
        var lineHeight = editor.InputBox.GetRectFromCharacterIndex(0).Height;
        var firstLine = editor.InputBox.TranslatePoint(new(0, editor.InputBox.GetRectFromCharacterIndex(0).Top), frame).Y;
        var origins = new List<(Point Display, Point Edit)>();
        for (var line = 0; line < lines; line++)
        {
            var y = (int)((firstLine + line * lineHeight) * scale);
            var band = new Rectangle(0, y, width, Math.Min(height - y, (int)Math.Ceiling(lineHeight * scale)));
            var normalOrigin = FindNoteInkOrigin(band, (x, y) => displayed.GetPixel(x, y));
            var editingOrigin = FindNoteInkOrigin(band, (x, y) =>
            {
                var offset = (y * width + x) * 4;
                return Color.FromArgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            });
            origins.Add((normalOrigin, editingOrigin));
        }
        var suffix = $"{name}-{scale}-{field.Width}";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"widget-origin-{suffix}.txt"), string.Join('\n', origins));
        displayed.Save(Path.Combine(AppContext.BaseDirectory, $"widget-display-{suffix}.png"));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(editing));
        using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"widget-editor-{suffix}.png"))) encoder.Save(output);
        foreach (var origin in origins)
        {
            Assert.InRange(Math.Abs(origin.Display.X - origin.Edit.X), 0, 1);
            Assert.InRange(Math.Abs(origin.Display.Y - origin.Edit.Y), 0, 1);
        }
    }

    public static IEnumerable<object[]> TranslationAlignmentCases()
    {
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
        foreach (var result in new[] { false, true })
        foreach (var sample in new[] { ("digits", "123123123\n123123123\n123123123"),
            ("mixed", "中文 Text 123\n第二行中文与 456\n第三行文字"), ("wrapped", new string('A', 120)) })
            yield return [scale, result, sample.Item1, sample.Item2];
    }

    [Theory]
    [MemberData(nameof(TranslationAlignmentCases))]
    public void TranslationEditorAndDisplayKeepTextOriginsAligned(double scale, bool result, string sample, string value) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        runtime.State.Settings.DefaultBoxAppearance.Background = "#FF202020";
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Size = DesktopWidgetSize.Large,
            MonitorId = monitor.Id, Bounds = new(20, 20, 372, 180), Text = value, Translation = value };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var layout = DesktopBoxForm.GetTranslationLayout(widget);
        var field = result ? layout.Result : layout.Source;
        Call(surface, "HandleWidgetClick", widget, new PointF(field.X + 14, field.Y + 14));
        var editor = (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField(result ? "_translationResultInput" : "_translationInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var actualFrame = Assert.IsType<System.Windows.Controls.Border>(editor.InputBox.Parent);
        Assert.Equal(new System.Windows.Thickness(1), actualFrame.BorderThickness);
        Assert.Equal(50, Assert.IsType<System.Windows.Media.SolidColorBrush>(actualFrame.BorderBrush).Color.A);
        var frame = actualFrame;
        var view = editor.InputBox;
        var root = System.Windows.PresentationSource.FromVisual(frame)!.RootVisual;
        System.Windows.Media.VisualTreeHelper.SetRootDpi(root, new System.Windows.DpiScale(scale, scale));
        var logicalWidth = Math.Round(field.Width * scale) / scale;
        var logicalHeight = Math.Round(field.Height * scale) / scale;
        // Keep the field at its intended DIP size when the simulated root DPI
        // differs from the actual monitor hosting this offscreen HWND.
        frame.Width = logicalWidth; frame.Height = logicalHeight;
        frame.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        frame.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        frame.Measure(new System.Windows.Size(logicalWidth, logicalHeight));
        frame.Arrange(new System.Windows.Rect(0, 0, logicalWidth, logicalHeight)); frame.UpdateLayout();
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.ScrollToVerticalOffset(0);
        editor.InputBox.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.CaretBrush = System.Windows.Media.Brushes.Transparent;
        var width = (int)Math.Round(field.Width * scale); var height = (int)Math.Round(field.Height * scale);
        var editing = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        editing.Render(frame);
        var pixels = new byte[width * height * 4]; editing.CopyPixels(pixels, width * 4, 0);
        using var displayed = new Bitmap(width, height);
        using var visual = new DesktopWidgetTextVisual();
        using (var graphics = Graphics.FromImage(displayed))
        {
            graphics.Clear(Color.FromArgb(32, 32, 32)); graphics.ScaleTransform((float)scale, (float)scale);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.TextContrast = 4;
            typeof(DesktopBoxForm).GetMethod("DrawTranslationField", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [graphics, widget.Text, "", new RectangleF(0, 0, field.Width, field.Height), Color.White, Color.White, 0f, visual]);
        }
        var lineHeight = view.GetRectFromCharacterIndex(0).Height;
        var origins = new List<(Point Display, Point Edit)>();
        for (var line = 0; line < 3; line++)
        {
            var top = (int)((6 + line * lineHeight) * scale);
            var band = new Rectangle((int)(4 * scale), top, width - (int)(8 * scale), Math.Min(height - top, (int)(lineHeight * scale)));
            var normalOrigin = FindNoteInkOrigin(band, (x, y) => displayed.GetPixel(x, y));
            var editingOrigin = FindNoteInkOrigin(band, (x, y) =>
            {
                var offset = (y * width + x) * 4;
                return Color.FromArgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            });
            origins.Add((normalOrigin, editingOrigin));
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"translation-origin-{sample}-{scale}-{result}.txt"),
            $"visualHeight={visual.Height}; editorLines={view.LineCount}; lineHeight={lineHeight}; width={view.ActualWidth}; dpi={System.Windows.Media.VisualTreeHelper.GetDpi(view).PixelsPerDip}\n" + string.Join('\n', origins));
        Assert.InRange(Math.Abs(visual.Height - view.LineCount * lineHeight), 0, 1 / scale);
        displayed.Save(Path.Combine(AppContext.BaseDirectory, $"translation-display-{sample}-{scale}-{result}.png"));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(editing));
        using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"translation-editor-{sample}-{scale}-{result}.png"))) encoder.Save(output);
        foreach (var origin in origins)
        {
            Assert.InRange(Math.Abs(origin.Display.X - origin.Edit.X), 0, 1);
            Assert.InRange(Math.Abs(origin.Display.Y - origin.Edit.Y), 0, 1);
        }
    });

    private static Point FindNoteInkOrigin(Rectangle bounds, Func<int, int, Color> pixel)
    {
        var left = bounds.Right;
        var top = bounds.Bottom;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var color = pixel(x, y);
                if (color.R > 180 && color.G > 180 && color.B > 180)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                }
            }
        Assert.True(left < bounds.Right && top < bounds.Bottom, "Expected visible note text in the line band");
        return new Point(left, top);
    }

    [Theory]
    [InlineData(180, false)]
    [InlineData(180, true)]
    [InlineData(372, false)]
    [InlineData(372, true)]
    public void WeatherLayoutFitsSingleAndMultipleCitiesWithoutOverlappingContent(int width, bool multipleCities) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180) };
        var city = DesktopWidgets.AddWeatherCity(widget, new("长安区", "西安", "中国", 34, 108));
        city.Weather = new(16, 6, 20, 0, DateTimeOffset.Now) { FeelsLike = 19, Humidity = 45, WindSpeed = 7, AirQuality = 127 };
        if (multipleCities) DesktopWidgets.AddWeatherCity(widget, new("北京", "", "中国", 40, 116));
        widget.ActiveCityId = city.Id;
        runtime.State.Widgets.Add(widget);
        var card = DesktopBoxForm.WidgetRectangle(widget);
        var layout = DesktopBoxForm.GetWeatherLayout(widget);
        var header = DesktopBoxForm.WeatherCityButtonRectangle(widget);
        Assert.True(card.Contains(header));
        Assert.False(header.IntersectsWith(DesktopBoxForm.WeatherLocationButtonRectangle(widget)));
        Assert.False(header.IntersectsWith(DesktopBoxForm.WeatherLocationsButtonRectangle(widget)));
        var regions = new[] { layout.Temperature, layout.Icon, layout.Forecast, layout.FeelsLike, layout.Humidity, layout.Wind, layout.Status };
        foreach (var region in regions) Assert.True(card.Contains(region));
        for (var i = 0; i < regions.Length; i++)
            for (var j = i + 1; j < regions.Length; j++) Assert.False(regions[i].IntersectsWith(regions[j]));
        if (multipleCities) Assert.True(layout.Status.Bottom <= card.Bottom - 28);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var point = new PointF(header.X + 10, header.Y + 10);
        Call(surface, "HandleWidgetMouseDown", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0), point);
        Assert.False((bool)typeof(DesktopBoxForm).GetField("_widgetCanDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!);
        var temperature = new PointF(layout.Temperature.X + 10, layout.Temperature.Y + 10);
        Call(surface, "HandleWidgetMouseDown", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)temperature.X, (int)temperature.Y, 0), temperature);
        Assert.True((bool)typeof(DesktopBoxForm).GetField("_widgetCanDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!);
        using var bitmap = new Bitmap(width + 40, 220);
        using (var graphics = Graphics.FromImage(bitmap)) Call(surface, "DrawWidget", graphics, widget);
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"weather-layout-{width}-{multipleCities}.png"));
    });

    [Fact]
    public void TodoDatesCanBeOptionalEditedAndClearedWithoutChangingTaskTextOrCompletion()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var task = new WidgetTodoItem { Text = "任务", IsCompleted = true };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, Tasks = [task] }; runtime.State.Widgets.Add(widget);
        var start = new DateOnly(2026, 10, 29); var end = start.AddDays(4);
        Assert.True(runtime.SetWidgetTaskDates(widget, task.Id, start, end));
        runtime.SaveWidgetTask(widget, task.Id, "修改文字");
        Assert.Equal(start, task.StartDate); Assert.Equal(end, task.EndDate); Assert.True(task.IsCompleted);
        Assert.Throws<ArgumentException>(() => runtime.SetWidgetTaskDates(widget, task.Id, end, start));
        Assert.Equal(start, task.StartDate); Assert.Equal(end, task.EndDate);
        Assert.True(runtime.SetWidgetTaskDates(widget, task.Id, null, end));
        Assert.True(runtime.SetWidgetTaskDates(widget, task.Id, start, null));
        Assert.True(runtime.SetWidgetTaskDates(widget, task.Id, null, null));
        Assert.False(task.HasDateRange); Assert.Equal("修改文字", task.Text);
        runtime.State.Widgets.Remove(widget);
        Assert.False(runtime.SetWidgetTaskDates(widget, task.Id, start, end));
    }

    [Fact]
    public void TodoDateEditingRequestsTheSelectedTaskWithoutChangingDates()
    {
        var (runtime, _) = Fixture();
        var task = new WidgetTodoItem { Text = "测试待办", StartDate = new(2026, 10, 29) };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, Tasks = [task] };
        runtime.State.Widgets.Add(widget);
        var requests = new List<(DesktopWidget, WidgetTodoItem)>();
        runtime.WidgetTodoDatesRequested += (owner, selected) => requests.Add((owner, selected));
        runtime.EditWidgetTaskDates(widget, task.Id);
        Assert.Equal((widget, task), Assert.Single(requests));
        Assert.Equal(new DateOnly(2026, 10, 29), task.StartDate); Assert.Null(task.EndDate);
        runtime.EditWidgetTaskDates(widget, Guid.NewGuid());
        runtime.State.Widgets.Remove(widget);
        runtime.EditWidgetTaskDates(widget, task.Id);
        Assert.Single(requests);
    }

    [Fact]
    public void MixedTodoRowsHitTheCorrectTaskAfterScrolling() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, 372, 180),
            Tasks = Enumerable.Range(0, 8).Select(i => new WidgetTodoItem { Text = $"任务 {i}", EndDate = i % 2 == 0 ? new DateOnly(2026, 11, 3) : null }).ToList() };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor); _ = surface.Handle;
        var list = DesktopBoxForm.GetTodoLayout(widget).List;
        void ClickCheck(float y) => Call(surface, "HandleWidgetClick", widget, new PointF(list.X + 10, list.Y + y));
        ClickCheck(36); Assert.True(widget.Tasks[0].IsCompleted);
        ClickCheck(12); Assert.True(widget.Tasks[1].IsCompleted);
        Call(surface, "TryScrollWidget", new PointF(list.X + 20, list.Y + 10), -120);
        ClickCheck(12); Assert.True(widget.Tasks[3].IsCompleted); Assert.False(widget.Tasks[2].IsCompleted);
        ClickCheck(36); Assert.True(widget.Tasks[4].IsCompleted);
        Call(surface, "TryScrollWidget", new PointF(list.X + 20, list.Y + 10), -120);
        using var bitmap = new Bitmap(500, 250); using var graphics = Graphics.FromImage(bitmap); Call(surface, "DrawWidget", graphics, widget);
    });

    private static (CrabDeskRuntime, MonitorLayout) Fixture(double scale = 1)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var monitor = new MonitorLayout { Id = "widgets-test", DeviceName = "widgets-test", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700), PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale) };
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        return (runtime, monitor);
    }

    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
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
