using System.Threading.Channels;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopLyricsControllerTests
{
    [Fact]
    public async Task PlayerIconFollowsTheLatestMediaPollWhenTheSongStaysTheSame()
    {
        var provider = new LyricsProvider();
        ControlCenterMedia? media = Media("one") with { SourceAppId = "QQMusic.exe" };
        using var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(media), provider);
        await controller.RefreshAsync();
        var lyrics = Document("one");
        await (await provider.NextAsync()).CompleteAsync(lyrics);
        await WaitForAsync(controller, snapshot => ReferenceEquals(snapshot.Document, lyrics));
        Assert.Equal("qqmusic", MediaPlayerIcons.Resolve(controller.Snapshot.Media!.SourceAppId));

        media = media with { SourceAppId = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", IsPlaying = false };
        await controller.RefreshAsync();
        Assert.Equal("spotify", MediaPlayerIcons.Resolve(controller.Snapshot.Media!.SourceAppId));
        Assert.Same(lyrics, controller.Snapshot.Document);
        Assert.Equal(1, provider.CallCount);

        media = null;
        await controller.RefreshAsync();
        Assert.Null(MediaPlayerIcons.Resolve(controller.Snapshot.Media?.SourceAppId));
    }

    [Fact]
    public async Task PendingLyricsDoNotBlockPlaybackPollingAndCompletionKeepsLatestPauseAndSeek()
    {
        var provider = new LyricsProvider();
        var media = Media("one");
        using var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(media), provider);

        await controller.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var lookup = await provider.NextAsync();
        Assert.Same(media, controller.Snapshot.Media);
        Assert.True(controller.Snapshot.IsLoading);

        media = media with { IsPlaying = false, Position = TimeSpan.FromSeconds(63), CapturedAt = DateTimeOffset.UtcNow };
        await controller.RefreshAsync();
        Assert.Same(media, controller.Snapshot.Media);
        Assert.True(controller.Snapshot.IsLoading);
        Assert.Equal(1, provider.CallCount);

        var lyrics = Document("one");
        await lookup.CompleteAsync(lyrics);
        await WaitForAsync(controller, snapshot => ReferenceEquals(snapshot.Document, lyrics));
        Assert.Same(media, controller.Snapshot.Media);
        Assert.Equal(TimeSpan.FromSeconds(63), controller.Snapshot.Media!.Position);
        Assert.False(controller.Snapshot.Media.IsPlaying);
        Assert.False(controller.Snapshot.IsLoading);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("album")]
    public async Task TrackIdentityOrMetadataChangeImmediatelyClearsPreviousLyrics(string change)
    {
        var provider = new LyricsProvider();
        var media = Media("one");
        using var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(media), provider);
        await controller.RefreshAsync();
        var first = await provider.NextAsync();
        await first.CompleteAsync(Document("old"));
        await WaitForAsync(controller, snapshot => snapshot.Document is not null);

        media = change switch
        {
            "id" => media with { TrackId = "other-player" },
            "title" => media with { Title = "new title" },
            "artist" => media with { Artist = "new artist" },
            _ => media with { Album = "new album" }
        };
        await controller.RefreshAsync();
        var second = await provider.NextAsync();

        Assert.Same(media, second.Media);
        Assert.Null(controller.Snapshot.Document);
        Assert.True(controller.Snapshot.IsLoading);
        await second.CompleteAsync(null);
        await WaitForAsync(controller, snapshot => !snapshot.IsLoading);
    }

    [Fact]
    public async Task SupersededLookupIsCancelledAndCannotOverwriteANewerSongsLyrics()
    {
        var provider = new LyricsProvider();
        var media = Media("one");
        using var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(media), provider);
        await controller.RefreshAsync();
        var first = await provider.NextAsync();

        media = Media("two");
        await controller.RefreshAsync();
        var second = await provider.NextAsync();
        Assert.True(first.Token.IsCancellationRequested);
        var currentLyrics = Document("two");
        await second.CompleteAsync(currentLyrics);
        await WaitForAsync(controller, snapshot => ReferenceEquals(snapshot.Document, currentLyrics));

        // This provider deliberately ignores cancellation and finishes out of order.
        await first.CompleteAsync(Document("one"));
        await controller.RefreshAsync();
        Assert.Same(currentLyrics, controller.Snapshot.Document);
        Assert.Same(media, controller.Snapshot.Media);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task MissingLyricsAreNotFetchedOnEveryPollButExplicitRetryStartsANewLookup()
    {
        var provider = new LyricsProvider();
        var media = Media("one") with { TrackId = "" };
        using var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(media), provider);
        await controller.RefreshAsync();
        await (await provider.NextAsync()).CompleteAsync(null);
        await WaitForAsync(controller, snapshot => !snapshot.IsLoading);
        for (var index = 0; index < 3; index++)
        {
            media = media with { Position = TimeSpan.FromSeconds(index + 1) };
            await controller.RefreshAsync();
        }
        Assert.Equal(1, provider.CallCount);

        await controller.RefreshAsync(forceLyrics: true);
        var retry = await provider.NextAsync();
        Assert.True(controller.Snapshot.IsLoading);
        await retry.CompleteAsync(Document("found"));
        await WaitForAsync(controller, snapshot => snapshot.Document is not null);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task FailedLookupReportsErrorAndExplicitRetryCanRecover()
    {
        var provider = new LyricsProvider();
        using var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(Media("one")), provider);
        await controller.RefreshAsync();
        await (await provider.NextAsync()).FailAsync();
        await WaitForAsync(controller, snapshot => !snapshot.IsLoading);
        Assert.Contains("歌词获取失败", controller.Snapshot.Error);
        await controller.RefreshAsync();
        Assert.Equal(1, provider.CallCount);

        await controller.RefreshAsync(true);
        var retry = await provider.NextAsync();
        Assert.Empty(controller.Snapshot.Error);
        await retry.CompleteAsync(Document("recovered"));
        await WaitForAsync(controller, snapshot => snapshot.Document is not null);
        Assert.Empty(controller.Snapshot.Error);
    }

    [Fact]
    public async Task LosingMediaCancelsPendingLyricsAndReturningMediaStartsAgain()
    {
        var provider = new LyricsProvider();
        ControlCenterMedia? media = Media("one");
        using var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(media), provider);
        await controller.RefreshAsync();
        var first = await provider.NextAsync();
        media = null;
        await controller.RefreshAsync();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.Equal(WidgetLyricsSnapshot.Empty, controller.Snapshot);
        await first.CompleteAsync(Document("stale"));
        media = Media("one");
        await controller.RefreshAsync();
        await (await provider.NextAsync()).CompleteAsync(null);
        await WaitForAsync(controller, snapshot => !snapshot.IsLoading);
        Assert.Equal(2, provider.CallCount);
        Assert.Null(controller.Snapshot.Document);
    }

    [Fact]
    public async Task OverlappingPollsDoNotOverlapMediaReadsAndPreserveExplicitRetry()
    {
        var provider = new LyricsProvider();
        var media = Media("one");
        var pendingRead = new TaskCompletionSource<ControlCenterMedia?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var controller = new DesktopLyricsController(_ =>
            Interlocked.Increment(ref reads) == 1 ? Task.FromResult<ControlCenterMedia?>(media) : pendingRead.Task, provider);
        await controller.RefreshAsync();
        await (await provider.NextAsync()).CompleteAsync(null);
        await WaitForAsync(controller, snapshot => !snapshot.IsLoading);

        var active = controller.RefreshAsync();
        await controller.RefreshAsync();
        await controller.RefreshAsync(true);
        Assert.Equal(2, reads);
        Assert.Equal(1, provider.CallCount);
        pendingRead.SetResult(media);
        await active;
        await (await provider.NextAsync()).CompleteAsync(null);
        await WaitForAsync(controller, snapshot => !snapshot.IsLoading);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task DisposeCancelsMediaReadAndSuppressesLateStateAndNotifications()
    {
        var provider = new LyricsProvider();
        var pendingRead = new TaskCompletionSource<ControlCenterMedia?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CancellationToken.None;
        var reads = 0;
        var controller = new DesktopLyricsController(value =>
        {
            token = value;
            reads++;
            return pendingRead.Task;
        }, provider);
        var changes = 0;
        controller.Changed += () => changes++;
        var read = controller.RefreshAsync();

        controller.Dispose();
        Assert.True(token.IsCancellationRequested);
        pendingRead.SetResult(Media("late"));
        await read;
        await controller.RefreshAsync(true);
        controller.Dispose();

        Assert.Equal(WidgetLyricsSnapshot.Empty, controller.Snapshot);
        Assert.Equal(0, changes);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task DisposeCancelsLyricsAndDoesNotPublishTheirLateCompletion()
    {
        var provider = new LyricsProvider();
        var controller = new DesktopLyricsController(_ => Task.FromResult<ControlCenterMedia?>(Media("one")), provider);
        var changes = 0;
        controller.Changed += () => changes++;
        await controller.RefreshAsync();
        var lookup = await provider.NextAsync();
        var snapshot = controller.Snapshot;
        var before = changes;

        controller.Dispose();
        Assert.True(lookup.Token.IsCancellationRequested);
        await lookup.CompleteAsync(Document("late"));
        await controller.RefreshAsync(true);

        Assert.Same(snapshot, controller.Snapshot);
        Assert.Equal(before, changes);
    }

    [Fact]
    public async Task ResetCancelsPendingReadAndCanResumeAfterItFinishes()
    {
        var provider = new LyricsProvider();
        var pendingRead = new TaskCompletionSource<ControlCenterMedia?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var token = CancellationToken.None;
        using var controller = new DesktopLyricsController(value =>
        {
            token = value;
            return ++reads == 1 ? pendingRead.Task : Task.FromResult<ControlCenterMedia?>(Media("new"));
        }, provider);
        var first = controller.RefreshAsync();
        controller.Reset();
        Assert.True(token.IsCancellationRequested);
        await controller.RefreshAsync();
        Assert.Equal(1, reads);
        pendingRead.SetResult(Media("old"));
        await first;
        Assert.Equal(WidgetLyricsSnapshot.Empty, controller.Snapshot);

        await controller.RefreshAsync();
        var lookup = await provider.NextAsync();
        Assert.Equal("new", lookup.Media.Title);
        await lookup.CompleteAsync(null);
        await WaitForAsync(controller, snapshot => !snapshot.IsLoading);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task MediaReadFailureClearsLyricsAndSubsequentPollCanRecover()
    {
        var provider = new LyricsProvider();
        var fail = false;
        using var controller = new DesktopLyricsController(_ => fail
            ? Task.FromException<ControlCenterMedia?>(new InvalidOperationException())
            : Task.FromResult<ControlCenterMedia?>(Media("one")), provider);
        await controller.RefreshAsync();
        var lookup = await provider.NextAsync();
        fail = true;
        await controller.RefreshAsync();
        Assert.Null(controller.Snapshot.Media);
        Assert.Contains("无法读取播放信息", controller.Snapshot.Error);
        Assert.True(lookup.Token.IsCancellationRequested);
        await lookup.CompleteAsync(null);

        fail = false;
        await controller.RefreshAsync();
        await (await provider.NextAsync()).CompleteAsync(Document("recovered"));
        await WaitForAsync(controller, snapshot => snapshot.Document is not null);
        Assert.Empty(controller.Snapshot.Error);
    }

    private static ControlCenterMedia Media(string title) => new(title, "artist", "album", true, true, true, true)
    {
        TrackId = title,
        Duration = TimeSpan.FromMinutes(3),
        Position = TimeSpan.FromSeconds(10),
        CapturedAt = DateTimeOffset.UtcNow
    };

    private static WidgetLyricsDocument Document(string text) => new([new(text, 0, 1000)], "test");

    private static async Task WaitForAsync(DesktopLyricsController controller, Func<WidgetLyricsSnapshot, bool> predicate)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (predicate(controller.Snapshot)) completed.TrySetResult(); }
        controller.Changed += Check;
        try
        {
            Check();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { controller.Changed -= Check; }
    }

    private sealed class LyricsProvider : IWidgetLyricsProvider
    {
        private readonly Channel<Lookup> _calls = Channel.CreateUnbounded<Lookup>();
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<WidgetLyricsDocument?> FetchAsync(ControlCenterMedia media, CancellationToken token = default)
        {
            var lookup = new Lookup(media, token);
            Interlocked.Increment(ref _callCount);
            Assert.True(_calls.Writer.TryWrite(lookup));
            return lookup.Completion.Task;
        }

        public Task<Lookup> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed record Lookup(ControlCenterMedia Media, CancellationToken Token)
    {
        // Run completion on a pool thread so the controller continuation can finish
        // inline, including when it intentionally publishes no notification.
        public TaskCompletionSource<WidgetLyricsDocument?> Completion { get; } = new();
        public Task CompleteAsync(WidgetLyricsDocument? document) => Task.Run(() => Completion.SetResult(document));
        public Task FailAsync() => Task.Run(() => Completion.SetException(new InvalidOperationException("test failure")));
    }
}
