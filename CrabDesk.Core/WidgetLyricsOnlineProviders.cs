using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Providers.Web;
using Lyricify.Lyrics.Searchers;
using SearchSource = Lyricify.Lyrics.Searchers.Searchers;

namespace CrabDesk.Core;

internal static class WidgetLyricsOnlineProviders
{
    public static IWidgetLyricsProvider[] Create() =>
    [
        new OnlineProvider(SearchSource.QQMusic),
        new OnlineProvider(SearchSource.Netease),
        new OnlineProvider(SearchSource.LRCLIB)
    ];

    private sealed class OnlineProvider(SearchSource source) : IWidgetLyricsProvider
    {
        public async Task<WidgetLyricsDocument?> FetchAsync(ControlCenterMedia media, CancellationToken token = default)
        {
            using var scope = LyricifyTransport.Enter(token);
            var metadata = new TrackMultiArtistMetadata
            {
                Title = media.Title,
                Artist = media.Artist,
                Album = media.Album,
                DurationMs = media.Duration.TotalMilliseconds is > 0 and <= int.MaxValue
                    ? (int)media.Duration.TotalMilliseconds : null
            };
            ISearcher searcher = source switch
            {
                SearchSource.QQMusic => new QQMusicSearcher(),
                SearchSource.Netease => new NeteaseSearcher(),
                _ => new LRCLIBSearcher()
            };
            var results = await searcher.SearchForResults($"{metadata.Title} {metadata.Artist}").ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var matches = results?.Where(result => WidgetLyricsService.IsTrackMatch(media,
                    result.Title, result.Artists, result.DurationMs))
                .OrderByDescending(result => !string.IsNullOrWhiteSpace(media.Album) &&
                    WidgetLyricsService.NormalizeTrackText(result.Album) == WidgetLyricsService.NormalizeTrackText(media.Album))
                .ThenBy(result => Math.Abs((result.DurationMs ?? metadata.DurationMs ?? 0) - (metadata.DurationMs ?? 0)))
                .Take(3).ToArray();
            if (matches is null) return null;
            WidgetLyricsDocument? fallback = null;
            foreach (var match in matches)
            {
                token.ThrowIfCancellationRequested();
                WidgetLyricsDocument? document = null;
                switch (match)
                {
                    case QQMusicSearchResult qq:
                        var qqLyrics = await ProviderHelper.QQMusicApi.GetLyricsAsync(qq.Id).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(qqLyrics?.Lyrics))
                            document = WidgetLyricsParser.Parse(qqLyrics.Lyrics, source: "QQ 音乐");
                        if (document is null)
                        {
                            var lrc = await ProviderHelper.QQMusicApi.GetLyric(qq.Mid).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(lrc?.Lyric))
                                document = WidgetLyricsParser.Parse(lrc.Lyric, "lrc", "QQ 音乐");
                        }
                        break;
                    case NeteaseSearchResult netease:
                        var lyrics = await ProviderHelper.NeteaseApi.GetLyricNew(netease.Id).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (lyrics?.Nolyric == true) return new([], "网易云音乐", true);
                        if (!string.IsNullOrWhiteSpace(lyrics?.Yrc?.Lyric))
                            document = WidgetLyricsParser.Parse(lyrics.Yrc.Lyric, "yrc", "网易云音乐");
                        if (document is null && !string.IsNullOrWhiteSpace(lyrics?.Lrc?.Lyric))
                            document = WidgetLyricsParser.Parse(lyrics.Lrc.Lyric, "lrc", "网易云音乐");
                        break;
                    case LRCLIBSearchResult lrclib:
                        var lrcResult = await ProviderHelper.LRCLIBApi.GetById(lrclib.Id).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (lrcResult is null || !WidgetLyricsService.IsTrackMatch(media, lrcResult.TrackName,
                                [lrcResult.ArtistName], lrcResult.Duration * 1000)) break;
                        if (lrcResult.Instrumental) return new([], "LRCLIB", true);
                        if (!string.IsNullOrWhiteSpace(lrcResult.SyncedLyrics))
                            document = WidgetLyricsParser.Parse(lrcResult.SyncedLyrics, "lrc", "LRCLIB");
                        break;
                }
                if (document?.HasWordTiming == true) return document;
                fallback ??= document;
            }
            return fallback;
        }
    }

    // Lyricify 0.2.1 has a process-wide HttpClient and no CancellationToken API.
    // A scope follows each async request, so cancelling one widget never cancels another.
    private static class LyricifyTransport
    {
        private static readonly AsyncLocal<CancellationToken> CurrentToken = new();

        static LyricifyTransport()
        {
            BaseApi.HttpClient = new HttpClient(new CancellationHandler())
            {
                Timeout = TimeSpan.FromSeconds(8),
                MaxResponseContentBufferSize = 2_000_000
            };
            BaseApi.HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CrabDesk/1.0");
        }

        public static IDisposable Enter(CancellationToken token)
        {
            var previous = CurrentToken.Value;
            CurrentToken.Value = token;
            return new Scope(previous);
        }

        private sealed class Scope(CancellationToken previous) : IDisposable
        {
            public void Dispose() => CurrentToken.Value = previous;
        }

        private sealed class CancellationHandler() : DelegatingHandler(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CurrentToken.Value);
                cancellation.Token.ThrowIfCancellationRequested();
                var response = await base.SendAsync(request, cancellation.Token).ConfigureAwait(false);
                try
                {
                    // Buffer inside the scope so response bodies also honor track changes.
                    await response.Content.ReadAsByteArrayAsync(cancellation.Token).ConfigureAwait(false);
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
        }
    }
}
