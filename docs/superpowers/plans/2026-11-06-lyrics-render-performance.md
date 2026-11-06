# Lyrics Rendering Performance Implementation Plan

**Goal:** Reduce playback CPU and per-frame allocations while preserving word coverage, horizontal scrolling, acrylic overlap, and desktop input.

**Architecture:** Keep a resident foreground bitmap for each acrylic monitor. On lyrics ticks, repaint and upload only the lyric widget rectangles when they do not overlap another object or participate in an active transform. Keep compositor front/back surfaces coherent by uploading the union of this frame's dirty region and the preceding frame's dirty region. Reuse the D2D upload bitmap and combine word-coverage clips into one accent draw.

**Tech Stack:** .NET 8, GDI+, Windows.UI.Composition, Direct2D, xUnit.

## Tasks

- [x] Record a deterministic many-word rendering baseline and add pixel-equivalence tests for partial lyric frames at 100% and 125% DPI, overlap/transform fallback, and buffer reuse.
- [x] Add the lyrics-only presentation route in `DesktopBoxForm.Lyrics.cs` / `DesktopBoxForm.Acrylic.cs`, connect it in `DesktopSurfaceManager.cs`, and retain/dispose the monitor foreground bitmap in `DesktopBoxForm.cs`.
- [x] Add dirty uploads and previous-dirty synchronization to `DesktopAcrylicHost.cs`. Reuse the D2D bitmap in `CompositionBitmapDevice.cs`, using the SDK's `ID2D1Bitmap.CopyFromMemory` (slot 10).
- [x] Batch all positive word clips into one winding path in `DesktopLyricsVisual.cs`, with one accent draw for the combined region.
- [x] Cache the rows preceding and following the active row in separate bitmaps to preserve paint order and avoid repeated inactive-line scaling. Rebuild on track, anchor, appearance, line count, expired-line placement or viewport changes; retain the original render during transitions.
- [x] Run the lyrics, acrylic, widget, cross-monitor, and rendering regression tests with the running instance exited through `--exit`. Compare deterministic rendering cost and inspect exported playback/transition frames.
- [x] Start the verified build and measure 40 seconds of live CPU, working set, private memory, threads and handles. Report actual improvement and any remaining limitations.

## Verification commands

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release -p:Platform=x64 --filter 'FullyQualifiedName~DesktopLyrics|FullyQualifiedName~MediaPlayerIcons|FullyQualifiedName~Acrylic|FullyQualifiedName~DesktopWidgetRuntime|FullyQualifiedName~CrossMonitorWidgetAndBoxDrag|FullyQualifiedName~BackupBoxRendering' --verbosity minimal
```

The live baseline from the preceding investigation was 3.42% average / 4.27% peak CPU across 20 logical processors, 539–556 MiB working set, and 394–411 MiB private memory. Live comparisons must keep the same desktop configuration and note that song length/timing can vary. Do not commit or push.

## Results

- Final Release build succeeded with existing warnings. All 290 selected regression cases were verified: the last broad run passed 288; the two adjacent-box cases used the default 38-DIP title bar instead of the scene's 32-DIP title bar, and passed after correcting that fixture in a focused nine-test run.
- The deterministic 120-frame case decreased from 24.003 ms/frame and 11,541 allocated bytes/frame to 9.176 ms/frame and 811 allocated bytes/frame.
- Exported steady-frame RGB differences versus the preceding renderer were at most 2/255; the sampled transition frame was identical. Inspected dark nine-row and light/dark long-line output.
- Final live 40-second sample: average CPU 1.75%, peak 2.60%, working set 548.6 MiB, private memory 403.0 MiB, 133 threads, 2,129 handles. The application remained responsive. Memory usage was essentially unchanged; the main gain is rendering CPU and allocation pressure.
- The lyric dirty rectangle uses two DIPs for edge antialiasing, while neighboring-object checks retain their padding. Actual overlaps and active transforms continue through the full acrylic painter.
