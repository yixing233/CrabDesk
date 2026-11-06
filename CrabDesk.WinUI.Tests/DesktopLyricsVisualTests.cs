using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopLyricsVisualTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReportManyWordRenderingCost() => InSta(() =>
    {
        const string text = "清晨的微光穿过窗边落在街道尽头风带着云朵向前走把心里的旋律慢慢唱给远方听";
        var document = new WidgetLyricsDocument(Enumerable.Range(0, 7).Select(index =>
        {
            var start = index * 10000;
            return new WidgetLyricLine(text, start, start + 9000,
                text.Select((character, word) => new WidgetLyricWord(character.ToString(),
                    start + word * 9000d / text.Length, start + (word + 1) * 9000d / text.Length)).ToArray());
        }).ToArray(), "Synthetic word timing");
        var snapshot = Snapshot(document, 37500);
        using var visual = new DesktopLyricsVisual();
        using var frame = new Bitmap(465, 365, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.ScaleTransform(1.25f, 1.25f);
        void Draw() => visual.Draw(graphics, snapshot, new RectangleF(0, 0, 372, 292),
            Color.White, Color.FromArgb(244, 75, 93), 7, 24, 0, true, true, 1.25, Epoch);
        for (var index = 0; index < 10; index++) Draw();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < 120; index++) Draw();
        timer.Stop();
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var output = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(output);
        var label = Environment.GetEnvironmentVariable("CRABDESK_PERF_LABEL") ?? "current";
        File.WriteAllText(Path.Combine(output, $"lyrics-render-cost-{label}.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Frames = 120, MillisecondsPerFrame = timer.Elapsed.TotalMilliseconds / 120,
                AllocatedBytesPerFrame = bytes / 120 }));
        Assert.True(AccentPixels(frame) > 100);
    });

    [Fact]
    public void LineIntervalsLeavePreludeGapsAndEndingUnhighlighted()
    {
        var document = new WidgetLyricsDocument([
            new("微光落下", 1000, 4000), new("风经过窗", 5000, 8000)
        ], "Fixture");
        Assert.Equal(-1, DesktopLyricsVisual.FindActiveLine(document, 999));
        Assert.Equal(0, DesktopLyricsVisual.FindActiveLine(document, 1000));
        Assert.Equal(0, DesktopLyricsVisual.FindActiveLine(document, 3999));
        Assert.Equal(-1, DesktopLyricsVisual.FindActiveLine(document, 4000));
        Assert.Equal(-1, DesktopLyricsVisual.FindActiveLine(document, 4999));
        Assert.Equal(1, DesktopLyricsVisual.FindActiveLine(document, 5000));
        Assert.Equal(-1, DesktopLyricsVisual.FindActiveLine(document, 8000));
        Assert.Equal(-1, DesktopLyricsVisual.FindActiveLine(document, 80000));
    }

    [Fact]
    public void WordCoverageUsesEachActualDurationAndHoldsAcrossGaps()
    {
        var shortWord = new WidgetLyricWord("微光", 1000, 1500);
        var longWord = new WidgetLyricWord("落在云端", 3000, 7000);
        Assert.Equal(0, DesktopLyricsVisual.WordProgress(shortWord, 999));
        Assert.Equal(.5, DesktopLyricsVisual.WordProgress(shortWord, 1250));
        Assert.Equal(1, DesktopLyricsVisual.WordProgress(shortWord, 2400));
        Assert.Equal(0, DesktopLyricsVisual.WordProgress(longWord, 2400));
        Assert.Equal(.25, DesktopLyricsVisual.WordProgress(longWord, 4000));
        Assert.Equal(1, DesktopLyricsVisual.WordProgress(longWord, 7000));
    }

    [Fact]
    public void PausingFreezesPixelsAndStopsAnimation() => InSta(() =>
    {
        using var visual = new DesktopLyricsVisual();
        var snapshot = Snapshot(TimedDocument(), 1350, playing: false);
        using var first = Render(visual, snapshot, Epoch);
        using var later = Render(visual, snapshot, Epoch.AddSeconds(8));
        Assert.Equal(Pixels(first), Pixels(later));
        Assert.False(visual.NeedsAnimation);
    });

    [Fact]
    public void PlaybackColorsOnlyTimedWordsAndStopsColoringAtLineEnd() => InSta(() =>
    {
        using var visual = new DesktopLyricsVisual();
        var snapshot = Snapshot(TimedDocument(), 1100, playing: true);
        using var first = Render(visual, snapshot, Epoch);
        using var covered = Render(visual, snapshot, Epoch.AddMilliseconds(300));
        Assert.True(AccentPixels(covered) > AccentPixels(first));
        using var gapStart = Render(visual, snapshot, Epoch.AddMilliseconds(600));
        using var gapEnd = Render(visual, snapshot, Epoch.AddMilliseconds(1500));
        Assert.Equal(Pixels(gapStart), Pixels(gapEnd));
        using var ended = Render(visual, snapshot, Epoch.AddMilliseconds(8000));
        Assert.Equal(0, AccentPixels(ended));
        Assert.False(visual.NeedsAnimation);
    });

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 5)]
    [InlineData(false, 1)]
    [InlineData(false, 5)]
    public void CompletedWordCoverageHoldsUntilTheNextLineStartsCovering(bool animations, int rows) => InSta(() =>
    {
        var snapshot = Snapshot(GappedDocument(), 4000, playing: true);
        using var visual = new DesktopLyricsVisual();
        using var completed = Render(visual, snapshot, Epoch, rows: rows, animations: animations);
        Assert.True(AccentPixels(completed) > 100);
        foreach (var elapsed in new[] { 1, 999, 1000, 1350, 1999, 2000 })
        {
            using var waiting = Render(visual, snapshot, Epoch.AddMilliseconds(elapsed), rows: rows, animations: animations);
            Assert.Equal(Pixels(completed), Pixels(waiting));
        }
        using var next = Render(visual, snapshot, Epoch.AddMilliseconds(2100), rows: rows, animations: animations);
        Assert.True(AccentPixels(next) > 0);
        using var settled = Render(visual, snapshot, Epoch.AddMilliseconds(2500), rows: rows, animations: animations);
        using var freshVisual = new DesktopLyricsVisual();
        using var expected = Render(freshVisual, snapshot, Epoch.AddMilliseconds(2500), rows: rows, animations: animations);
        Assert.Equal(Pixels(expected), Pixels(settled));
    });

    [Fact]
    public void PausingAndSeekingIntoALineGapPreservesCompletedCoverage() => InSta(() =>
    {
        var document = GappedDocument();
        using var visual = new DesktopLyricsVisual();
        using var completed = Render(visual, Snapshot(document, 4000), Epoch);
        Assert.True(AccentPixels(completed) > 100);
        var paused = Snapshot(document, 4500);
        using var waiting = Render(visual, paused, Epoch.AddSeconds(1));
        using var later = Render(visual, paused, Epoch.AddSeconds(10));
        Assert.Equal(Pixels(completed), Pixels(waiting));
        Assert.Equal(Pixels(waiting), Pixels(later));
        Assert.False(visual.NeedsAnimation);
        using var freshVisual = new DesktopLyricsVisual();
        using var sought = Render(freshVisual, paused, Epoch);
        Assert.Equal(Pixels(waiting), Pixels(sought));
    });

    [Fact]
    public void LineOnlyLyricsStayFullyHighlightedWithinTheirInterval() => InSta(() =>
    {
        var document = new WidgetLyricsDocument([new("微光落在云端", 1000, 6000)], "Fixture");
        var snapshot = Snapshot(document, 1500, playing: true);
        using var visual = new DesktopLyricsVisual();
        using var first = Render(visual, snapshot, Epoch);
        using var later = Render(visual, snapshot, Epoch.AddSeconds(3));
        Assert.Equal(Pixels(first), Pixels(later));
        Assert.True(AccentPixels(first) > 100);
    });

    [Fact]
    public void AdjacentLineTransitionMovesThenSettlesAtTheCorrectFrame() => InSta(() =>
    {
        var document = ReviewDocument();
        var snapshot = Snapshot(document, 24951, playing: true);
        using var visual = new DesktopLyricsVisual();
        using var previous = Render(visual, snapshot, Epoch);
        using var first = Render(visual, snapshot, Epoch.AddMilliseconds(50));
        Assert.True(visual.NeedsAnimation);
        using var middle = Render(visual, snapshot, Epoch.AddMilliseconds(175));
        Assert.False(Pixels(first).SequenceEqual(Pixels(middle)));
        using var settled = Render(visual, snapshot, Epoch.AddMilliseconds(450));
        using var freshVisual = new DesktopLyricsVisual();
        using var fresh = Render(freshVisual, snapshot, Epoch.AddMilliseconds(450));
        Assert.Equal(Pixels(fresh), Pixels(settled));
    });

    [Fact]
    public void DepartingHighlightFadesContinuouslyAtTransitionEnd() => InSta(() =>
    {
        var snapshot = Snapshot(ReviewDocument(), 24951, playing: true);
        using var visual = new DesktopLyricsVisual();
        using var previous = Render(visual, snapshot, Epoch);
        using var start = Render(visual, snapshot, Epoch.AddMilliseconds(50));
        using var beforeEnd = Render(visual, snapshot, Epoch.AddMilliseconds(399));
        using var atEnd = Render(visual, snapshot, Epoch.AddMilliseconds(400));
        var before = Pixels(beforeEnd);
        var after = Pixels(atEnd);
        var changedPixels = 0;
        for (var index = 0; index < before.Length; index += 4)
            if (Math.Abs(before[index] - after[index]) > 10
                || Math.Abs(before[index + 1] - after[index + 1]) > 10
                || Math.Abs(before[index + 2] - after[index + 2]) > 10) changedPixels++;
        Assert.InRange(changedPixels, 0, 100);
    });

    [Fact]
    public void DisabledAnimationsAndMultiLineSeeksSelectTheirDestinationImmediately() => InSta(() =>
    {
        var document = ReviewDocument();
        using var visual = new DesktopLyricsVisual();
        using var initial = Render(visual, Snapshot(document, 5000), Epoch);
        var seek = Snapshot(document, 29300);
        using var afterSeek = Render(visual, seek, Epoch.AddMilliseconds(10));
        using var freshVisual = new DesktopLyricsVisual();
        using var expected = Render(freshVisual, seek, Epoch.AddMilliseconds(10));
        Assert.Equal(Pixels(expected), Pixels(afterSeek));
        Assert.False(visual.NeedsAnimation);

        var nextLine = Snapshot(document, 33300);
        using var disabled = Render(visual, nextLine, Epoch.AddMilliseconds(20), animations: false);
        using var nextVisual = new DesktopLyricsVisual();
        using var expectedNext = Render(nextVisual, nextLine, Epoch.AddMilliseconds(20), animations: false);
        Assert.Equal(Pixels(expectedNext), Pixels(disabled));
        Assert.False(visual.NeedsAnimation);
    });

    [Fact]
    public void NarrowUnicodeAndEvenLineCountsRenderInsideTransparentBounds() => InSta(() =>
    {
        var document = new WidgetLyricsDocument([
            new("微光 🌙 在长长的街道尽头经过 Café e\u0301 与云层之间，仍有一盏灯", 1000, 4000),
            new("风吹过窗 — نسيم", 4000, 7000),
            new("夜色轻轻停靠", 7000, 11000)
        ], "Fixture");
        using var visual = new DesktopLyricsVisual();
        using var frame = Render(visual, Snapshot(document, 4500), Epoch, rows: 4, width: 160, height: 210, scale: 1.5);
        Assert.Contains(Pixels(frame), value => value != 0);
        for (var x = 0; x < frame.Width; x++)
        {
            Assert.Equal(0, frame.GetPixel(x, 0).A);
            Assert.Equal(0, frame.GetPixel(x, frame.Height - 1).A);
        }
        for (var y = 0; y < frame.Height; y++)
        {
            Assert.Equal(0, frame.GetPixel(0, y).A);
            Assert.Equal(0, frame.GetPixel(frame.Width - 1, y).A);
        }
    });

    [Theory]
    [InlineData(1, 1d)]
    [InlineData(5, 1d)]
    [InlineData(5, 1.5d)]
    public void LengthIndependentFontSizesKeepTheSameGlyphHeightAtEachDepth(int rows, double scale) => InSta(() =>
    {
        WidgetLyricsDocument Document(int length) => new(Enumerable.Range(0, 5)
            .Select(index => new WidgetLyricLine(new string('晨', length), 1000 + index * 10000, 11000 + index * 10000))
            .ToArray(), "Fixture");
        using var shortVisual = new DesktopLyricsVisual();
        using var longVisual = new DesktopLyricsVisual();
        using var shortFrame = Render(shortVisual, Snapshot(Document(3), 25000), Epoch, rows: rows, width: 240, height: 390, scale: scale);
        using var longFrame = Render(longVisual, Snapshot(Document(32), 25000), Epoch, rows: rows, width: 240, height: 390, scale: scale);
        for (var depth = -(rows - 1) / 2; depth <= (rows - 1) / 2; depth++)
        {
            var center = (int)Math.Round((195 + depth * 52.5) * scale);
            var band = (int)Math.Floor(25 * scale);
            var shortHeight = GlyphHeight(shortFrame, center, band);
            var longHeight = GlyphHeight(longFrame, center, band);
            Assert.True(shortHeight > 0);
            Assert.InRange(longHeight - shortHeight, -1, 1);
        }
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void LongLyricsScrollWithPlaybackAndFreezeWhenPaused(bool timedWords, bool animations) => InSta(() =>
    {
        var document = LongDocument(timedWords);
        var snapshot = Snapshot(document, 1500, playing: true);
        using var visual = new DesktopLyricsVisual();
        using var early = Render(visual, snapshot, Epoch, rows: 1, width: 240, animations: animations, wordHighlight: false);
        using var middle = Render(visual, snapshot, Epoch.AddSeconds(4), rows: 1, width: 240, animations: animations, wordHighlight: false);
        using var late = Render(visual, snapshot, Epoch.AddSeconds(7), rows: 1, width: 240, animations: animations, wordHighlight: false);
        Assert.False(Pixels(early).SequenceEqual(Pixels(middle)));
        Assert.False(Pixels(middle).SequenceEqual(Pixels(late)));
        // The visible characters change while scrolling; their ink bounds can differ by a pixel.
        Assert.InRange(GlyphHeight(late, 155, 30) - GlyphHeight(early, 155, 30), -1, 1);
        var paused = Snapshot(document, 5500);
        using var pausedFrame = Render(visual, paused, Epoch.AddSeconds(8), rows: 1, width: 240, animations: animations, wordHighlight: false);
        using var later = Render(visual, paused, Epoch.AddSeconds(20), rows: 1, width: 240, animations: animations, wordHighlight: false);
        Assert.Equal(Pixels(middle), Pixels(pausedFrame));
        Assert.Equal(Pixels(pausedFrame), Pixels(later));
        Assert.False(visual.NeedsAnimation);
        using var freshVisual = new DesktopLyricsVisual();
        using var sought = Render(freshVisual, paused, Epoch, rows: 1, width: 240, animations: animations, wordHighlight: false);
        Assert.Equal(Pixels(pausedFrame), Pixels(sought));
    });

    [Fact]
    public void LongLyricsInSurroundingRowsRemainStatic() => InSta(() =>
    {
        var text = LongDocument(true).Lines[0].Text;
        var document = new WidgetLyricsDocument([
            new(text, 1000, 3000), new("微光落下", 4000, 9000), new(text, 10000, 17000)
        ], "Fixture");
        var snapshot = Snapshot(document, 4500, playing: true);
        using var visual = new DesktopLyricsVisual();
        using var first = Render(visual, snapshot, Epoch, width: 240);
        using var later = Render(visual, snapshot, Epoch.AddSeconds(2), width: 240);
        Assert.Equal(Pixels(first), Pixels(later));
    });

    [Fact]
    public void LongLyricsKeepCompletedCoverageAndScrollPositionAcrossALineGap() => InSta(() =>
    {
        var firstLine = LongDocument(true).Lines[0];
        var document = new WidgetLyricsDocument([
            firstLine, new("风经过窗", 11000, 15000, [new("风经过窗", 12000, 15000)])
        ], "Fixture");
        var snapshot = Snapshot(document, 9000, playing: true);
        using var visual = new DesktopLyricsVisual();
        using var completed = Render(visual, snapshot, Epoch, rows: 1, width: 240);
        Assert.True(AccentPixels(completed) > 100);
        using var gap = Render(visual, snapshot, Epoch.AddMilliseconds(2999), rows: 1, width: 240);
        Assert.Equal(Pixels(completed), Pixels(gap));
        using var next = Render(visual, snapshot, Epoch.AddMilliseconds(3100), rows: 1, width: 240);
        using var settled = Render(visual, snapshot, Epoch.AddMilliseconds(3500), rows: 1, width: 240);
        using var freshVisual = new DesktopLyricsVisual();
        using var expected = Render(freshVisual, snapshot, Epoch.AddMilliseconds(3500), rows: 1, width: 240);
        Assert.Equal(Pixels(expected), Pixels(settled));
    });

    [Fact]
    public void ExportLongLyricsScrollFrames() => InSta(() =>
    {
        var output = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "CrabDeskLyricsVisualReview");
        Directory.CreateDirectory(output);
        var longLine = LongDocument(true).Lines[0];
        var document = new WidgetLyricsDocument([
            new("窗外晨风轻轻吹过", 0, 1000), longLine,
            new("下一句继续向前", 10000, 15000)
        ], "Fixture");
        foreach (var dark in new[] { true, false })
        foreach (var position in new[] { 1500, 5500, 8500 })
        {
            using var visual = new DesktopLyricsVisual();
            using var frame = Render(visual, Snapshot(document, position), Epoch, width: 360, dark: dark);
            SaveReview(frame, output, $"long-lyrics-{(dark ? "dark" : "light")}-{position}", dark);
        }
    });

    [Fact]
    public void ReplacingDocumentRebuildsCachedText() => InSta(() =>
    {
        using var visual = new DesktopLyricsVisual();
        using var first = Render(visual, Snapshot(TimedDocument(), 1400), Epoch);
        var changed = new WidgetLyricsDocument([new("雨停在远山之外", 1000, 7000)], "Fixture B");
        using var second = Render(visual, Snapshot(changed, 1400), Epoch);
        using var freshVisual = new DesktopLyricsVisual();
        using var expected = Render(freshVisual, Snapshot(changed, 1400), Epoch);
        Assert.False(Pixels(first).SequenceEqual(Pixels(second)));
        Assert.Equal(Pixels(expected), Pixels(second));
    });

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("rows")]
    [InlineData("font")]
    [InlineData("color")]
    [InlineData("scale")]
    [InlineData("family")]
    [InlineData("weight")]
    [InlineData("style")]
    public void CachedSurroundingLyricsFollowLayoutAndAppearanceChanges(string change) => InSta(() =>
    {
        var snapshot = Snapshot(ReviewDocument(), 26600);
        using var visual = new DesktopLyricsVisual();
        using var initial = Render(visual, snapshot, Epoch);
        var rows = change == "rows" ? 9 : 5;
        var width = change == "width" ? 240 : 480;
        var height = change == "height" ? 210 : 310;
        var scale = change == "scale" ? 1.25 : 1;
        var dark = change != "color";
        var fontSize = change == "font" ? 48 : 30;
        var family = change == "family" ? "Consolas" : null;
        var weight = change == "weight" ? 300 : 600;
        var style = change == "style" ? WidgetLyricsFontStyle.Italic : WidgetLyricsFontStyle.Normal;
        using var changed = Render(visual, snapshot, Epoch, rows: rows, width: width, height: height,
            scale: scale, dark: dark, fontSize: fontSize, fontFamily: family, fontWeight: weight, fontStyle: style);
        using var freshVisual = new DesktopLyricsVisual();
        using var expected = Render(freshVisual, snapshot, Epoch, rows: rows, width: width, height: height,
            scale: scale, dark: dark, fontSize: fontSize, fontFamily: family, fontWeight: weight, fontStyle: style);
        Assert.Equal(Pixels(expected), Pixels(changed));
    });

    [Fact]
    public void FamilyWeightAndItalicChangeTheActualGlyphMasks() => InSta(() =>
    {
        var snapshot = Snapshot(new WidgetLyricsDocument([new("Wide iii narrow MMM melody", 0, 10000)], "Typography"), 2000);
        using var visual = new DesktopLyricsVisual();
        using var regular = Render(visual, snapshot, Epoch, rows: 1, animations: false, height: 120, fontFamily: "Arial", fontWeight: 400);
        using var heavy = Render(visual, snapshot, Epoch, rows: 1, animations: false, height: 120, fontFamily: "Arial", fontWeight: 900);
        using var mono = Render(visual, snapshot, Epoch, rows: 1, animations: false, height: 120, fontFamily: "Consolas", fontWeight: 400);
        using var italic = Render(visual, snapshot, Epoch, rows: 1, animations: false, height: 120, fontFamily: "Arial", fontWeight: 400,
            fontStyle: WidgetLyricsFontStyle.Italic);
        Assert.False(Pixels(regular).SequenceEqual(Pixels(heavy)));
        Assert.False(Pixels(regular).SequenceEqual(Pixels(mono)));
        Assert.False(Pixels(regular).SequenceEqual(Pixels(italic)));
        long Alpha(Bitmap bitmap) => Pixels(bitmap).Where((_, index) => index % 4 == 3).Sum(value => (long)value);
        Assert.True(Alpha(heavy) > Alpha(regular), "Heavier text must have denser glyph coverage.");
        var directory = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            regular.Save(Path.Combine(directory, "lyrics-font-regular.png"));
            heavy.Save(Path.Combine(directory, "lyrics-font-heavy.png"));
            italic.Save(Path.Combine(directory, "lyrics-font-italic.png"));
        }
    });

    [Theory]
    [InlineData("Arial", 300, WidgetLyricsFontStyle.Normal)]
    [InlineData("Arial", 900, WidgetLyricsFontStyle.Italic)]
    [InlineData("Consolas", 700, WidgetLyricsFontStyle.Italic)]
    public void CustomTypographyKeepsWordCoverageAlignedAndHoldsAcrossGaps(string family, int weight, WidgetLyricsFontStyle style) => InSta(() =>
    {
        var document = new WidgetLyricsDocument([new("Wide glyphs", 1000, 5000,
            [new("Wide", 1000, 1500), new("glyphs", 2500, 5000)])], "Typography timing");
        var snapshot = Snapshot(document, 1100, playing: true);
        using var visual = new DesktopLyricsVisual();
        Bitmap Draw(int elapsed) => Render(visual, snapshot, Epoch.AddMilliseconds(elapsed), rows: 1, animations: false,
            fontFamily: family, fontWeight: weight, fontStyle: style);
        using var first = Draw(0);
        using var covered = Draw(400);
        using var waiting = Draw(1300);
        Assert.True(AccentPixels(covered) > AccentPixels(first));
        Assert.Equal(Pixels(covered), Pixels(waiting));
        using var next = Draw(2000);
        Assert.True(AccentPixels(next) > AccentPixels(covered));
    });

    [Fact]
    public void CachedRowsFollowFractionalHeightChangesWithoutAFrameResize() => InSta(() =>
    {
        var snapshot = Snapshot(ReviewDocument(), 26600);
        Bitmap DrawAt(DesktopLyricsVisual visual, float height)
        {
            var frame = new Bitmap(465, 400, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(frame);
            graphics.ScaleTransform(1.25f, 1.25f);
            visual.Draw(graphics, snapshot, new RectangleF(0, 0, 372, height), Color.White,
                Color.CornflowerBlue, 7, 24, 0, true, false, 1.25, Epoch);
            return frame;
        }
        using var visual = new DesktopLyricsVisual();
        using var initial = DrawAt(visual, 292.2f);
        using var changed = DrawAt(visual, 292.4f);
        using var freshVisual = new DesktopLyricsVisual();
        using var expected = DrawAt(freshVisual, 292.4f);
        Assert.Equal(Pixels(expected), Pixels(changed));
    });

    [Fact]
    public void ExportDeterministicLightDarkAndTransitionFrames() => InSta(() =>
    {
        var output = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "CrabDeskLyricsVisualReview");
        Directory.CreateDirectory(output);
        var document = ReviewDocument();
        foreach (var dark in new[] { true, false })
        foreach (var rows in new[] { 1, 5, 9 })
        {
            using var visual = new DesktopLyricsVisual();
            using var frame = Render(visual, Snapshot(document, 26600), Epoch,
                rows: rows, height: rows == 9 ? 390 : rows == 1 ? 170 : 310, dark: dark);
            SaveReview(frame, output, $"lyrics-{(dark ? "dark" : "light")}-{rows}", dark);
        }
        var transitionSnapshot = Snapshot(document, 24951, playing: true);
        using var transition = new DesktopLyricsVisual();
        using var before = Render(transition, transitionSnapshot, Epoch);
        SaveReview(before, output, "transition-before", dark: true);
        foreach (var elapsed in new[] { 50, 120, 225, 350, 399, 400 })
        {
            using var frame = Render(transition, transitionSnapshot, Epoch.AddMilliseconds(elapsed));
            SaveReview(frame, output, $"transition-{elapsed - 50:000}ms", dark: true);
        }
        Assert.True(File.Exists(Path.Combine(output, "lyrics-dark-5.png")));
    });

    private static WidgetLyricsDocument TimedDocument() => new([
        new("微光 落在云端", 1000, 7000, [new("微光", 1000, 1500), new("落在云端", 3000, 7000)])
    ], "Fixture");

    private static WidgetLyricsDocument GappedDocument() => new([
        new("微光落下", 1000, 4000, [new("微光", 1000, 2000), new("落下", 2000, 4000)]),
        new("风经过窗", 5000, 9000, [new("风", 6000, 7000), new("经过窗", 7000, 9000)])
    ], "Fixture");

    private static WidgetLyricsDocument LongDocument(bool timedWords)
    {
        const string text = "清晨的微光穿过窗边落在街道尽头，风带着云朵向前走，把心里的旋律慢慢唱给远方听。";
        return new([new(text, 1000, 9000, timedWords ? [new(text, 1000, 9000)] : null)], "Fixture");
    }

    private static WidgetLyricsDocument ReviewDocument()
    {
        string[] phrases = ["晨风 走过 屋檐", "远山 留下 回声", "窗边 开着 小花", "雨点 写下 问候",
            "城市 慢慢 醒来", "你将 星光 收藏", "微光 落在 云端", "沿着 河流 向前", "把梦 交给 清晨",
            "脚步 轻轻 回响", "云朵 靠近 天边", "晚风 送来 晴朗", "明天 仍有 远方"];
        return new WidgetLyricsDocument(phrases.Select((text, index) =>
        {
            var start = 1000 + index * 4000;
            var words = text.Split(' ');
            return new WidgetLyricLine(text, start, start + 4000, [
                new(words[0], start, start + 900), new(words[1], start + 1100, start + 2300),
                new(words[2], start + 2600, start + 3700)]);
        }).ToArray(), "Synthetic word timing");
    }

    private static WidgetLyricsSnapshot Snapshot(WidgetLyricsDocument document, double position, bool playing = false) => new(
        new ControlCenterMedia("云端微光", "示例歌手", "合成歌词", playing, true, true, true)
        {
            Position = TimeSpan.FromMilliseconds(position), Duration = TimeSpan.FromSeconds(90), CapturedAt = Epoch
        }, document);

    private static Bitmap Render(DesktopLyricsVisual visual, WidgetLyricsSnapshot snapshot, DateTimeOffset now,
        int rows = 5, bool animations = true, int width = 480, int height = 310, double scale = 1, bool dark = true,
        bool wordHighlight = true, double fontSize = 30, string? fontFamily = null, int fontWeight = 600,
        WidgetLyricsFontStyle fontStyle = WidgetLyricsFontStyle.Normal)
    {
        var frame = new Bitmap((int)(width * scale), (int)(height * scale), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform((float)scale, (float)scale);
        visual.Draw(graphics, snapshot, new RectangleF(10, 0, width - 20, height),
            dark ? Color.FromArgb(240, 242, 247) : Color.FromArgb(32, 39, 53),
            Color.FromArgb(244, 75, 93), rows, fontSize, 0, wordHighlight, animations, scale, now, fontFamily, fontWeight, fontStyle);
        return frame;
    }

    private static int GlyphHeight(Bitmap bitmap, int center, int band)
    {
        var top = bitmap.Height;
        var bottom = -1;
        for (var y = Math.Max(0, center - band); y <= Math.Min(bitmap.Height - 1, center + band); y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).A > 20) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        return bottom < top ? 0 : bottom - top + 1;
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[data.Stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static int AccentPixels(Bitmap bitmap)
    {
        var pixels = Pixels(bitmap);
        var count = 0;
        for (var index = 0; index < pixels.Length; index += 4)
            if (pixels[index + 2] > pixels[index + 1] * 1.5 && pixels[index + 3] > 20) count++;
        return count;
    }

    private static void SaveReview(Bitmap frame, string output, string name, bool dark)
    {
        frame.Save(Path.Combine(output, name + "-transparent.png"), ImageFormat.Png);
        using var preview = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(dark ? Color.FromArgb(24, 29, 40) : Color.FromArgb(243, 245, 249));
            graphics.DrawImageUnscaled(frame, 0, 0);
        }
        preview.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
    }

    private static void InSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Lyrics rendering did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
