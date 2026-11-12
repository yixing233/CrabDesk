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

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, null, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void StatusFilterDistinguishesInstancesFromAvailableSamples(bool catalog, bool? added, bool expected)
    {
        var item = new WidgetGalleryItem(new() { Kind = DesktopWidgetKind.Note }, new(new(), new()), catalog, "便签");
        Assert.Equal(expected, item.Matches(null, added));
        Assert.Equal(catalog ? "未添加" : "已添加", item.StatusLabel);
        Assert.Equal(Visibility.Visible, item.StatusVisibility);
    }

    [Theory]
    [InlineData(" 工作记录 ", true)]
    [InlineData("便签", true)]
    [InlineData("灵感", true)]
    [InlineData("note", true)]
    [InlineData("已锁定", true)]
    [InlineData("  ", true)]
    [InlineData("不存在的组件", false)]
    public void SearchMatchesCustomTitleTypeDescriptionAndSettings(string query, bool expected)
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, Title = "工作记录" };
        var item = new WidgetGalleryItem(widget, new(new(), new()), false, "小 · 已锁定");
        Assert.Equal(expected, item.Matches(query, null));
        Assert.Equal(expected, item.Matches(query, true));
        Assert.False(item.Matches(query, false));
        Assert.Equal(0, item.PreviewRevision);
    }

    [Fact]
    public void SearchReflectsRenamedWidgetsWithoutReplacingTheirPreviews()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, Title = "旧名称" };
        var appearance = new WidgetPreviewAppearance(new(), new());
        var item = new WidgetGalleryItem(widget, appearance, false, "便签");
        widget.Title = "新名称";
        item.Refresh(appearance, "便签", 1, refreshPreview: false);
        Assert.True(item.Matches("新名称", true));
        Assert.False(item.Matches("旧名称", null));
        Assert.Equal(0, item.PreviewRevision);
        Assert.Same(widget, item.Widget);
    }
}
