using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Pins how the two desktop surfaces wire Shift+click into the shared range
/// policy. The behaviour of the range itself is covered by
/// <see cref="DesktopSelectionPolicyTests"/>; what cannot be reached without a
/// live shell — which order the surface reports, which box the range may span,
/// and that Shift never moves the anchor — is asserted on the source.
/// </summary>
public sealed class DesktopRangeSelectionTests
{
    [Fact]
    public void TheDesktopRangeFollowsTheColumnMajorGridInsteadOfTheItemListOrder()
    {
        // RebuildGeometry appends items with a stored cell before auto-placed
        // ones, so _items order is not what the user reads down each column.
        var readingOrder = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "private IReadOnlyList<string> GetReadingOrderKeys()",
            "private void OnMouseDown(object? sender");

        Assert.Contains("OrderBy(item => item.Cell.Column)", readingOrder, StringComparison.Ordinal);
        Assert.Contains("ThenBy(item => item.Cell.Row)", readingOrder, StringComparison.Ordinal);
    }

    [Fact]
    public void ADesktopShiftClickExtendsFromTheAnchorAndLeavesItInPlace()
    {
        var mouseDown = ExtractDesktopMouseDown();
        var rangeBranch = ExtractMethod(
            mouseDown,
            "if (shiftPressed)",
            "DesktopSelectionGesture.PrimaryItem,");

        Assert.Contains(
            "DesktopSelectionPolicy.BuildRangeSelectionKeys(",
            rangeBranch,
            StringComparison.Ordinal);
        Assert.Contains("GetReadingOrderKeys()", rangeBranch, StringComparison.Ordinal);
        Assert.Contains("DesktopSelectionGesture.RangeItem", rangeBranch, StringComparison.Ordinal);

        // Ctrl+Shift unions with what is already selected; plain Shift owns it.
        Assert.Contains("if (!controlPressed)", rangeBranch, StringComparison.Ordinal);
        Assert.Contains("_selection.Clear();", rangeBranch, StringComparison.Ordinal);

        // Re-anchoring here would make each further Shift+click grow the range
        // by one cell instead of re-measuring it from the original item.
        Assert.DoesNotContain("_selectionAnchorKey =", rangeBranch, StringComparison.Ordinal);

        // Only a press that is not a range extension moves the anchor.
        Assert.Contains("_selectionAnchorKey = itemKey;", mouseDown, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(mouseDown, "_selectionAnchorKey = itemKey;"));
    }

    [Fact]
    public void ABoxShiftClickStaysInsideTheAnchorsOwnBox()
    {
        var source = ReadRuntimeSource("DesktopBoxForm.Input.cs");
        var mouseDown = ExtractMethod(
            source,
            "private void OnMouseDown(object? sender",
            "private bool TryApplyRangeSelection(");
        var rangeSelection = ExtractMethod(
            source,
            "private bool TryApplyRangeSelection(",
            "private void OnMouseMove(object? sender");

        Assert.Contains("_selectionAnchorBoxId == item.Box.Id &&", mouseDown, StringComparison.Ordinal);
        Assert.Contains("candidate.Box.Id == item.Box.Id", rangeSelection, StringComparison.Ordinal);
        Assert.Contains(
            "DesktopSelectionPolicy.BuildRangeSelectionKeys(",
            rangeSelection,
            StringComparison.Ordinal);
        Assert.Contains("DesktopSelectionGesture.RangeItem", rangeSelection, StringComparison.Ordinal);
        Assert.Contains("if (!controlPressed)", rangeSelection, StringComparison.Ordinal);

        // A missing anchor must degrade to a plain click, never to an empty
        // selection, so the helper reports failure instead of clearing.
        Assert.Contains("if (range.Count == 0)", rangeSelection, StringComparison.Ordinal);
        Assert.Contains("return false;", rangeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectionAnchorKey =", rangeSelection, StringComparison.Ordinal);

        Assert.Contains("_selectionAnchorKey = key;", mouseDown, StringComparison.Ordinal);
        Assert.Contains("_selectionAnchorBoxId = item.Box.Id;", mouseDown, StringComparison.Ordinal);
    }

    [Fact]
    public void AShiftDragKeepsTheSelectionOnBothSurfacesJustLikeCtrl()
    {
        // Otherwise the rubber band would throw away the range the user had
        // only just built with Shift+click.
        var desktopMarquee = ExtractMethod(
            ExtractDesktopMouseDown(),
            "var additive = (DesktopWindowTools.GetAsyncModifierKeys() &",
            "var itemKey = item.Item.Key.ToString();");
        var boxMarquee = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.Input.cs"),
            "else if (box.Body.Contains(point))",
            "if (_movingBox is not null || _resizingBox is not null)");

        foreach (var marquee in new[] { desktopMarquee, boxMarquee })
        {
            Assert.Contains(
                "(Forms.Keys.Control | Forms.Keys.Shift)) != 0;",
                marquee,
                StringComparison.Ordinal);
            Assert.Contains("_selectionAnchorKey = null;", marquee, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LosingTheSelectionToAnotherSurfaceAlsoDropsTheAnchor()
    {
        var desktopClear = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "internal void ClearSelection()",
            "internal bool CommitActiveInlineRename()");
        var boxClear = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.cs"),
            "internal void ClearSelection()",
            "internal bool HasSelection");

        // Before the early return: a surface can hold an anchor from a click
        // whose selection another surface has since taken over.
        foreach (var clear in new[] { desktopClear, boxClear })
        {
            Assert.True(
                clear.IndexOf("_selectionAnchorKey = null;", StringComparison.Ordinal) <
                clear.IndexOf("if (_selection.Count == 0)", StringComparison.Ordinal),
                "The anchor must be dropped before ClearSelection can return early.");
        }

        Assert.Contains("_selectionAnchorBoxId = null;", boxClear, StringComparison.Ordinal);
    }

    private static string ExtractDesktopMouseDown() => ExtractMethod(
        ReadRuntimeSource("DesktopIconSurface.cs"),
        "private void OnMouseDown(object? sender",
        "private void OnMouseMove(object? sender");

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
