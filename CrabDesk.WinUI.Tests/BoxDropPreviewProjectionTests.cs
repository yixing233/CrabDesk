using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class BoxDropPreviewProjectionTests
{
    [Fact]
    public void ManualProjectionAppendsDesktopItemsWithoutChangingExistingOrder()
    {
        var box = new DesktopBox
        {
            SortMode = BoxSortMode.Manual,
            ItemOrder = [Key("B"), Key("A")]
        };
        var items = new[]
        {
            Item("A", "Alpha"),
            Item("B", "Bravo"),
            Item("C", "Charlie")
        };
        var assignments = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("A")] = box.Id,
            [Key("B")] = box.Id
        };

        var projected = CrabDeskRuntime.ProjectItemsForBoxAfterAssigning(
            box,
            items,
            assignments,
            [Key("C")]);

        Assert.Equal([Key("B"), Key("A"), Key("C")], projected.Select(item => item.Key.ToString()));
        Assert.Equal([Key("B"), Key("A")], box.ItemOrder);
        Assert.DoesNotContain(Key("C"), assignments.Keys);
    }

    [Fact]
    public void NameProjectionUsesTheSameFinalOrderAsAssignment()
    {
        var box = new DesktopBox { SortMode = BoxSortMode.Name };
        var items = new[]
        {
            Item("B", "Bravo"),
            Item("A", "Alpha"),
            Item("C", "Charlie")
        };
        var assignments = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("B")] = box.Id,
            [Key("C")] = box.Id
        };

        var projected = CrabDeskRuntime.ProjectItemsForBoxAfterAssigning(
            box,
            items,
            assignments,
            [Key("A")]);

        Assert.Equal([Key("A"), Key("B"), Key("C")], projected.Select(item => item.Key.ToString()));
        Assert.Empty(box.ItemOrder);
        Assert.DoesNotContain(Key("A"), assignments.Keys);
    }

    [Fact]
    public void ManualProjectionCanBeInsertedBeforeTheHoveredItem()
    {
        var box = new DesktopBox
        {
            SortMode = BoxSortMode.Manual,
            ItemOrder = [Key("A"), Key("B")]
        };
        var items = new[]
        {
            Item("A", "Alpha"),
            Item("B", "Bravo"),
            Item("C", "Charlie")
        };
        var assignments = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("A")] = box.Id,
            [Key("B")] = box.Id
        };

        var projected = CrabDeskRuntime.ProjectItemsForBoxAfterAssigning(
            box,
            items,
            assignments,
            [Key("C")]);
        var incoming = projected.Where(item => item.Key.ToString() == Key("C")).ToArray();
        var remaining = projected.Where(item => item.Key.ToString() != Key("C")).ToList();
        remaining.InsertRange(1, incoming);

        Assert.Equal([Key("A"), Key("C"), Key("B")], remaining.Select(item => item.Key.ToString()));
        Assert.Equal([Key("A"), Key("B")], box.ItemOrder);
        Assert.DoesNotContain(Key("C"), assignments.Keys);
    }

    private static DesktopItemRef Item(string key, string name) => new()
    {
        Key = new DesktopItemKey("test", key),
        DisplayName = name,
        ParsingName = name,
        Kind = DesktopItemKind.File
    };

    private static string Key(string value) => $"test:{value}";
}
