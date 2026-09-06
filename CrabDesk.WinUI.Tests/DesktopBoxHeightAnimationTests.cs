using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Pins how smoothly a box expands and collapses. The frame budget is arithmetic
/// over two constants, but the drawing contract cannot be reached without a live
/// layered window and a GDI+ surface, so it is asserted on the source.
/// </summary>
public sealed class DesktopBoxHeightAnimationTests
{
    [Fact]
    public void AFullHeightChangeGetsEnoughFramesToReadAsMotion()
    {
        // Duration alone says nothing: the frame clock can only deliver one
        // frame per system tick, so the honest unit is frames. Twelve is the
        // floor at which the travel stops looking like a handful of steps.
        var frames = DesktopBoxForm.CalculateBoxHeightAnimationDuration(248, 248).TotalMilliseconds /
            DesktopAnimationFrameClock.IntervalMilliseconds;

        Assert.True(frames >= 12, $"A full expand only gets {frames:0.#} frames.");
    }

    [Fact]
    public void EvenTheShortestHeightChangeIsAnimatedRatherThanSnapped()
    {
        var frames = DesktopBoxForm.CalculateBoxHeightAnimationDuration(1, 248).TotalMilliseconds /
            DesktopAnimationFrameClock.IntervalMilliseconds;

        Assert.True(frames >= 6, $"The shortest change only gets {frames:0.#} frames.");
    }

    [Fact]
    public void TheAnimatedEdgeIsPaintedThroughABrushInsteadOfAClipRegion()
    {
        var draw = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.Rendering.cs"),
            "private bool DrawHeightAnimationVisualCache(",
            "private void ReleaseHeightAnimationVisualCache(");

        // A GDI+ clip region is hard edged whatever the smoothing mode, so
        // clipping the cache to the rounded outline left the moving bottom edge
        // aliased on every frame and then snapped it smooth on the settled one.
        Assert.Contains("new TextureBrush(cache.Bitmap, WrapMode.Clamp)", draw, StringComparison.Ordinal);
        Assert.Contains("graphics.FillPath(brush, path);", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("graphics.SetClip(path", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("graphics.DrawImage(", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnimationFrameOptsBackIntoAntialiasingForThatOneFill()
    {
        var draw = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.Rendering.cs"),
            "private bool DrawHeightAnimationVisualCache(",
            "private void ReleaseHeightAnimationVisualCache(");

        // Animation frames are configured for speed, which would fill the
        // rounded outline aliased and undo the point of the brush.
        Assert.Contains(
            "graphics.SmoothingMode = SmoothingMode.AntiAlias;",
            draw,
            StringComparison.Ordinal);
        Assert.Contains(
            "graphics.SmoothingMode = fastRender ? SmoothingMode.HighSpeed : SmoothingMode.AntiAlias;",
            ReadRuntimeSource("DesktopIconSurface.cs"),
            StringComparison.Ordinal);

        // Save/Restore keeps that override from leaking into the rest of the frame.
        var save = draw.IndexOf("var state = graphics.Save();", StringComparison.Ordinal);
        var restore = draw.IndexOf("graphics.Restore(state);", StringComparison.Ordinal);
        Assert.True(save >= 0);
        Assert.True(restore > save);
    }

    [Fact]
    public void TheBrushUndoesTheCacheScaleSoTheBlitIsNotResampled()
    {
        var draw = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.Rendering.cs"),
            "private bool DrawHeightAnimationVisualCache(",
            "private void ReleaseHeightAnimationVisualCache(");

        // The cache holds device pixels anchored at a whole-pixel origin, so
        // inverting the scale leaves an exact integer translation once the
        // layer's own scale transform is applied.
        Assert.Contains("Transform = new Matrix(", draw, StringComparison.Ordinal);
        Assert.Contains("1f / (float)_scale,", draw, StringComparison.Ordinal);
        Assert.Contains("cache.Bounds.X,", draw, StringComparison.Ordinal);
        Assert.Contains("cache.Bounds.Y),", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameHandlerReusesItsScratchListsInsteadOfAllocatingPerFrame()
    {
        var frame = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.TitleEditor.cs"),
            "private void OnAnimationFrame(",
            // The collection phase ends where the clock decides whether to keep
            // running; everything asserted here belongs to that phase.
            "_animationFrameClock.StopWhenIdle(");

        // This runs for every frame of every animation; a gen0 collection landing
        // inside one is a dropped frame.
        Assert.Contains("var animatedBoxIds = _animationFrameBoxIds;", frame, StringComparison.Ordinal);
        Assert.Contains("animatedBoxIds.Clear();", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("_heightAnimations.Keys.ToArray()", frame, StringComparison.Ordinal);
        Assert.DoesNotContain(".Aggregate(", frame, StringComparison.Ordinal);

        // One scan per completed box, not one for its bounds and another for its
        // cache.
        Assert.Equal(1, CountOccurrences(frame, "DesktopBoxes.FirstOrDefault("));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReadRuntimeSource(string fileName) => File.ReadAllText(
        Path.Combine(FindSolutionDirectory(), "CrabDesk.Runtime", fileName));

    private static string ExtractMethod(string source, string startAnchor, string endAnchor)
    {
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        var end = source.IndexOf(endAnchor, Math.Max(0, start), StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startAnchor} was not found.");
        Assert.True(end > start, $"{endAnchor} was not found after {startAnchor}.");
        return source[start..end];
    }

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CrabDesk solution directory.");
    }
}
