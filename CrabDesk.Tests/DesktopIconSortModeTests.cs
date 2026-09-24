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
            new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
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

    [Fact]
    public void ZeroedPersistedSortValueIsNotAuthoritative()
    {
        var isAuthoritative = DesktopIconPositionService.TryDecodePersistedDesktopSortMode(
            new byte[20],
            out var mode);

        Assert.False(isAuthoritative);
        Assert.Equal(DesktopIconSortMode.Name, mode);
    }

    [Fact]
    public void LateUnknownSnapshotCannotDowngradeCachedCreatedSort()
    {
        var cached = new DesktopIconViewState(
            new DesktopIconSortState(DesktopIconSortMode.Created, Descending: false),
            48,
            true,
            false,
            "shell:prop:System.DateCreated;",
            HasAuthoritativeSort: true,
            HasLiveSortColumns: true);
        var lateUnknown = new DesktopIconViewState(
            new DesktopIconSortState(DesktopIconSortMode.Name, Descending: false),
            48,
            true,
            false,
            "shell:|size:48|flags:0",
            HasAuthoritativeSort: false,
            HasLiveSortColumns: false);

        var merged = DesktopIconPositionService.PreserveLastKnownSortWhenSnapshotHasNoLiveSort(
            lateUnknown,
            cached);

        Assert.Equal(cached.Sort, merged.Sort);
        Assert.True(merged.HasAuthoritativeSort);
        Assert.False(merged.HasLiveSortColumns);
        Assert.Equal(lateUnknown.Signature, merged.Signature);
    }

    [Fact]
    public void ExplicitPersistedNamePropertyIsAuthoritative()
    {
        var value = CreatePropertyKey(
            new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
            10);

        var isAuthoritative = DesktopIconPositionService.TryDecodePersistedDesktopSortMode(
            value,
            out var mode);

        Assert.True(isAuthoritative);
        Assert.Equal(DesktopIconSortMode.Name, mode);
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

    [Fact]
    public void DecodesDateCreatedSort()
    {
        var value = CreatePropertyKey(
            new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
            15);

        Assert.Equal(DesktopIconSortMode.Created, DesktopIconPositionService.DecodeDesktopSortMode(value));
    }

    [Theory]
    [InlineData("prop:System.DateCreated;", false)]
    [InlineData("prop:-System.DateCreated;", true)]
    public void DecodesLiveDateCreatedSortColumns(string sortColumns, bool expectedDescending)
    {
        // Explorer's "创建时间" column must not fall through to the Name
        // default: that silently re-sorted the desktop A-Z whenever a newly
        // created file was dragged.
        var state = DesktopIconPositionService.DecodeDesktopSortColumns(sortColumns);

        Assert.Equal(DesktopIconSortMode.Created, state.Mode);
        Assert.Equal(expectedDescending, state.Descending);
    }

    [Fact]
    public void CreatedSortOrdersByCreationTimeNotModificationTime()
    {
        // The two orderings must differ, otherwise drag-then-rebuild would put
        // a freshly created file in the wrong place.
        var items = new[]
        {
            Created("NewestCreated", createdAt: new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero), modifiedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Created("OldestCreated", createdAt: new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), modifiedAt: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero))
        };

        var byCreated = DesktopItemSortService.Order(
                items,
                new DesktopIconSortState(DesktopIconSortMode.Created, false))
            .Select(item => item.DisplayName)
            .ToArray();
        var byModified = DesktopItemSortService.Order(
                items,
                new DesktopIconSortState(DesktopIconSortMode.Modified, false))
            .Select(item => item.DisplayName)
            .ToArray();

        Assert.Equal(new[] { "OldestCreated", "NewestCreated" }, byCreated);
        Assert.NotEqual(byCreated, byModified);
    }

    [Fact]
    public void EmptyLiveSortColumnsPreserveLastKnownSortInsteadOfDefaultingToName()
    {
        var lastKnown = new DesktopIconSortState(DesktopIconSortMode.Created, false);

        var state = DesktopIconPositionService.DecodeDesktopSortColumns(string.Empty, lastKnown);

        Assert.Equal(lastKnown, state);
    }

    [Fact]
    public void DecodesDateCreatedFromPersistedShellItemPropertyKey()
    {
        var value = CreatePropertyKey(
            new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
            15);

        Assert.Equal(DesktopIconSortMode.Created, DesktopIconPositionService.DecodeDesktopSortMode(value));
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

    private static DesktopItemRef Created(
        string name,
        DateTimeOffset? createdAt,
        DateTimeOffset? modifiedAt) => new()
    {
        Key = new DesktopItemKey("test", name),
        DisplayName = name,
        ParsingName = name,
        Kind = DesktopItemKind.File,
        CreatedAt = createdAt,
        ModifiedAt = modifiedAt
    };

    private static byte[] CreatePropertyKey(Guid format, int propertyId) =>
        format.ToByteArray().Concat(BitConverter.GetBytes(propertyId)).ToArray();
}
