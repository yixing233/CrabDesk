using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Forms = System.Windows.Forms;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FileShelfIconTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void IconItemsUseEachEntryEffectivePathAndStableIdentity() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var reference = fixture.Add("reference.pdf");
        var copy = fixture.Add("copy.png", WidgetFileShelfMode.Copy);
        fixture.Widget.FileShelfMode = WidgetFileShelfMode.Reference;
        var referenceIcon = fixture.IconItem(reference);
        var copyIcon = fixture.IconItem(copy);

        Assert.Equal(reference.OriginalPath, referenceIcon.ParsingName);
        Assert.Equal(fixture.Runtime.GetFileShelfPath(fixture.Widget, copy), copyIcon.ParsingName);
        Assert.NotEqual(copy.OriginalPath, copyIcon.ParsingName);
        Assert.Equal(copyIcon.ParsingName, copyIcon.FileSystemPath);
        Assert.Equal(copy.Name, copyIcon.DisplayName);
        Assert.Equal(DesktopItemKind.File, copyIcon.Kind);
        Assert.Equal(new DesktopItemKey("file-shelf", $"{fixture.Widget.Id:N}/{copy.Id:N}"), copyIcon.Key);
        fixture.Widget.FileShelfMode = WidgetFileShelfMode.Copy;
        Assert.Equal(referenceIcon.Key, fixture.IconItem(reference).Key);
        Assert.Equal(reference.OriginalPath, fixture.IconItem(reference).ParsingName);
        Assert.False(Directory.Exists(fixture.Root));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidMetadataAndUnconfiguredPreviewsRenderLocalFallback(bool missingProvider) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var item = fixture.Add("unavailable.zip", WidgetFileShelfMode.Copy);
        item.StoredRelativePath = "../outside.zip";
        item.IsAvailable = false;
        if (missingProvider)
        {
            Set(fixture.Runtime, "_iconProvider", null);
            Set(fixture.Runtime, "_fileShelfStore", null);
        }
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        Call(fixture.Surface, "DrawFileShelfItemIcon", graphics, fixture.Widget, item, new RectangleF(4, 4, 22, 22));

        Assert.Null(Call(fixture.Surface, "GetFileShelfIconItem", fixture.Widget, item));
        Assert.Empty(fixture.Cache.Keys.Cast<object>());
        Assert.Equal(0, fixture.PendingCount);
        Assert.Contains(Enumerable.Range(4, 22), x => Enumerable.Range(4, 22).Any(y => bitmap.GetPixel(x, y).A > 0));
        Assert.Equal("unavailable.zip", item.Name);
        Assert.False(item.IsAvailable);
    });

    [Fact]
    public void PaintingQueuesIconWorkWithoutQueryingShell() => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Add("document.pdf");
        var gate = (SemaphoreSlim)Get(fixture.Surface, "_iconLoadGate")!;
        for (var slot = 0; slot < 4; slot++) Assert.True(gate.Wait(0));
        using var bitmap = new Bitmap(420, 240);
        using var graphics = Graphics.FromImage(bitmap);

        Call(fixture.Surface, "DrawFileShelfWidget", graphics, fixture.Widget, Color.White, Color.Gray, Color.Blue);

        Assert.Equal(1, fixture.PendingCount);
        Assert.Equal(0, fixture.Provider.GetCacheStatistics().Misses);
        Assert.Empty(fixture.Cache.Keys.Cast<object>());
        Assert.False(Directory.Exists(fixture.Root));
    });

    [Theory]
    [InlineData(true, 255)]
    [InlineData(false, 115)]
    public void CachedIconsKeepTheirColorsAndFadeWhenUnavailable(bool available, int expectedAlpha) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var item = fixture.Add("document.pdf");
        item.IsAvailable = available;
        fixture.Cache.Add(fixture.Key(item), SolidIcon());
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);

        Call(fixture.Surface, "DrawFileShelfItemIcon", graphics, fixture.Widget, item, new RectangleF(4, 4, 22, 22));

        var pixel = bitmap.GetPixel(15, 15);
        Assert.InRange(pixel.A, expectedAlpha - 1, expectedAlpha + 1);
        Assert.InRange(pixel.R, 218, 222);
        Assert.InRange(pixel.G, 68, 72);
        Assert.InRange(pixel.B, 38, 42);
        Assert.Equal(0, fixture.PendingCount);
        Assert.Equal(0, fixture.Provider.GetCacheStatistics().Misses);
    });

    [Fact]
    public void BackgroundIconCompletionCachesBitmapAndRefreshesItsVisibleRow() => OnSta(() =>
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var item = fixture.Add("document.txt");
        File.WriteAllText(item.OriginalPath, "shell icon test");
        var key = fixture.Key(item);
        var refreshed = new List<RectangleF>();
        Set(fixture.Surface, "_isCompositedByIconSurface", true);
        fixture.Surface.SetIconLayerPartialRenderRequest(refreshed.Add);
        // BeginInvoke needs a window handle; no desktop attachment is required.
        _ = fixture.Surface.Handle;

        Assert.Null(Call(fixture.Surface, "GetIconBitmap", fixture.IconItem(item), 22f));
        WaitUntil(() => fixture.Cache.Contains(key));

        Assert.Equal(0, fixture.PendingCount);
        var cached = Assert.IsType<Bitmap>(fixture.Cache[key]);
        Assert.Same(cached, Call(fixture.Surface, "GetIconBitmap", fixture.IconItem(item), 22f));
        var layout = DesktopBoxForm.GetFileShelfLayout(fixture.Widget);
        Assert.Equal(new RectangleF(layout.List.X, layout.List.Y, layout.List.Width, layout.RowHeight), Assert.Single(refreshed));
    });

    [Fact]
    public void PruningRetainsShelfIconsAndDisposesRemovedOrSupersededPaths() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var reference = fixture.Add("reference.pdf");
        reference.IsAvailable = false;
        var copy = fixture.Add("copy.png", WidgetFileShelfMode.Copy);
        var removed = fixture.Add("removed.zip");
        var referenceKey = fixture.Key(reference);
        var referenceNeighborKey = fixture.Key(reference, 32f);
        var oldCopyKey = fixture.Key(copy);
        var removedKey = fixture.Key(removed);
        var staleCopy = SolidIcon();
        var removedIcon = SolidIcon();
        fixture.Cache.Add(referenceKey, SolidIcon());
        fixture.Cache.Add(referenceNeighborKey, SolidIcon());
        fixture.Cache.Add(oldCopyKey, staleCopy);
        fixture.Cache.Add(removedKey, removedIcon);
        copy.OriginalPath = Path.Combine(fixture.Root, "renamed.png");
        copy.StoredRelativePath = fixture.CopyRelativePath(copy);
        var currentCopyKey = fixture.Key(copy);
        fixture.Cache.Add(currentCopyKey, SolidIcon());
        fixture.Widget.FileShelfItems.Remove(removed);

        Call(fixture.Surface, "PruneIconCache");

        Assert.True(fixture.Cache.Contains(referenceKey));
        Assert.True(fixture.Cache.Contains(referenceNeighborKey));
        Assert.True(fixture.Cache.Contains(currentCopyKey));
        Assert.False(fixture.Cache.Contains(oldCopyKey));
        Assert.False(fixture.Cache.Contains(removedKey));
        Assert.ThrowsAny<Exception>(() => staleCopy.GetPixel(0, 0));
        Assert.ThrowsAny<Exception>(() => removedIcon.GetPixel(0, 0));
    });

    [Fact]
    public void InvalidationIgnoresScrolledOutItemsOtherMonitorsAndOldCopyPaths() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var first = fixture.Add("first.pdf");
        var copy = fixture.Add("copy.png", WidgetFileShelfMode.Copy);
        fixture.Add("third.txt"); fixture.Add("fourth.txt");
        var firstKey = fixture.Key(first);
        var staleCopyKey = fixture.Key(copy);
        copy.OriginalPath = Path.Combine(fixture.Root, "new-copy.png");
        copy.StoredRelativePath = fixture.CopyRelativePath(copy);
        ((Dictionary<Guid, float>)Get(fixture.Surface, "_widgetScroll")!)[fixture.Widget.Id] = 1;
        var refreshed = new List<RectangleF>();
        var fullRefreshes = 0;
        Set(fixture.Surface, "_isCompositedByIconSurface", true);
        fixture.Surface.SetIconLayerPartialRenderRequest(refreshed.Add);
        fixture.Surface.SetIconLayerRenderRequest(() => fullRefreshes++);

        Call(fixture.Surface, "InvalidateIcon", firstKey);
        Call(fixture.Surface, "InvalidateIcon", staleCopyKey);
        Assert.Empty(refreshed);
        Call(fixture.Surface, "InvalidateIcon", fixture.Key(copy));
        var layout = DesktopBoxForm.GetFileShelfLayout(fixture.Widget);
        Assert.Equal(new RectangleF(layout.List.X, layout.List.Y, layout.List.Width, layout.RowHeight), Assert.Single(refreshed));
        fixture.Widget.MonitorId = "another-monitor";
        Call(fixture.Surface, "InvalidateIcon", fixture.Key(copy));
        Assert.Single(refreshed);
        Assert.Equal(0, fullRefreshes);
    });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CrabDesk.FileShelfIcons", Guid.NewGuid().ToString("N"));
        public CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        public ShellIconProvider Provider { get; } = new();
        public DesktopWidget Widget { get; } = new() { Kind = DesktopWidgetKind.FileShelf, Size = DesktopWidgetSize.Large,
            MonitorId = "file-icon-test", Bounds = new(40, 40, 372, 180) };
        public DesktopBoxForm Surface { get; }
        public IDictionary Cache => (IDictionary)Get(Surface, "_iconCache")!;
        public int PendingCount => (int)Get(Surface, "_pendingIconLoads")!.GetType().GetProperty("Count")!.GetValue(Get(Surface, "_pendingIconLoads"))!;

        public Fixture()
        {
            var monitor = new MonitorLayout { Id = Widget.MonitorId, DeviceName = Widget.MonitorId, IsPrimary = true, DpiScale = 1,
                Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
                PixelBounds = new(0, 0, 1000, 700), PixelWorkArea = new(0, 0, 1000, 700) };
            Set(Runtime, "<State>k__BackingField", new CrabDeskState { Widgets = [Widget] });
            Set(Runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(Runtime, "<Monitors>k__BackingField", new[] { monitor });
            Set(Runtime, "_iconProvider", Provider);
            Set(Runtime, "_fileShelfStore", new WidgetFileShelfStore(Root));
            Surface = new DesktopBoxForm(Runtime, monitor);
        }

        public WidgetFileShelfItem Add(string name, WidgetFileShelfMode mode = WidgetFileShelfMode.Reference)
        {
            var item = new WidgetFileShelfItem { OriginalPath = Path.Combine(Root, name), Mode = mode, IsAvailable = true };
            if (mode == WidgetFileShelfMode.Copy) item.StoredRelativePath = CopyRelativePath(item);
            Widget.FileShelfItems.Add(item);
            return item;
        }

        public string CopyRelativePath(WidgetFileShelfItem item) => Path.Combine("FileShelf", Widget.Id.ToString("N"), item.Id.ToString("N"), item.Name);
        public DesktopItemRef IconItem(WidgetFileShelfItem item) => Assert.IsType<DesktopItemRef>(Call(Surface, "GetFileShelfIconItem", Widget, item));
        public object Key(WidgetFileShelfItem item, float size = 22f) => Call(Surface, "CreateIconBitmapKey", IconItem(item), size)!;
        public void Dispose()
        {
            Surface.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static Bitmap SolidIcon()
    {
        var icon = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(icon);
        graphics.Clear(Color.FromArgb(220, 70, 40));
        return icon;
    }

    private static object? Get(object target, string name) => target.GetType().GetField(name, Hidden)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Forms.Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert.True(condition(), "The background Shell icon load did not complete.");
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
