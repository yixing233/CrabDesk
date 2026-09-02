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
