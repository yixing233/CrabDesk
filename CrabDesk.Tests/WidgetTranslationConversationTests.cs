using System.Net;
using System.Text;
using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetTranslationConversationTests
{
    [Fact]
    public async Task ReusesCompletedPairsInOrderWithAnUnchangedSystemPrefix()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings();
        await service.TranslateAsync(settings, "你好", "英语");
        await service.TranslateAsync(settings, "谢谢", "英语");
        var first = handler.Requests[0]; var second = handler.Requests[1];
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, second.Select(message => message.Role));
        Assert.Equal(first[0], second[0]);
        Assert.Equal("你好", second[1].Content);
        Assert.Equal("translation-0", second[2].Content);
        Assert.Equal("谢谢", second[3].Content);
    }

    [Fact]
    public async Task IdleTimeoutStartsAtCompletionAndExpiresAtTheConfiguredBoundary()
    {
        var clock = new ManualClock();
        using var handler = new RecordingHandler();
        handler.Respond = (index, _) =>
        {
            if (index == 0) clock.Advance(TimeSpan.FromMinutes(40));
            return Task.FromResult(Reply("translated"));
        };
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client, clock);
        var settings = Settings();
        await service.TranslateAsync(settings, "first", "英语");
        clock.Advance(TimeSpan.FromMinutes(29));
        await service.TranslateAsync(settings, "second", "英语");
        clock.Advance(TimeSpan.FromMinutes(30));
        await service.TranslateAsync(settings, "third", "英语");
        Assert.Equal(new[] { 2, 4, 2 }, handler.Requests.Select(messages => messages.Length));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("key")]
    [InlineData("model")]
    [InlineData("prompt")]
    [InlineData("language")]
    [InlineData("timeout")]
    [InlineData("reuse")]
    public async Task ChangingTheProfileStartsANewConversation(string change)
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings(); var language = "英语";
        await service.TranslateAsync(settings, "first", language);
        switch (change)
        {
            case "endpoint": settings.BaseUrl = "https://another.test/v1"; break;
            case "key": settings.ApiKey = "other-test-key"; break;
            case "model": settings.Model = "another-model"; break;
            case "prompt": settings.PromptTemplate = "Translate into {targetLanguage}."; break;
            case "language": language = "日语"; break;
            case "timeout": settings.ConversationIdleMinutes = 10; break;
            case "reuse": settings.ReuseConversation = false; break;
        }
        await service.TranslateAsync(settings, "second", language);
        Assert.Equal(2, handler.Requests[1].Length);
        Assert.Equal("second", handler.Requests[1][1].Content);
    }

    [Fact]
    public async Task DisablingReuseKeepsEveryRequestIndependent()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings(); settings.ReuseConversation = false;
        await service.TranslateAsync(settings, "first", "英语");
        await service.TranslateAsync(settings, "second", "英语");
        Assert.All(handler.Requests, messages => Assert.Equal(2, messages.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledRequestsDoNotEnterTheNextContext(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RecordingHandler();
        handler.Respond = (index, _) =>
        {
            if (index == 1)
            {
                if (cancel) cancellation.Cancel();
                else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            return Task.FromResult(Reply("translation-" + index));
        };
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings();
        await service.TranslateAsync(settings, "successful", "英语");
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync(settings, "discarded", "英语", cancellation.Token));
        else await Assert.ThrowsAsync<HttpRequestException>(() => service.TranslateAsync(settings, "discarded", "英语"));
        await service.TranslateAsync(settings, "next", "英语");
        Assert.Equal(new[] { "successful", "translation-0", "next" }, handler.Requests[2].Skip(1).Select(message => message.Content));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateResponsesCannotAppendAfterANewerRequestOrExplicitReset(bool reset)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler();
        handler.Respond = async (index, _) =>
        {
            if (index == 1) { started.SetResult(); await release.Task; }
            return Reply("translation-" + index);
        };
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings();
        await service.TranslateAsync(settings, "seed", "英语");
        var old = service.TranslateAsync(settings, "outdated", "英语");
        await started.Task;
        if (reset) service.ResetConversation();
        await service.TranslateAsync(settings, "current", "英语");
        release.SetResult(); await old;
        await service.TranslateAsync(settings, "next", "英语");
        var contents = handler.Requests[3].Skip(1).Select(message => message.Content).ToArray();
        Assert.Equal(reset ? new[] { "current", "translation-2", "next" } :
            new[] { "seed", "translation-0", "current", "translation-2", "next" }, contents);
    }

    [Fact]
    public async Task StartsFreshAfterThirtyTwoCompletedTurns()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings();
        for (var index = 0; index < 33; index++) await service.TranslateAsync(settings, "text-" + index, "英语");
        Assert.Equal(64, handler.Requests[31].Length);
        Assert.Equal(2, handler.Requests[32].Length);
    }

    [Fact]
    public async Task LargeInputsResetHistoryBeforeTheRequestExceedsTheContextBudget()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings(); var text = new string('a', 12000);
        await service.TranslateAsync(settings, text, "英语");
        await service.TranslateAsync(settings, text, "英语");
        Assert.All(handler.Requests, messages => Assert.Equal(2, messages.Length));
        Assert.Equal(text, handler.Requests[1][1].Content);
    }

    [Fact]
    public async Task OversizedResultsAreReturnedIntactAndNotRetainedAsContext()
    {
        using var handler = new RecordingHandler { Respond = (_, _) => Task.FromResult(Reply(new string('x', 24001))) };
        using var client = new HttpClient(handler);
        using var service = new WidgetTranslationService(client);
        var settings = Settings();
        Assert.Equal(24001, (await service.TranslateAsync(settings, "first", "英语")).Length);
        await service.TranslateAsync(settings, "next", "英语");
        Assert.Equal(2, handler.Requests[1].Length);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(2000, 1440)]
    public async Task SessionPreferencesPersistWithBoundedTimeouts(int input, int expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrabDesk.ConversationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonLayoutStore(directory);
            await File.WriteAllTextAsync(store.StatePath, "{\"SchemaVersion\":24}");
            var state = await store.LoadAsync();
            Assert.True(state.Settings.WidgetTranslation.ReuseConversation);
            Assert.Equal(30, state.Settings.WidgetTranslation.ConversationIdleMinutes);
            state.Settings.WidgetTranslation.ReuseConversation = false;
            state.Settings.WidgetTranslation.ConversationIdleMinutes = input;
            await store.SaveAsync(state);
            var restored = await store.LoadAsync();
            Assert.False(restored.Settings.WidgetTranslation.ReuseConversation);
            Assert.Equal(expected, restored.Settings.WidgetTranslation.ConversationIdleMinutes);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static WidgetTranslationSettings Settings() => new() { BaseUrl = "https://translation.test/v1", ApiKey = "test-key", Model = "translator" };
    private sealed record Message(string Role, string Content);
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json")
    };
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<Message[]> Requests { get; } = [];
        internal Func<int, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (index, _) => Task.FromResult(Reply("translation-" + index));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var messages = document.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => new Message(message.GetProperty("role").GetString()!, message.GetProperty("content").GetString()!)).ToArray();
            var index = Requests.Count; Requests.Add(messages);
            return await Respond(index, cancellationToken);
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
