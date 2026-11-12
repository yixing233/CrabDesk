using System.Xml.Linq;
using CrabDesk.Core;
using CrabDesk.WinUI.Views;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FileShelfSettingsTests
{
    [Fact]
    public void SettingsOfferLabeledModesAndFileActions()
    {
        var document = XDocument.Load(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var editor = document.Descendants(ui + "StackPanel").Single(element => (string?)element.Attribute(x + "Name") == "FileShelfEditor");
        var mode = editor.Descendants(ui + "RadioButtons").Single();
        Assert.Equal("新文件的收集方式", (string?)mode.Attribute("Header"));
        Assert.Equal("FileShelfModePicker_SelectionChanged", (string?)mode.Attribute("SelectionChanged"));
        Assert.Equal(new[] { "引用原文件", "复制到中转站" }, mode.Elements(x + "String").Select(element => element.Value));
        Assert.Contains(editor.Descendants(ui + "TextBlock"), element =>
            ((string?)element.Attribute("Text"))?.Contains("仅影响后续添加", StringComparison.Ordinal) is true);
        Assert.Equal("FileShelfAdd_Click", (string?)editor.Descendants(ui + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "FileShelfAddButton").Attribute("Click"));
        Assert.Equal("FileShelfClear_Click", (string?)editor.Descendants(ui + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "FileShelfClearButton").Attribute("Click"));
    }

    [Theory]
    [InlineData(WidgetFileShelfMode.Reference)]
    [InlineData(WidgetFileShelfMode.Copy)]
    public void SummaryUsesRecordedItemModesAndCachedAvailability(WidgetFileShelfMode newMode)
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.FileShelf, FileShelfMode = newMode,
            FileShelfItems =
            [
                new() { OriginalPath = @"Z:\unreachable\reference.pdf", Mode = WidgetFileShelfMode.Reference, IsAvailable = false },
                new() { OriginalPath = @"Z:\unreachable\copied.png", Mode = WidgetFileShelfMode.Copy, IsAvailable = true },
                new() { OriginalPath = @"Z:\unreachable\pending.zip", Mode = WidgetFileShelfMode.Reference, IsAvailable = null }
            ]
        };
        Assert.Equal("共 3 个文件 · 2 个引用 · 1 个副本\n1 个文件不可用", FileShelfSettingsPresentation.Summary(widget));
        Assert.Null(widget.FileShelfItems[2].IsAvailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemovalConfirmationCountsCopiesEvenAfterSwitchingToReferences(bool clear)
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.FileShelf, Title = "项目资料", FileShelfMode = WidgetFileShelfMode.Reference,
            FileShelfItems =
            [
                new() { Mode = WidgetFileShelfMode.Copy }, new() { Mode = WidgetFileShelfMode.Copy },
                new() { Mode = WidgetFileShelfMode.Reference }
            ]
        };
        var message = FileShelfSettingsPresentation.RemovalMessage(widget, clear);
        Assert.Contains("项目资料", message);
        Assert.Contains("3 个文件条目", message);
        Assert.Contains("同时删除中转站管理的 2 个副本", message);
        Assert.Contains("原文件保留", message);
        Assert.Contains(clear ? "将清空" : "将从桌面移除", message);
    }

    [Fact]
    public void ReferenceOnlyRemovalRetainsOriginalFiles()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.FileShelf, FileShelfMode = WidgetFileShelfMode.Copy,
            FileShelfItems = [new() { Mode = WidgetFileShelfMode.Reference }]
        };
        var message = FileShelfSettingsPresentation.RemovalMessage(widget, clear: true);
        Assert.Contains("原文件保留", message);
        Assert.DoesNotContain("删除", message);
    }

    [Fact]
    public void ModeHelpExplainsFileOwnershipAndMissingReferences()
    {
        Assert.Contains("原文件移动或删除", FileShelfSettingsPresentation.ModeExplanation(WidgetFileShelfMode.Reference));
        Assert.Contains("不可用", FileShelfSettingsPresentation.ModeExplanation(WidgetFileShelfMode.Reference));
        Assert.Contains("独立副本", FileShelfSettingsPresentation.ModeExplanation(WidgetFileShelfMode.Copy));
        Assert.Contains("原文件保留", FileShelfSettingsPresentation.ModeExplanation(WidgetFileShelfMode.Copy));
    }

    [Fact]
    public void GallerySamplesContainNamesAndRecordedModesWithoutProbingFiles()
    {
        var items = FileShelfSettingsPresentation.SampleItems();
        Assert.Equal(new[] { "项目计划.pdf", "设计草图.png", "素材包.zip" }, items.Select(item => item.Name));
        Assert.All(items, item => Assert.True(item.IsAvailable));
        Assert.Contains(items, item => item.Mode == WidgetFileShelfMode.Reference);
        Assert.Contains(items, item => item.Mode == WidgetFileShelfMode.Copy);
    }

    private static string SolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) return directory.FullName;
        throw new InvalidOperationException("CrabDesk.sln not found.");
    }
}
