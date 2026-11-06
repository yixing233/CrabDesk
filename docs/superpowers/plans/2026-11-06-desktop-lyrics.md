# Desktop Lyrics Implementation Plan

**Goal:** Add an adjustable desktop lyrics widget with smooth line transitions, depth/fade treatment and timing-based karaoke highlighting.

**Architecture:** Reuse Windows media-session metadata and the existing desktop widget surface. Use Lyricify.Lyrics.Helper 0.2.1 for online lyrics and parsing, behind a testable provider. Keep fetching, playback state and cached visual rendering separate. Share a single playback/lyrics controller across widgets; never query networking or media from a paint callback.

**Tech Stack:** .NET 8, Lyricify Lyrics Helper, Windows SMTC, existing WinForms/GDI+ surface with cached WPF text rendering, xUnit/Moq.

## Contracts

```csharp
public sealed record WidgetLyricWord(string Text, double StartMilliseconds, double EndMilliseconds);
public sealed record WidgetLyricLine(string Text, double StartMilliseconds, double EndMilliseconds,
    IReadOnlyList<WidgetLyricWord>? Words = null);
public sealed record WidgetLyricsDocument(IReadOnlyList<WidgetLyricLine> Lines, string Source, bool IsInstrumental = false);
public sealed record WidgetLyricsSnapshot(ControlCenterMedia? Media, WidgetLyricsDocument? Document,
    bool IsLoading = false, string Error = "");
public interface IWidgetLyricsProvider
{
    Task<WidgetLyricsDocument?> FetchAsync(ControlCenterMedia media, CancellationToken token = default);
}
```

## Tasks

- [x] **Sources and parsing** — Add `WidgetLyrics.cs`, `WidgetLyricsService.cs` and `WidgetLyricsParser.cs` to Core; pin the library in `CrabDesk.Core.csproj`. Match title/artist/duration, prefer word timing where available, and fall back to LRCLIB. Support local LRC/QRC/YRC/TTML through the same normalized model. Bound requests and cache successful tracks. Test parser boundaries, offsets, Unicode, provider fallback and cancellation using fixtures rather than live requests.
- [x] **Playback controller** — Add `DesktopLyricsController.cs` in Runtime and a media-only entry point in Native. Poll metadata independently of the control center, cancel stale lyric requests when the track changes, retain timing updates during fetching, and stop work when disposed. Tests cover late results, pause, seek and retries with injected sources.
- [x] **Renderer** — Add `DesktopLyricsVisual.cs` in Runtime. Cache text geometry/rasters, use playback time for real word coverage, ease vertical motion and scale at line changes, soften distant lines and fade top/bottom edges. Render 1–9 visible lines; respect disabled animations. Generate light/dark and transition snapshots using synthetic lyrics for visual review.
- [x] **Widget integration** — Append `Lyrics` to the persisted kind enum; add default size, free resize, gallery preview and menus. Persist line count, font size, ink, word highlighting and timing offset. Provide automatic reload and local lyric import. Integrate controller lifetime and visible-only animation invalidation.
- [x] **Verification and delivery** — Run focused Core/Runtime/UI tests and build the full app. Inspect static and intermediate animation renders and use a read-only media/provider probe. Exit the running app through `--exit`, start the updated build, and confirm responsiveness.

## Source references

- Lyricify Lyrics Helper: https://github.com/WXRIW/Lyricify-Lyrics-Helper — .NET Standard 2.1, package 0.2.1, Apache-2.0. Provider and parser usage verified from its README and `docs/providers.md` / `docs/lyrics.md`.
- LRCLIB: https://github.com/tranxuanthang/lrclib — public synchronized-lyrics fallback exposed by the helper.

## Presentation

Default to five centered lines. The current line uses the largest type and a clear highlight; neighbors become smaller, dimmer and softer with distance. Preserve horizontal space for lyrics, show only a compact song heading/source hint, and place settings in the widget menu. Timed words use their actual timestamps; line-only lyrics use line highlighting. Pausing freezes coverage and seeking immediately selects the appropriate line.

## Verified result

- Full WinUI build succeeded with existing warnings. Core regression selection: 121 passed. Runtime/UI regression selection: 180 passed; the final lyrics-only run after the fade correction and integration additions passed 29 tests.
- Reviewed light/dark 1/5/9-line renders, whole-widget renders, and intermediate frames including the 349/350 ms transition boundary. Desktop updates run at a 33 ms interval while active; paused and hidden visuals stop requesting animation frames.
- QQ Music returned real word timing for two reference songs and for the current media session (45 lines). In the running application, UI Automation confirmed `歌词来源：QQ 音乐 · 逐字`.
- The optional Chinese-conversion asset is excluded at both Core and executable dependency resolution. Isolated parser/source probes and the application output were checked without `ChineseConverter.dll`; the three distributed component licenses are copied alongside the app.
- Started the updated application with `--show-widgets`; process 28308 responded normally. Used the real gallery entry to add one default five-line lyrics widget and verified the enabled line/font/ink/word/offset controls.
