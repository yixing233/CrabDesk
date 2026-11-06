# Lyrics Horizontal Scroll Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep each lyric depth at a consistent font size and show overflowing current lyrics through playback-driven horizontal scrolling.

**Architecture:** Retain the shared WPF text-mask renderer used by the desktop and widget preview. Rasterize each line at the configured font size; depth alone determines its rendered scale. Translate overflowing current lines using word coverage, or elapsed line time for line-only lyrics. Derive offsets from playback position so pauses, gaps and seeks preserve the correct view. Completed surrounding lines show their tail and upcoming lines show their beginning without scrolling.

**Tech Stack:** .NET 8, WPF text rasterization, System.Drawing, xUnit.

---

### Task 1: Reproduce length-dependent sizing and missing horizontal scrolling

**Files:** Modify `CrabDesk.WinUI.Tests/DesktopLyricsVisualTests.cs`.

- [x] Compare visible glyph heights in the same depth bands for three repeated Chinese glyphs and thirty-two repeated glyphs, using one and five display rows at 100% and 150% DPI. Scan each band's alpha pixels above 20; the height difference must be at most one pixel. The previous fit-to-width implementation should fail.
- [x] Render an overflowing line at early, middle and late playback positions with word highlighting disabled, so pixel differences demonstrate scrolling rather than coverage changes:

```csharp
using var early = Render(visual, snapshot, Epoch, rows: 1, width: 240, wordHighlight: false);
using var middle = Render(visual, snapshot, Epoch.AddSeconds(4), rows: 1, width: 240, wordHighlight: false);
using var late = Render(visual, snapshot, Epoch.AddSeconds(7), rows: 1, width: 240, wordHighlight: false);
Assert.False(Pixels(early).SequenceEqual(Pixels(middle)));
Assert.False(Pixels(middle).SequenceEqual(Pixels(late)));
```

- [x] Extend the existing `Render` helper with the optional `bool wordHighlight = true` argument and pass it to `visual.Draw`.
- [x] Verify paused long lines remain pixel-identical, a fresh seek renders the same viewport, surrounding long lines remain static, and completed long lines retain their viewport across a line gap.
- [x] Exit the running instance with its `--exit` channel, wait for clean termination, then run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release -p:Platform=x64 --filter 'FullyQualifiedName~LengthIndependent|FullyQualifiedName~LongLyrics' --verbosity minimal
```

Expected: sizing and scrolling regression tests fail on the current implementation.

### Task 2: Render fixed-size text and translate the current line

**Files:** Modify `CrabDesk.Runtime/DesktopLyricsVisual.cs`.

- [x] Remove font-size reduction and width trimming from `CreateLine`. Preserve a single line and its full natural width:

```csharp
var formatted = new Media.FormattedText(text, CultureInfo.CurrentUICulture,
    Wpf.FlowDirection.LeftToRight, typeface, fontSize, Media.Brushes.White,
    null, Media.TextFormattingMode.Ideal, scale);
formatted.MaxLineCount = 1;
var logicalWidth = formatted.WidthIncludingTrailingWhitespace + padding * 2;
```

- [x] Remove the unused available-width parameter from `GetLine` and `CreateLine` and update their two callers. Keep the existing bounded line cache and depth transforms.
- [x] Add the playback-derived horizontal offset helper:

```csharp
private static float HorizontalOffset(LineRaster raster, WidgetLyricLine line,
    double position, float viewportWidth)
{
    var overflow = Math.Max(0, raster.Width - viewportWidth);
    if (overflow == 0) return 0;
    if (raster.Words.Length == 0)
    {
        var duration = line.EndMilliseconds - line.StartMilliseconds;
        var progress = duration <= 0 ? 1 : Math.Clamp((position - line.StartMilliseconds) / duration, 0, 1);
        return overflow * (float)progress;
    }
    var focus = 0f;
    foreach (var word in raster.Words)
    {
        var coverage = WordProgress(word.Word, position);
        if (coverage <= 0) continue;
        focus = word.RightToLeft ? word.Bounds.Right - word.Bounds.Width * (float)coverage
            : word.Bounds.X + word.Bounds.Width * (float)coverage;
    }
    return Math.Clamp(focus - viewportWidth * .6f, 0, overflow);
}
```

- [x] For a short row, preserve centered placement. For overflow, set destination X to the negative horizontal offset, after applying the depth scale. Compute the current and departing line's offset with the helper; use a fixed tail for completed surrounding lines and a fixed beginning for upcoming lines. Use the same destination for base masks and word clips so timing and coverage stay aligned.
- [x] Add a smooth horizontal edge fade to the existing premultiplied-alpha fade pass. Apply the same factor to RGB and alpha to preserve transparent compositing.

### Task 3: Verify and apply the updated build

**Files:** Update this plan's checkboxes and verification results.

- [x] Run all existing and new lyrics tests, including desktop rendering and gallery integration:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release -p:Platform=x64 --filter 'FullyQualifiedName~DesktopLyrics' --verbosity minimal
```

- [x] Export and inspect early, middle and late long-line frames in light and dark colors. Check stable sizes, visible singing position, smooth side clipping, correct word coverage and unchanged vertical transitions.
- [x] Start the verified Release executable with `Start-Process -WindowStyle Hidden`, check that it stays running and responds, and report the result. Do not commit or push.

**Verification:** The initial regression run reproduced seven sizing/scrolling failures. The final Release build succeeded with existing warnings, and all 44 lyrics tests passed. Inspected the exported early/middle/late dark frames and middle light frame. The updated application started successfully and reported `Responding = True`.
