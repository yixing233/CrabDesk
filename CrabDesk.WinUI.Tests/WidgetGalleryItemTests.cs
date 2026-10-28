using CrabDesk.Core;
using CrabDesk.WinUI.Controls;
using CrabDesk.WinUI.Views;
using Microsoft.UI.Xaml;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetGalleryItemTests
{
    [Fact]
    public void SelectionChangesDoNotInvalidateThePreview()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note };
        var appearance = new WidgetPreviewAppearance(new(), new());
        var item = new WidgetGalleryItem(widget, appearance, false, "便签");
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        item.IsSelected = true;
        Assert.Equal(Visibility.Visible, item.SelectionVisibility);
        item.IsSelected = false;
        Assert.Equal(Visibility.Collapsed, item.SelectionVisibility);
        Assert.Equal(0, item.PreviewRevision);
        Assert.DoesNotContain(nameof(item.Appearance), changes);
        Assert.DoesNotContain(nameof(item.PreviewRevision), changes);
        Assert.Same(widget, item.Widget);
    }

    [Fact]
    public void ExistingCardNotifiesTitleStatusAndContentUpdatesWithoutBeingReplaced()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note };
        var appearance = new WidgetPreviewAppearance(new(), new());
        var item = new WidgetGalleryItem(widget, appearance, true, "记录想法");
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        widget.Title = "新标题";
        item.Refresh(appearance, "已锁定", 1, refreshPreview: true);
        Assert.Equal("新标题", item.Title);
        Assert.Equal("已锁定", item.Caption);
        Assert.True(item.IsAdded);
        Assert.Equal("已添加", item.StatusLabel);
        Assert.Contains(nameof(item.Title), changes);
        Assert.Contains(nameof(item.AccessibleName), changes);
        Assert.Contains(nameof(item.ActionIcon), changes);
        Assert.Equal(1, item.PreviewRevision);
        changes.Clear();
        item.Refresh(appearance, "已锁定", 1, refreshPreview: false);
        Assert.Empty(changes);
        Assert.Equal(1, item.PreviewRevision);
    }
}
