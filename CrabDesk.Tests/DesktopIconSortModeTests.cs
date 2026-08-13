using CrabDesk.Native;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopIconSortModeTests
{
    [Theory]
    [InlineData(12, DesktopIconSortMode.Size)]
    [InlineData(4, DesktopIconSortMode.Type)]
    public void DecodesShellItemPropertySort(int propertyId, DesktopIconSortMode expected)
    {
        var value = CreatePropertyKey(
            new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
            propertyId);

        Assert.Equal(expected, DesktopIconPositionService.DecodeDesktopSortMode(value));
    }

    [Fact]
    public void DecodesDateModifiedSort()
    {
        var value = CreatePropertyKey(
            new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
            14);

        Assert.Equal(DesktopIconSortMode.Modified, DesktopIconPositionService.DecodeDesktopSortMode(value));
    }

    [Fact]
    public void DefaultsToNameForExplorerDefaultSort()
    {
        Assert.Equal(
            DesktopIconSortMode.Name,
            DesktopIconPositionService.DecodeDesktopSortMode(new byte[20]));
    }

    [Theory]
    [InlineData("prop:System.ItemNameDisplay;", DesktopIconSortMode.Name, false)]
    [InlineData("prop:System.Size;", DesktopIconSortMode.Size, false)]
    [InlineData("prop:-System.ItemTypeText;", DesktopIconSortMode.Type, true)]
    [InlineData("prop:-System.DateModified;", DesktopIconSortMode.Modified, true)]
    public void DecodesLiveExplorerSortColumns(
        string sortColumns,
        DesktopIconSortMode expectedMode,
        bool expectedDescending)
    {
        var state = DesktopIconPositionService.DecodeDesktopSortColumns(sortColumns);

        Assert.Equal(expectedMode, state.Mode);
        Assert.Equal(expectedDescending, state.Descending);
    }

    [Theory]
    [InlineData(0x00000000, false)]
    [InlineData(0x00000001, true)]
    [InlineData(0x00001000, false)]
    [InlineData(0x00001001, true)]
    public void DecodesExplorerAutoArrangeFolderFlag(uint flags, bool expected)
    {
        Assert.Equal(expected, DesktopIconPositionService.IsAutoArrangeEnabled(flags));
    }

    [Fact]
    public void ModifiedSortPlacesEmptyDatesBeforeRealDatesAscending()
    {
        var items = new[]
        {
            Item("Later", new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero)),
            Item("System", null),
            Item("Earlier", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero))
        };

        var ordered = DesktopItemSortService.Order(
                items,
                new DesktopIconSortState(DesktopIconSortMode.Modified, false))
            .Select(item => item.DisplayName)
            .ToArray();

        Assert.Equal(new[] { "System", "Earlier", "Later" }, ordered);
    }

    [Fact]
    public void ModifiedSortPlacesEmptyDatesAfterRealDatesDescending()
    {
        var items = new[]
        {
            Item("Later", new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero)),
            Item("System", null),
            Item("Earlier", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero))
        };

        var ordered = DesktopItemSortService.Order(
                items,
                new DesktopIconSortState(DesktopIconSortMode.Modified, true))
            .Select(item => item.DisplayName)
            .ToArray();

        Assert.Equal(new[] { "Later", "Earlier", "System" }, ordered);
    }

    private static DesktopItemRef Item(string name, DateTimeOffset? modifiedAt) => new()
    {
        Key = new DesktopItemKey("test", name),
        DisplayName = name,
        ParsingName = name,
        Kind = modifiedAt is null ? DesktopItemKind.Shell : DesktopItemKind.File,
        ModifiedAt = modifiedAt
    };

    private static byte[] CreatePropertyKey(Guid format, int propertyId) =>
        format.ToByteArray().Concat(BitConverter.GetBytes(propertyId)).ToArray();
}
