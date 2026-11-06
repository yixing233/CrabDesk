using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetLyricsSourceTests
{
    [Fact]
    public void LrcOrdersRepeatedTimestampsAndAppliesSignedGlobalOffset()
    {
        var document = WidgetLyricsParser.Parse("[00:04.50]Second\n[00:01.250][00:03.00]First\n[offset:-250]", "lrc")!;

        Assert.Equal(new[] { 1500d, 3250d, 4750d }, document.Lines.Select(line => line.StartMilliseconds));
        Assert.Equal(3250, document.Lines[0].EndMilliseconds);
        Assert.Equal(4750, document.Lines[1].EndMilliseconds);
        Assert.False(document.HasWordTiming);
    }

    [Fact]
    public void LrcUsesEmptyTimedLineAsEndAndIgnoresUntimedText()
    {
        var document = WidgetLyricsParser.Parse("[ar:Example]\nnot timed\n[00:01.00]Visible\n[00:02.00]\n", "lrc")!;

        var line = Assert.Single(document.Lines);
        Assert.Equal("Visible", line.Text);
        Assert.Equal(2000, line.EndMilliseconds);
        Assert.Null(WidgetLyricsParser.Parse("ordinary unsynchronized text", "lrc"));
        Assert.Null(WidgetLyricsParser.Parse("ordinary unsynchronized text"));
    }

    [Theory]
    [InlineData("[1000,1000]你(1000,300)好🌟(1300,700)", "qrc")]
    [InlineData("[1000,1000](1000,300,0)你(1300,700,0)好🌟", "yrc")]
    public void WordFormatsKeepActualTimestampsAndUnicode(string raw, string format)
    {
        var document = WidgetLyricsParser.Parse(raw, format)!;
        var line = Assert.Single(document.Lines);

        Assert.Equal("你好🌟", line.Text);
        Assert.True(document.HasWordTiming);
        Assert.Collection(line.Words!,
            word => Assert.Equal(new WidgetLyricWord("你", 1000, 1300), word),
            word => Assert.Equal(new WidgetLyricWord("好🌟", 1300, 2000), word));
    }

    [Fact]
    public void TtmlReadsWordTimesWithoutSplittingUnicodeText()
    {
        var document = WidgetLyricsParser.Parse("""
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
              <p begin="00:01.000" end="00:02.500"><span begin="00:01.000" end="00:01.400">你</span><span begin="00:01.400" end="00:02.500">好🌟</span></p>
            </div></body></tt>
            """, "ttml")!;

        var line = Assert.Single(document.Lines);
        Assert.True(document.HasWordTiming);
        Assert.Equal("你好🌟", line.Text);
        Assert.Equal(1400, line.Words![1].StartMilliseconds);
        Assert.Equal(2500, line.Words[1].EndMilliseconds);
    }

    [Fact]
    public void EnhancedLrcRequiresRealClosingTimestampForWordTiming()
    {
        var document = WidgetLyricsParser.Parse("[00:01.00]<00:01.00>你<00:01.40>好🌟<00:02.50>", "lrc")!;
        Assert.True(document.HasWordTiming);
        Assert.Equal(new WidgetLyricWord("好🌟", 1400, 2500), document.Lines[0].Words![1]);

        var noClosingTime = WidgetLyricsParser.Parse("[00:01.00]<00:01.00>你<00:01.40>好🌟", "lrc")!;
        Assert.False(noClosingTime.HasWordTiming);
        Assert.Equal("你好🌟", noClosingTime.Lines[0].Text);
    }

    [Fact]
    public void QrcXmlWrapperIsUnwrappedAndOffsetClipsAtZero()
    {
        var document = WidgetLyricsParser.Parse("""
            <?xml version="1.0"?><QrcInfos><Lyric_1 LyricContent="[offset:1500]&#10;[1000,1000]你(1000,300)好(1300,700)" /></QrcInfos>
            """)!;

        var line = Assert.Single(document.Lines);
        Assert.Equal(0, line.StartMilliseconds);
        Assert.All(line.Words!, word => Assert.True(word.StartMilliseconds >= 0));
        Assert.Equal(500, line.Words![1].EndMilliseconds);
    }

    [Theory]
    [InlineData("<tt><broken", "ttml")]
    [InlineData("[99:99]Invalid", "lrc")]
    [InlineData("[00:01]", "lrc")]
    [InlineData("", "qrc")]
    public void MalformedOrEmptyLyricsDoNotCreateTimedDocument(string raw, string format) =>
        Assert.Null(WidgetLyricsParser.Parse(raw, format));

    [Fact]
    public async Task PrefersWordTimingAndCachesNormalizedSuccess()
    {
        var first = new StubProvider((_, _) => Task.FromResult<WidgetLyricsDocument?>(LineDocument()));
        var second = new StubProvider((_, _) => Task.FromResult<WidgetLyricsDocument?>(WordDocument()));
        var third = new StubProvider((_, _) => throw new InvalidOperationException("Should not be reached"));
        using var service = new WidgetLyricsService([first, second, third]);

        var result = await service.FetchAsync(Media());
        var cached = await service.FetchAsync(Media() with { Title = " Example  " });

        Assert.True(result!.HasWordTiming);
        Assert.Same(result, cached);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(0, third.Calls);
    }

    [Fact]
    public async Task KeepsLineFallbackWhenLaterSourcesFail()
    {
        var first = new StubProvider((_, _) => Task.FromResult<WidgetLyricsDocument?>(LineDocument()));
        var failure = new StubProvider((_, _) => throw new HttpRequestException("Offline"));
        using var service = new WidgetLyricsService([first, failure]);

        var result = await service.FetchAsync(Media());

        Assert.NotNull(result);
        Assert.False(result.HasWordTiming);
        Assert.Equal("line source", result.Source);
    }

    [Fact]
    public async Task EmptyResultsAreRetriedAndSuccessfulCacheIsBounded()
    {
        var source = new StubProvider((media, _) => Task.FromResult<WidgetLyricsDocument?>(media.Title == "missing" ? null : LineDocument()));
        using var service = new WidgetLyricsService([source], cacheCapacity: 1);

        await service.FetchAsync(Media("missing"));
        await service.FetchAsync(Media("missing"));
        await service.FetchAsync(Media("first"));
        await service.FetchAsync(Media("second"));
        await service.FetchAsync(Media("first"));

        Assert.Equal(5, source.Calls);
    }

    [Fact]
    public async Task ProviderTimeoutCancelsItsWorkAndContinuesToFallback()
    {
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new StubProvider(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { providerCancelled.TrySetResult(); }
            return null;
        });
        var fallback = new StubProvider((_, _) => Task.FromResult<WidgetLyricsDocument?>(LineDocument()));
        using var service = new WidgetLyricsService([slow, fallback], TimeSpan.FromMilliseconds(40));

        var result = await service.FetchAsync(Media());

        await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task CallerCancellationStopsBeforeNextSourceAndDoesNotPopulateCache()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new StubProvider(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return WordDocument();
        });
        var fallback = new StubProvider((_, _) => Task.FromResult<WidgetLyricsDocument?>(LineDocument()));
        using var service = new WidgetLyricsService([slow, fallback]);
        using var cancellation = new CancellationTokenSource();
        var request = service.FetchAsync(Media(), cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task DifferentDurationsDoNotShareCacheAndMissingArtistDoesNotSearch()
    {
        var source = new StubProvider((_, _) => Task.FromResult<WidgetLyricsDocument?>(LineDocument()));
        using var service = new WidgetLyricsService([source]);
        await service.FetchAsync(Media());
        await service.FetchAsync(Media() with { Duration = TimeSpan.FromSeconds(210) });
        Assert.Null(await service.FetchAsync(Media() with { Artist = "" }));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public void MatchingRejectsWrongSongArtistAndRecordingDuration()
    {
        Assert.True(WidgetLyricsService.IsTrackMatch(Media(), " EXAMPLE ", ["Artist"], 181000));
        Assert.False(WidgetLyricsService.IsTrackMatch(Media(), "Example (Live)", ["Artist"], 180000));
        Assert.False(WidgetLyricsService.IsTrackMatch(Media(), "Example", ["Other Artist"], 180000));
        Assert.False(WidgetLyricsService.IsTrackMatch(Media(), "Example", ["Artist"], 210000));
        Assert.False(WidgetLyricsService.IsTrackMatch(Media(), "Other Song", ["Artist"], 180000));
    }

    private static ControlCenterMedia Media(string title = "Example") => new(title, "Artist", "Album", true, true, true, true)
    {
        Duration = TimeSpan.FromSeconds(180)
    };

    private static WidgetLyricsDocument LineDocument() => new([new("Example", 1000, 3000)], "line source");
    private static WidgetLyricsDocument WordDocument() => new(
        [new("Example", 1000, 3000, [new("Ex", 1000, 1400), new("ample", 1400, 3000)])], "word source");

    private sealed class StubProvider(Func<ControlCenterMedia, CancellationToken, Task<WidgetLyricsDocument?>> fetch) : IWidgetLyricsProvider
    {
        public int Calls { get; private set; }
        public Task<WidgetLyricsDocument?> FetchAsync(ControlCenterMedia media, CancellationToken token = default)
        {
            Calls++;
            return fetch(media, token);
        }
    }
}
