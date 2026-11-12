using System.Xml.Linq;
using CrabDesk.Core;
using CrabDesk.WinUI.Controls;
using CrabDesk.WinUI.Views;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetColorIconUiTests
{
    [Fact]
    public void EveryGalleryKindUsesItsColorAssetWhileKeepingItsOwnTitle()
    {
        var assets = Path.Combine(SolutionDirectory(), "CrabDesk.Runtime", "Assets", "WidgetIcons");
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        {
            var widget = new DesktopWidget { Kind = kind, Title = "我的组件" };
            var item = new WidgetGalleryItem(widget, new WidgetPreviewAppearance(new(), new()), false, "预览说明");
            Assert.Equal(WidgetIconCatalog.GetName(kind), item.IconAssetName);
            Assert.True(File.Exists(Path.Combine(assets, item.IconAssetName + ".svg")), item.IconAssetName);
            Assert.Equal("我的组件", item.Title);
            Assert.Equal("预览说明", item.Caption);
        }
    }

    [Fact]
    public void GalleryAndEditorKeepPreviewAndActionLayoutWithAdjacentColorIcons()
    {
        var document = XDocument.Load(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace icons = "using:CrabDesk.WinUI.Controls";
        var tile = document.Descendants(ui + "DataTemplate").Single(element => (string?)element.Attribute(x + "Key") == "WidgetTileTemplate");
        var identity = tile.Descendants(icons + "WidgetColorIcon").Single();
        Assert.Equal("{Binding IconAssetName}", (string?)identity.Attribute("AssetName"));
        Assert.Equal("32", (string?)identity.Attribute("Width"));
        Assert.Equal("32", (string?)identity.Attribute("Height"));
        var preview = tile.Descendants(icons + "WidgetPreview").Single();
        Assert.Equal("{Binding IsCatalog}", (string?)preview.Attribute("IsCatalogPreview"));
        Assert.Equal("156", (string?)preview.Parent!.Attribute("Height"));
        Assert.Contains(tile.Descendants(ui + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding ActionLabel}");
        Assert.Contains(tile.Descendants(icons + "LucideIcon"), element => (string?)element.Attribute("Icon") == "{Binding ActionIcon}");
        var editor = document.Descendants(icons + "WidgetColorIcon").Single(element => (string?)element.Attribute(x + "Name") == "EditorIcon");
        Assert.Equal("24", (string?)editor.Attribute("Width"));
        Assert.Equal("24", (string?)editor.Attribute("Height"));
        Assert.Contains(editor.Parent!.Elements(ui + "TextBlock"), element => (string?)element.Attribute(x + "Name") == "EditorName");
    }

    [Fact]
    public void SamplePathsNeverResolveCopiesAndReferencesUseTheirOriginalPath()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.FileShelf };
        var item = new WidgetFileShelfItem { Mode = WidgetFileShelfMode.Copy, OriginalPath = @"Z:\sample\report.pdf" };
        var calls = 0;
        string Copy(DesktopWidget owner, WidgetFileShelfItem file)
        {
            Assert.Same(widget, owner);
            Assert.Same(item, file);
            calls++;
            return @"C:\config\FileShelf\copy.pdf";
        }
        Assert.Empty(FileShelfPreviewIcons.ResolveShellPath(widget, item, true, Copy));
        Assert.Equal(0, calls);
        item.Mode = WidgetFileShelfMode.Reference;
        Assert.Equal(item.OriginalPath, FileShelfPreviewIcons.ResolveShellPath(widget, item, false, Copy));
        Assert.Equal(0, calls);
        item.Mode = WidgetFileShelfMode.Copy;
        Assert.Equal(@"C:\config\FileShelf\copy.pdf", FileShelfPreviewIcons.ResolveShellPath(widget, item, false, Copy));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("项目计划.pdf", "file-pdf")]
    [InlineData("设计草图.PNG", "file-image")]
    [InlineData("素材包.zip", "file-archive")]
    [InlineData("记录.txt", "file-generic")]
    public void CatalogSamplesUseLocalFileArtwork(string name, string expected) =>
        Assert.Equal(expected, FileShelfPreviewIcons.GetSampleAssetName(name));

    [Fact]
    public async Task PendingAndCompletedIconWorkSurvivesUnrelatedRefreshes()
    {
        var cache = new FileShelfPreviewIconCache<object>(2);
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var lifetime = new CancellationTokenSource();
        Task<object?> Load(string path, CancellationToken token)
        {
            Assert.Equal(lifetime.Token, token);
            calls++;
            return completion.Task;
        }
        var first = cache.GetOrLoad(@"C:\file.pdf", Load, lifetime.Token);
        Assert.Same(first, cache.GetOrLoad(@"c:\FILE.pdf", Load, lifetime.Token));
        var icon = new object();
        completion.SetResult(icon);
        Assert.Same(icon, await cache.GetOrLoad(@"C:\file.pdf", Load, lifetime.Token));
        Assert.True(cache.TryGet(@"c:\FILE.pdf", out var retained));
        Assert.Same(icon, await retained);
        Assert.False(cache.TryGet(@"C:\missing.pdf", out _));
        Assert.Equal(1, calls);
        cache.Clear();
        _ = cache.GetOrLoad(@"C:\file.pdf", Load, lifetime.Token);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void IconCacheEvictsOldPathsAtItsCapacity()
    {
        var cache = new FileShelfPreviewIconCache<object>(2);
        var loaded = new List<string>();
        Task<object?> Load(string path, CancellationToken token)
        {
            loaded.Add(path);
            return Task.FromResult<object?>(new object());
        }
        cache.GetOrLoad("first.pdf", Load, default);
        var second = cache.GetOrLoad("second.pdf", Load, default);
        cache.GetOrLoad("third.pdf", Load, default);
        Assert.Same(second, cache.GetOrLoad("second.pdf", Load, default));
        cache.GetOrLoad("first.pdf", Load, default);
        Assert.Equal(new[] { "first.pdf", "second.pdf", "third.pdf", "first.pdf" }, loaded);
    }

    private static string SolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) return directory.FullName;
        throw new InvalidOperationException("CrabDesk.sln not found.");
    }
}
