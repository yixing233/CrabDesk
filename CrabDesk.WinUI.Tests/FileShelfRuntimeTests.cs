using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Forms = System.Windows.Forms;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FileShelfRuntimeTests
{
    [Fact]
    public async Task FloatingSwitchAndPositionChangesKeepTheSameFilesAndDesktopBounds()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("shared.txt", "source");
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        var item = Assert.Single(fixture.Widget.FileShelfItems);
        var desktopBounds = fixture.Widget.Bounds;
        fixture.Runtime.SetFileShelfFloating(fixture.Widget, true, false);
        fixture.Runtime.SaveFileShelfFloatingPosition(fixture.Widget, "monitor-two", new(10, 60, 380, 420), FileShelfDockEdge.Right);
        Assert.Equal(desktopBounds, fixture.Widget.Bounds);
        Assert.Same(item, Assert.Single(fixture.Widget.FileShelfItems));
        fixture.Runtime.SetFileShelfFloating(fixture.Widget, false, true);
        Assert.False(fixture.Widget.FileShelfFloating.Enabled);
        Assert.Equal(new LayoutRect(10, 60, 380, 420), fixture.Widget.FileShelfFloating.Bounds);
        Assert.Equal("source", await File.ReadAllTextAsync(source));
        fixture.Runtime.ResetFileShelfFloatingPosition(fixture.Widget);
        Assert.Null(fixture.Widget.FileShelfFloating.Bounds);
        Assert.Equal(FileShelfDockEdge.None, fixture.Widget.FileShelfFloating.DockEdge);
        Assert.Same(item, Assert.Single(fixture.Widget.FileShelfItems));
    }

    [Fact]
    public async Task ModeChangesApplyToNewFilesAndRecordedCopiesStayIndependent()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("document.txt", "original");
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        fixture.Runtime.SetFileShelfMode(fixture.Widget, WidgetFileShelfMode.Copy);
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        fixture.Runtime.SetFileShelfMode(fixture.Widget, WidgetFileShelfMode.Reference);
        var items = fixture.Widget.FileShelfItems;
        Assert.Equal(WidgetFileShelfMode.Reference, items[0].Mode);
        Assert.Equal(WidgetFileShelfMode.Copy, items[1].Mode);
        await File.WriteAllTextAsync(source, "changed");
        Assert.Equal("original", await File.ReadAllTextAsync(fixture.Runtime.GetFileShelfPath(fixture.Widget, items[1])));
        await fixture.Runtime.ClearFileShelfAsync(fixture.Widget);
        Assert.Empty(fixture.Widget.FileShelfItems);
        Assert.Equal("changed", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task DuplicateReferencesAreSkippedAndSameSourceCanHaveSeveralCopies()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("sample.txt", "sample");
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source, source]);
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        Assert.Single(fixture.Widget.FileShelfItems);
        fixture.Runtime.SetFileShelfMode(fixture.Widget, WidgetFileShelfMode.Copy);
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        Assert.Equal(3, fixture.Widget.FileShelfItems.Count);
        Assert.NotEqual(fixture.Widget.FileShelfItems[1].StoredRelativePath, fixture.Widget.FileShelfItems[2].StoredRelativePath);
    }

    [Fact]
    public async Task PartialFailuresPreserveSuccessfulImportsAndReleaseBusyState()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("valid.txt", "valid");
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [Path.Combine(fixture.Root, "missing.txt"), source]);
        Assert.Equal(source, Assert.Single(fixture.Widget.FileShelfItems).OriginalPath);
        Assert.False(fixture.Widget.FileShelfBusy);
        Assert.Contains("1 个失败", fixture.Widget.FileShelfStatus);
    }

    [Fact]
    public async Task CancelledImportDoesNotPublishAnEntryOrRemainBusy()
    {
        using var fixture = new Fixture();
        fixture.Runtime.SetFileShelfMode(fixture.Widget, WidgetFileShelfMode.Copy);
        var source = fixture.Write("sample.txt", "sample");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source], cancellation.Token);
        Assert.Empty(fixture.Widget.FileShelfItems);
        Assert.False(fixture.Widget.FileShelfBusy);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task AvailabilityRefreshChecksEffectivePathsAndRetainsMissingReferences()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("sample.txt", "sample");
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        fixture.Runtime.SetFileShelfMode(fixture.Widget, WidgetFileShelfMode.Copy);
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        File.Delete(source);
        await fixture.Runtime.RefreshFileShelfAvailabilityAsync(force: true);
        Assert.False(fixture.Widget.FileShelfItems[0].IsAvailable);
        Assert.True(fixture.Widget.FileShelfItems[1].IsAvailable);
        Assert.Equal(2, fixture.Widget.FileShelfItems.Count);
    }

    [Fact]
    public async Task FailedCopyRemovalRetainsEntryForRetryAndPreservesSource()
    {
        using var fixture = new Fixture();
        fixture.Runtime.SetFileShelfMode(fixture.Widget, WidgetFileShelfMode.Copy);
        var source = fixture.Write("locked.txt", "sample");
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        var item = Assert.Single(fixture.Widget.FileShelfItems);
        var copy = fixture.Runtime.GetFileShelfPath(fixture.Widget, item);
        using (var locked = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await fixture.Runtime.RemoveFileShelfItemAsync(fixture.Widget, item.Id);
            Assert.Same(item, Assert.Single(fixture.Widget.FileShelfItems));
            Assert.False(fixture.Widget.FileShelfBusy);
            Assert.Contains("未能移除", fixture.Widget.FileShelfStatus);
        }
        await fixture.Runtime.RemoveFileShelfItemAsync(fixture.Widget, item.Id);
        Assert.Empty(fixture.Widget.FileShelfItems);
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task RemovingWidgetCleansCopiesBeforeRemovingItsMetadata()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("copy.txt", "sample");
        fixture.Runtime.SetFileShelfMode(fixture.Widget, WidgetFileShelfMode.Copy);
        await fixture.Runtime.AddFileShelfFilesAsync(fixture.Widget, [source]);
        var copy = fixture.Runtime.GetFileShelfPath(fixture.Widget, Assert.Single(fixture.Widget.FileShelfItems));
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.Changed += (_, _) => { if (!fixture.Runtime.State.Widgets.Contains(fixture.Widget)) removed.TrySetResult(); };
        fixture.Runtime.RemoveWidget(fixture.Widget);
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task InvalidCopyMetadataIsRetainedAndReportedWithoutInterruptingClearOrRefresh()
    {
        using var fixture = new Fixture();
        fixture.Widget.FileShelfItems = [new() { OriginalPath = "C:\\sample.txt", Mode = WidgetFileShelfMode.Copy, StoredRelativePath = "../outside.txt" }];
        await fixture.Runtime.ClearFileShelfAsync(fixture.Widget);
        var item = Assert.Single(fixture.Widget.FileShelfItems);
        Assert.False(fixture.Widget.FileShelfBusy);
        Assert.Contains("未能移除", fixture.Widget.FileShelfStatus);
        await fixture.Runtime.RefreshFileShelfAvailabilityAsync(force: true);
        Assert.False(item.IsAvailable);
        item.IsAvailable = null;
        Assert.Empty(CrabDeskRuntime.FileShelfPaths(fixture.Widget, [item.Id], staged => fixture.Runtime.GetFileShelfPath(fixture.Widget, staged)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    public void ShelfDragToBoxesOrFolderTargetsAlwaysCopiesEvenOnTheSameVolume(int keys) => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        var data = new Forms.DataObject(); data.SetData(Forms.DataFormats.FileDrop, new[] { "C:\\sample.txt" });
        data.SetData(DesktopBoxForm.FileShelfDragFormat, fixture.Widget.Id.ToString("D"));
        var args = new Forms.DragEventArgs(data, keys, 0, 0, Forms.DragDropEffects.Copy, Forms.DragDropEffects.None);
        var box = new DesktopBox();
        Assert.Equal(BoxTransferEffect.CopyFiles, (BoxTransferEffect)Call(surface, "ResolveTransferEffect", args, box)!);
        Assert.False((bool)Call(surface, "ResolveFilesystemDropEffect", args, "C:\\target", new[] { "C:\\sample.txt" })!);
        Set(fixture.Runtime, "<Items>k__BackingField", new[] { new DesktopItemRef
        { Key = new("file", "sample"), DisplayName = "sample.txt", ParsingName = "C:\\sample.txt", FileSystemPath = "C:\\sample.txt", Kind = DesktopItemKind.File } });
        Assert.False((bool)Call(surface, "IsDesktopVirtualAssignment", args, box)!);
    });

    [Theory]
    [InlineData(WidgetFileShelfMode.Reference)]
    [InlineData(WidgetFileShelfMode.Copy)]
    public void DropPreviewAlwaysReportsCopyAndDoesNotAlterSourceState(WidgetFileShelfMode mode) => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Widget.FileShelfMode = mode;
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        var data = new Forms.DataObject(); data.SetData(Forms.DataFormats.FileDrop, new[] { "C:\\sample.txt" });
        var args = new Forms.DragEventArgs(data, 0, 0, 0, Forms.DragDropEffects.Move | Forms.DragDropEffects.Copy, Forms.DragDropEffects.None);
        Assert.True((bool)Call(surface, "TryHandleFileShelfDragOver", args, new PointF(60, 100))!);
        Assert.Equal(Forms.DragDropEffects.Copy, args.Effect);
        Assert.Empty(fixture.Widget.FileShelfItems);
        fixture.Widget.FileShelfBusy = true;
        Call(surface, "TryHandleFileShelfDragOver", args, new PointF(60, 100));
        Assert.Equal(Forms.DragDropEffects.None, args.Effect);
    });

    [Fact]
    public void OwnShelfDropIsRejectedAndUnknownLocationsContinueToNormalDropRouting() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        var data = new Forms.DataObject(); data.SetData(Forms.DataFormats.FileDrop, new[] { "C:\\sample.txt" });
        data.SetData(DesktopBoxForm.FileShelfDragFormat, fixture.Widget.Id.ToString("D"));
        var args = new Forms.DragEventArgs(data, 0, 0, 0, Forms.DragDropEffects.Copy, Forms.DragDropEffects.None);
        Assert.True((bool)Call(surface, "TryHandleFileShelfDragOver", args, new PointF(60, 100))!);
        Assert.Equal(Forms.DragDropEffects.None, args.Effect);
        Assert.False((bool)Call(surface, "TryHandleFileShelfDragOver", args, new PointF(800, 600))!);
    });

    [Fact]
    public void CtrlAndShiftSelectFilesAndDragPayloadUsesEachEntryEffectivePath() => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Widget.FileShelfItems = Enumerable.Range(0, 5).Select(index => new WidgetFileShelfItem
        { OriginalPath = $"C:\\source{index}.txt", Mode = index % 2 == 0 ? WidgetFileShelfMode.Reference : WidgetFileShelfMode.Copy }).ToList();
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        var items = fixture.Widget.FileShelfItems;
        surface.SelectFileShelfItem(fixture.Widget, items[1].Id, false, false);
        surface.SelectFileShelfItem(fixture.Widget, items[3].Id, false, true);
        var selected = (HashSet<Guid>)Call(surface, "FileShelfSelection", fixture.Widget)!;
        Assert.Equal(3, selected.Count);
        surface.SelectFileShelfItem(fixture.Widget, items[2].Id, true, false);
        Assert.Equal(2, selected.Count);
        items[3].IsAvailable = false;
        var paths = CrabDeskRuntime.FileShelfPaths(fixture.Widget, selected, item => "resolved/" + item.Name);
        Assert.Equal(new[] { "resolved/source1.txt" }, paths);
    });

    [Theory]
    [InlineData(DesktopWidgetSize.Medium, false, 1)]
    [InlineData(DesktopWidgetSize.Medium, true, 1.5)]
    [InlineData(DesktopWidgetSize.Large, false, 1.25)]
    [InlineData(DesktopWidgetSize.Large, true, 2)]
    public void CardsRenderBothModesAndMissingEntriesWithinTheirBounds(DesktopWidgetSize size, bool light, double scale) => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Widget.Size = size;
        var (width, height) = DesktopWidgets.Dimensions(size);
        fixture.Widget.Bounds = new(0, 0, width, height);
        fixture.Runtime.State.Settings.DefaultBoxAppearance.Background = light ? "#FFF2F4F7" : "#FF2A2D32";
        fixture.Runtime.State.Settings.Appearance.CornerRadius = 20;
        fixture.Widget.FileShelfItems =
        [
            new() { OriginalPath = "C:\\项目计划.pdf", Mode = WidgetFileShelfMode.Reference, IsAvailable = true },
            new() { OriginalPath = "C:\\设计草图.png", Mode = WidgetFileShelfMode.Copy, IsAvailable = true },
            new() { OriginalPath = "C:\\失效的素材包.zip", Mode = WidgetFileShelfMode.Reference, IsAvailable = false }
        ];
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        surface.SelectFileShelfItem(fixture.Widget, fixture.Widget.FileShelfItems[1].Id, false, false);
        using var bitmap = new Bitmap((int)(width * scale), (int)(height * scale));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(100, 122, 137));
        graphics.ScaleTransform((float)scale, (float)scale);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Call(surface, "DrawWidget", graphics, fixture.Widget);
        var directory = Path.Combine(SolutionDirectory(), "artifacts", "file-shelf-preview");
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, $"{size}-{(light ? "light" : "dark")}.png"));
        var layout = DesktopBoxForm.GetFileShelfLayout(fixture.Widget, fixture.Runtime.State.Settings.Appearance.CornerRadius);
        Assert.True(layout.List.Bottom <= layout.Footer.Top);
        Assert.True(layout.Add.Right <= width);
        Assert.Equal(3, layout.Rows);
        Assert.NotEqual(Color.FromArgb(100, 122, 137).ToArgb(), bitmap.GetPixel((int)(width / 2 * scale), (int)(height / 2 * scale)).ToArgb());
    });

    [Theory]
    [InlineData(DesktopWidgetSize.Medium, 1)]
    [InlineData(DesktopWidgetSize.Medium, 1.5)]
    [InlineData(DesktopWidgetSize.Large, 1)]
    [InlineData(DesktopWidgetSize.Large, 1.5)]
    public void EnlargedTextAndScrolledRowsKeepFileHitTargetsAligned(DesktopWidgetSize size, double fontScale) => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Widget.Size = size;
        fixture.Widget.FontScale = fontScale;
        var (width, height) = DesktopWidgets.Dimensions(size);
        fixture.Widget.Bounds = new(40, 40, width, height);
        fixture.Widget.FileShelfItems = Enumerable.Range(0, 8).Select(index => new WidgetFileShelfItem
            { OriginalPath = $"C:\\source{index}.txt" }).ToList();
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        var layout = DesktopBoxForm.GetFileShelfLayout(fixture.Widget);
        var first = new PointF(layout.List.X + 40, layout.List.Y + layout.RowHeight / 2);
        Assert.Equal(fixture.Widget.FileShelfItems[0], Call(surface, "FileShelfItemAt", fixture.Widget, first));
        Call(surface, "TryScrollWidget", first, -120);
        for (var row = 0; row < layout.Rows; row++)
            Assert.Equal(fixture.Widget.FileShelfItems[row + 1], Call(surface, "FileShelfItemAt", fixture.Widget,
                new PointF(first.X, layout.List.Y + (row + .5f) * layout.RowHeight)));
        Assert.Null(Call(surface, "FileShelfItemAt", fixture.Widget, new PointF(first.X, layout.Footer.Y + 2)));
        Assert.True(layout.List.Y + layout.Rows * layout.RowHeight <= layout.Footer.Y);
        for (var index = 0; index < 12; index++) Call(surface, "TryScrollWidget", first, -120);
        Assert.Equal(fixture.Widget.FileShelfItems[^1], Call(surface, "FileShelfItemAt", fixture.Widget,
            new PointF(first.X, layout.List.Y + (layout.Rows - .5f) * layout.RowHeight)));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(20)]
    public void AddAndEmptyDropAreaUseActionCursorAndBusyStateBlocksClicks(double cornerRadius) => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Runtime.State.Settings.Appearance.CornerRadius = cornerRadius;
        using var surface = new DesktopBoxForm(fixture.Runtime, fixture.Monitor);
        var layout = DesktopBoxForm.GetFileShelfLayout(fixture.Widget, cornerRadius);
        var add = new PointF(layout.Add.X + 12, layout.Add.Y + 12);
        var empty = new PointF(layout.List.X + 40, layout.List.Y + 20);
        Call(surface, "HandleWidgetMouseMove", add);
        Assert.Equal(Forms.Cursors.Hand, surface.Cursor);
        Call(surface, "HandleWidgetMouseMove", empty);
        Assert.Equal(Forms.Cursors.Hand, surface.Cursor);
        fixture.Widget.FileShelfBusy = true;
        Call(surface, "HandleWidgetMouseMove", add);
        Assert.Equal(Forms.Cursors.WaitCursor, surface.Cursor);
        Assert.True((bool)Call(surface, "HandleFileShelfClick", fixture.Widget, empty)!);
        Assert.Empty(fixture.Widget.FileShelfItems);
    });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CrabDesk.FileShelfRuntime", Guid.NewGuid().ToString("N"));
        public CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        public DesktopWidget Widget { get; } = new() { Kind = DesktopWidgetKind.FileShelf, Size = DesktopWidgetSize.Large,
            MonitorId = "file-shelf-test", Bounds = new(40, 40, 372, 180) };
        public MonitorLayout Monitor { get; } = new() { Id = "file-shelf-test", DeviceName = "file-shelf-test", IsPrimary = true,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700), PixelBounds = new(0, 0, 1000, 700), PixelWorkArea = new(0, 0, 1000, 700), DpiScale = 1 };
        private readonly RuntimeTimer _saveTimer = new(TimeSpan.FromHours(1), false, _ => { }, () => { });
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Set(Runtime, "_layoutStore", new JsonLayoutStore(Root));
            Set(Runtime, "<State>k__BackingField", new CrabDeskState { Widgets = [Widget] });
            Set(Runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(Runtime, "<Monitors>k__BackingField", new[] { Monitor });
            Set(Runtime, "_saveTimer", _saveTimer);
            Set(Runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
            Set(Runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        }
        public string Write(string name, string contents) { var path = Path.Combine(Root, name); File.WriteAllText(path, contents); return path; }
        public void Dispose()
        {
            _saveTimer.Dispose();
            var lifetime = (CancellationTokenSource?)typeof(CrabDeskRuntime).GetField("_fileShelfLifetime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Runtime);
            lifetime?.Cancel(); lifetime?.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static void Set(object instance, string name, object value) => typeof(CrabDeskRuntime).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static string SolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) return directory.FullName;
        throw new InvalidOperationException("CrabDesk.sln not found.");
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
