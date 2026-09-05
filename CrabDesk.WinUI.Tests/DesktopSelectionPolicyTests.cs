using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopSelectionPolicyTests
{
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(0, false, true, true)]
    [InlineData(0, true, false, true)]
    [InlineData(0, true, true, true)]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, false, true)]
    [InlineData(2, false, false, false)]
    [InlineData(2, false, true, true)]
    [InlineData(3, false, true, false)]
    [InlineData(3, true, false, true)]
    public void PreserveExistingSelectionMatchesUnifiedGestureRules(
        int gesture,
        bool additive,
        bool targetAlreadySelected,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopSelectionPolicy.PreserveExistingSelection(
                (DesktopSelectionGesture)gesture,
                additive,
                targetAlreadySelected));
    }

    [Theory]
    [InlineData("b", "d", "b,c,d")]
    [InlineData("d", "b", "b,c,d")]
    [InlineData("c", "c", "c")]
    [InlineData("a", "e", "a,b,c,d,e")]
    public void RangeSelectionSpansBothDirectionsInclusively(
        string anchor,
        string target,
        string expected)
    {
        Assert.Equal(
            expected.Split(','),
            DesktopSelectionPolicy.BuildRangeSelectionKeys(
                ["a", "b", "c", "d", "e"],
                anchor,
                target));
    }

    [Fact]
    public void RangeSelectionIsEmptyWhenEitherEndIsNoLongerLaidOut()
    {
        // An anchor can disappear between two clicks: a box search filters it
        // out, a scroll drops it from the visible band, a tab switch hides it.
        // Callers read the empty range as "treat this as a plain click", which
        // is why no surface has to prune the anchor on every mutation.
        Assert.Empty(DesktopSelectionPolicy.BuildRangeSelectionKeys(["a", "b"], "missing", "b"));
        Assert.Empty(DesktopSelectionPolicy.BuildRangeSelectionKeys(["a", "b"], "a", "missing"));
        Assert.Empty(DesktopSelectionPolicy.BuildRangeSelectionKeys(["a", "b"], null, "b"));
        Assert.Empty(DesktopSelectionPolicy.BuildRangeSelectionKeys([], "a", "a"));
    }

    [Fact]
    public void RangeSelectionMatchesItemKeysCaseInsensitivelyLikeTheSelectionSets()
    {
        Assert.Equal(
            new[] { "path:A", "path:B" },
            DesktopSelectionPolicy.BuildRangeSelectionKeys(
                ["path:A", "path:B", "path:C"],
                "PATH:a",
                "path:b"));
    }

    [Fact]
    public void DeleteSelectionDeduplicatesPathsAndCountsBlockedItems()
    {
        var firstPath = @"C:\Desktop\first.txt";
        var secondPath = @"C:\Mapped\second.txt";
        var firstDesktop = Item("desktop-first", firstPath);
        var firstBox = Item("box-first", firstPath.ToUpperInvariant());
        var readOnlyBox = Item("readonly-second", secondPath);
        var shellItem = Item("ThisPC");

        var result = DesktopSelectionPolicy.BuildDeleteSelection(
            [firstDesktop, firstBox, readOnlyBox, shellItem],
            [firstDesktop]);

        Assert.Equal(3, result.SelectedCount);
        Assert.Single(result.DeletableItems);
        Assert.Equal(firstPath, result.DeletableItems[0].FileSystemPath);
        Assert.Equal(2, result.BlockedCount);
    }

    private static DesktopItemRef Item(string key, string? path = null) => new()
    {
        Key = new DesktopItemKey(path is null ? "shell" : "path", key),
        DisplayName = key,
        ParsingName = path ?? key,
        FileSystemPath = path,
        Kind = path is null ? DesktopItemKind.Shell : DesktopItemKind.File
    };
}
