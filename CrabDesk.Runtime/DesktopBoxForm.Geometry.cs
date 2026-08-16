using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices.ComTypes;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm : Forms.Form
{

    private void RebuildGeometry()
    {
        _boxes.Clear();
        _items.Clear();
        _expandedItemHitBounds.Clear();
        foreach (var box in DesktopBoxes)
        {
            var titleBarHeight = (float)box.Appearance.TitleBarHeight;
            var height = (float)GetVisualBoxHeight(box);
            var isCollapsed = IsEffectivelyCollapsed(box);
            var bounds = new RectangleF((float)box.Bounds.X, (float)box.Bounds.Y, (float)box.Bounds.Width, (float)height);
            var manualTabs = isCollapsed ? [] : GetManualTabs(box);
            var categoryTabs = manualTabs.Count == 0 && !isCollapsed ? GetMappedFolderTabs(box) : [];
            var tabCount = categoryTabs.Count + manualTabs.Count;
            var tabBar = tabCount == 0
                ? RectangleF.Empty
                : new RectangleF(
                    bounds.X + 8,
                    bounds.Y + titleBarHeight,
                    Math.Max(0, bounds.Width - 16),
                    MappedFolderTabBarHeight);
            var bodyTop = tabBar.IsEmpty ? titleBarHeight + 8 : titleBarHeight + tabBar.Height + 8;
            var geometry = new BoxGeometry(
                box,
                isCollapsed,
                bounds,
                new RectangleF(bounds.X, bounds.Y, bounds.Width, titleBarHeight),
                tabBar,
                categoryTabs,
                GetActiveMappedFolderCategory(box.Id, categoryTabs),
                manualTabs,
                GetActiveManualTabId(box.Id, manualTabs),
                new RectangleF(bounds.X + 8, bounds.Y + bodyTop, bounds.Width - 16, Math.Max(0, bounds.Height - bodyTop - 8)),
                new RectangleF(bounds.Right - 62, bounds.Y + (titleBarHeight - 28) / 2, 26, 28),
                new RectangleF(bounds.Right - 32, bounds.Y + (titleBarHeight - 28) / 2, 26, 28),
                new RectangleF(bounds.Right - 18, bounds.Bottom - 18, 18, 18));
            _boxes.Add(geometry);
            if (!isCollapsed)
            {
                BuildItemGeometry(geometry);
            }
        }
        _geometryDirty = false;
    }

    private void EnsureGeometry()
    {
        if (_geometryDirty || _boxes.Count == 0)
        {
            RebuildGeometry();
        }
    }

    private BoxGeometry GetTransformGeometry(BoxGeometry geometry)
    {
        if (_movingBox is null || geometry.Box.Id != _movingBox.Id)
        {
            return geometry;
        }

        var offsetX = (float)(_movingBox.Bounds.X - _startBounds.X);
        var offsetY = (float)(_movingBox.Bounds.Y - _startBounds.Y);
        if (Math.Abs(offsetX) < float.Epsilon && Math.Abs(offsetY) < float.Epsilon)
        {
            return geometry;
        }

        return geometry with
        {
            Bounds = OffsetBounds(geometry.Bounds, offsetX, offsetY),
            Header = OffsetBounds(geometry.Header, offsetX, offsetY),
            TabBar = OffsetBounds(geometry.TabBar, offsetX, offsetY),
            Body = OffsetBounds(geometry.Body, offsetX, offsetY),
            AutoExpand = OffsetBounds(geometry.AutoExpand, offsetX, offsetY),
            Menu = OffsetBounds(geometry.Menu, offsetX, offsetY),
            Resize = OffsetBounds(geometry.Resize, offsetX, offsetY)
        };
    }

    private static RectangleF OffsetBounds(RectangleF bounds, float offsetX, float offsetY) => new(
        bounds.X + offsetX,
        bounds.Y + offsetY,
        bounds.Width,
        bounds.Height);

    private void BuildItemGeometry(BoxGeometry geometry)
    {
        if (_runtime.AreDesktopItemsHidden)
        {
            return;
        }
        var items = GetVisibleItemsForBox(geometry);
        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.CalculateVisible(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            items.Count,
            geometry.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconHorizontalSpacing, geometry.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconVerticalSpacing, geometry.Box.Appearance.IconSize),
            _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)));
        _scrollOffsets[GetItemViewKey(geometry)] = layout.ScrollOffset;
        foreach (var entry in layout.Items)
        {
            var itemBounds = entry.Bounds;
            var bounds = new RectangleF(
                (float)itemBounds.X,
                (float)itemBounds.Y,
                (float)itemBounds.Width,
                (float)itemBounds.Height);
            if (bounds.Bottom >= geometry.Body.Top && bounds.Top <= geometry.Body.Bottom)
            {
                _items.Add(new ItemGeometry(geometry.Box, items[entry.Index], bounds));
            }
        }
    }

    private IReadOnlyList<DesktopItemRef> GetVisibleItemsForBox(BoxGeometry geometry)
    {
        var items = GetCachedItemsForBox(geometry.Box.Id);
        if (geometry.ManualTabs.Count > 0)
        {
            return geometry.ActiveManualTabId is not { } tabId
                ? items
                : items.Where(item =>
                        geometry.Box.ItemTabAssignments.TryGetValue(item.Key.ToString(), out var assignedTabId) &&
                        assignedTabId == tabId)
                    .ToArray();
        }
        return geometry.ActiveMappedFolderCategory == MappedFolderItemCategory.All
            ? items
            : items.Where(item => MappedFolderItemCategoryClassifier.Matches(
                    geometry.ActiveMappedFolderCategory,
                    item))
                .ToArray();
    }

    private IReadOnlyList<MappedFolderTab> GetMappedFolderTabs(DesktopBox box)
    {
        if (box.MappedFolder?.EnableCategoryTabs != true)
        {
            return [];
        }

        var items = GetCachedItemsForBox(box.Id);
        var counts = items
            .GroupBy(MappedFolderItemCategoryClassifier.GetCategory)
            .ToDictionary(group => group.Key, group => group.Count());
        if (counts.Count < 2)
        {
            return [];
        }

        var tabs = new List<MappedFolderTab>
        {
            new(MappedFolderItemCategory.All, "全部", items.Count)
        };
        foreach (var category in MappedFolderTabOrder)
        {
            if (counts.TryGetValue(category, out var count))
            {
                tabs.Add(new MappedFolderTab(category, GetMappedFolderCategoryLabel(category), count));
            }
        }

        return tabs;
    }

    private IReadOnlyList<ManualBoxTab> GetManualTabs(DesktopBox box)
    {
        if (box.IsMappedFolder || box.ManualTabs.Count == 0)
        {
            return [];
        }

        var items = GetCachedItemsForBox(box.Id);
        var tabs = new List<ManualBoxTab>
        {
            new(null, "全部", items.Count)
        };
        foreach (var tab in box.ManualTabs)
        {
            var count = items.Count(item =>
                box.ItemTabAssignments.TryGetValue(item.Key.ToString(), out var assignedTabId) &&
                assignedTabId == tab.Id);
            tabs.Add(new ManualBoxTab(tab.Id, tab.Title, count));
        }
        return tabs;
    }

    private MappedFolderItemCategory GetActiveMappedFolderCategory(
        Guid boxId,
        IReadOnlyList<MappedFolderTab> tabs)
    {
        if (tabs.Count == 0)
        {
            _activeMappedFolderCategories[boxId] = MappedFolderItemCategory.All;
            return MappedFolderItemCategory.All;
        }

        var category = _activeMappedFolderCategories.GetValueOrDefault(boxId);
        if (tabs.Any(tab => tab.Category == category))
        {
            return category;
        }

        _activeMappedFolderCategories[boxId] = MappedFolderItemCategory.All;
        return MappedFolderItemCategory.All;
    }

    private Guid? GetActiveManualTabId(Guid boxId, IReadOnlyList<ManualBoxTab> tabs)
    {
        if (tabs.Count == 0)
        {
            _activeManualTabIds.Remove(boxId);
            return null;
        }

        if (_activeManualTabIds.TryGetValue(boxId, out var activeTabId) &&
            tabs.Any(tab => tab.Id == activeTabId))
        {
            return activeTabId;
        }

        _activeManualTabIds[boxId] = null;
        return null;
    }

    private static ItemViewKey GetItemViewKey(BoxGeometry geometry) =>
        new(
            geometry.Box.Id,
            geometry.ManualTabs.Count > 0
                ? $"manual:{geometry.ActiveManualTabId?.ToString("N") ?? "all"}"
                : $"mapped:{geometry.ActiveMappedFolderCategory}");

    private static string GetMappedFolderCategoryLabel(MappedFolderItemCategory category) => category switch
    {
        MappedFolderItemCategory.Folder => "目录",
        MappedFolderItemCategory.Image => "图片",
        MappedFolderItemCategory.Document => "文档",
        MappedFolderItemCategory.Archive => "压缩",
        MappedFolderItemCategory.Other => "其它",
        _ => "全部"
    };

    private ItemGeometry? GetItemAtPoint(BoxGeometry? box, PointF point)
    {
        // A scrolled item's layout rectangle can extend above the content
        // viewport. Painting clips that portion to Body; hit testing must use
        // the same boundary so header buttons always receive their clicks.
        if (box is null || !box.Body.Contains(point))
        {
            return null;
        }

        return _items.LastOrDefault(candidate =>
            candidate.Box.Id == box.Box.Id && GetItemHitBounds(candidate, box.Body).Contains(point));
    }

    private RectangleF GetItemHitBounds(ItemGeometry item, RectangleF contentBounds)
    {
        var key = (item.Box.Id, item.Item.Key.ToString());
        return _expandedItemHitBounds.TryGetValue(key, out var expandedBounds)
            ? RectangleF.Intersect(expandedBounds, contentBounds)
            : item.Bounds;
    }

    private bool TrySelectBoxTab(BoxGeometry box, PointF point)
    {
        var manualTab = GetManualBoxTabAtPoint(box, point);
        if (manualTab is not null)
        {
            if (manualTab.Id == box.ActiveManualTabId)
            {
                return true;
            }

            _activeManualTabIds[box.Box.Id] = manualTab.Id;
            _scrollOffsets.Remove(new ItemViewKey(
                box.Box.Id,
                $"manual:{manualTab.Id?.ToString("N") ?? "all"}"));
            ClearBoxItemSelection(box.Box.Id);
            _geometryDirty = true;
            InvalidateDip(box.TabBar);
            InvalidateDip(box.Body);
            return true;
        }

        var tab = GetMappedFolderTabAtPoint(box, point);
        if (tab is null)
        {
            return false;
        }

        if (tab.Category == box.ActiveMappedFolderCategory)
        {
            return true;
        }

        _activeMappedFolderCategories[box.Box.Id] = tab.Category;
        _scrollOffsets.Remove(new ItemViewKey(box.Box.Id, $"mapped:{tab.Category}"));
        ClearBoxItemSelection(box.Box.Id);
        _geometryDirty = true;
        InvalidateDip(box.TabBar);
        InvalidateDip(box.Body);
        return true;
    }

    private void ClearBoxItemSelection(Guid boxId)
    {
        var boxItemKeys = GetCachedItemsForBox(boxId)
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selection.RemoveWhere(boxItemKeys.Contains);
        _pressedItem = null;
        _pressedBoxId = null;
    }

    private static MappedFolderTab? GetMappedFolderTabAtPoint(BoxGeometry box, PointF point)
    {
        if (box.CategoryTabs.Count == 0 || !box.TabBar.Contains(point))
        {
            return null;
        }

        for (var index = 0; index < box.CategoryTabs.Count; index++)
        {
            if (GetBoxTabBounds(box, index, box.CategoryTabs.Count).Contains(point))
            {
                return box.CategoryTabs[index];
            }
        }

        return null;
    }

    private static ManualBoxTab? GetManualBoxTabAtPoint(BoxGeometry box, PointF point)
    {
        if (box.ManualTabs.Count == 0 || !box.TabBar.Contains(point))
        {
            return null;
        }

        for (var index = 0; index < box.ManualTabs.Count; index++)
        {
            if (GetBoxTabBounds(box, index, box.ManualTabs.Count).Contains(point))
            {
                return box.ManualTabs[index];
            }
        }

        return null;
    }

    private static RectangleF GetBoxTabBounds(BoxGeometry box, int index, int tabCount)
    {
        var tabWidth = box.TabBar.Width / tabCount;
        return new RectangleF(
            box.TabBar.X + tabWidth * index,
            box.TabBar.Y,
            index == tabCount - 1
                ? box.TabBar.Right - (box.TabBar.X + tabWidth * index)
                : tabWidth,
            box.TabBar.Height);
    }

    private static int? GetManualBoxTabIndex(BoxGeometry box, PointF point)
    {
        if (box.ManualTabs.Count == 0 || !box.TabBar.Contains(point))
        {
            return null;
        }

        for (var index = 0; index < box.ManualTabs.Count; index++)
        {
            if (GetBoxTabBounds(box, index, box.ManualTabs.Count).Contains(point))
            {
                return index;
            }
        }

        return null;
    }

    // Resolves the insertion point for an incoming group: the upper half of
    // an item inserts before it, the lower half inserts after it (and after
    // the last item means append at the end). Explorer-style label-edit
    // placement, so dropping "onto the last item" lands at the end.
    private string? ResolveInsertBeforeKey(
        BoxGeometry geometry,
        PointF pointer,
        IReadOnlySet<string>? incomingKeys = null)
    {
        var targetItem = _items.LastOrDefault(item =>
                item.Box.Id == geometry.Box.Id &&
                item.Bounds.Contains(pointer) &&
                (incomingKeys is null || !incomingKeys.Contains(item.Item.Key.ToString())))
            ?.Item;
        if (targetItem is null)
        {
            return null;
        }

        var bounds = _items.First(candidate =>
            string.Equals(candidate.Item.Key.ToString(), targetItem.Key.ToString(), StringComparison.OrdinalIgnoreCase)).Bounds;
        var insertAfter = pointer.Y >= bounds.Y + bounds.Height / 2;
        if (!insertAfter)
        {
            return targetItem.Key.ToString();
        }

        var orderedKeys = GetCachedItemsForBox(geometry.Box.Id)
            .Select(item => item.Key.ToString())
            .ToArray();
        var index = Array.FindIndex(
            orderedKeys,
            key => string.Equals(key, targetItem.Key.ToString(), StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < orderedKeys.Length
            ? orderedKeys[index + 1]
            : null;
    }

    private string? GetReorderBeforeKey(
        BoxGeometry geometry,
        PointF pointer) =>
        ResolveInsertBeforeKey(geometry, pointer);

    private IReadOnlyList<DesktopItemRef> GetProjectedDesktopAssignmentItems(
        BoxGeometry geometry,
        DropPreviewState preview)
    {
        var incomingKeys = preview.ItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectedItems = _runtime.GetItemsForBoxAfterAssigning(
            geometry.Box.Id,
            preview.ItemKeys);
        var beforeKey = ResolveInsertBeforeKey(geometry, preview.Pointer, incomingKeys);
        return beforeKey is null
            ? projectedItems
            : InsertProjectedItemsBefore(projectedItems, incomingKeys, beforeKey);
    }

}

