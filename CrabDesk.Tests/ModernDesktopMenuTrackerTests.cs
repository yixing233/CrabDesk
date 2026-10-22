using System.Drawing;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class ModernDesktopMenuTrackerTests
{
    [Fact]
    public void CompositionMenuCanBeMatchedWhenWindowFromPointReturnsTheWindowUnderneath()
    {
        var item = new ModernDesktopRefreshItem(new IntPtr(42), new RectangleF(758, 587, 262, 40),
            UsesNativeHitTest: false);

        Assert.True(item.Contains(new IntPtr(99), 850, 608));
        Assert.False(item.Contains(new IntPtr(99), 850, 700));
    }

    [Theory]
    [InlineData("刷新", true)]
    [InlineData("刷新(&R)", true)]
    [InlineData("刷新(R)\tF5", true)]
    [InlineData("Refresh", true)]
    [InlineData("&Refresh", true)]
    [InlineData("Refresh\tF5", true)]
    [InlineData("Refresh (R)", true)]
    [InlineData("刷新“文档”(F5)", false)]
    [InlineData("Refresh folder", false)]
    [InlineData("刷新全部", false)]
    [InlineData("显示更多选项", false)]
    [InlineData("Sort by", false)]
    public void RefreshClassificationMatchesTheMenuCommand(string label, bool expected) =>
        Assert.Equal(expected, ModernDesktopMenuTracker.IsRefreshLabel(label));

    [Theory]
    [InlineData("Microsoft.UI.Content.PopupWindowSiteBridge", true)]
    [InlineData("Xaml_WindowedPopupClass", true)]
    [InlineData("XamlExplorerHostIslandWindow", true)]
    [InlineData("#32768", true)]
    [InlineData("CabinetWClass", false)]
    [InlineData("Progman", false)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    public void OnlyPopupHostsAreReadThroughAccessibility(string className, bool expected) =>
        Assert.Equal(expected, ModernDesktopMenuTracker.IsPopupWindowClass(className));

    [Theory]
    [InlineData(42, -100, 20, true)]
    [InlineData(42, -1, 59, true)]
    [InlineData(42, 0, 30, false)]
    [InlineData(42, -50, 60, false)]
    [InlineData(43, -50, 30, false)]
    public void CachedItemRequiresItsOwnWindowAndScreenBounds(int window, int x, int y, bool expected)
    {
        var item = new ModernDesktopRefreshItem(new IntPtr(42), new RectangleF(-100, 20, 100, 40));
        Assert.Equal(expected, item.Contains(new IntPtr(window), x, y));
    }

    [Fact]
    public void BusyAccessibilityProviderCannotBlockInputAndLateResultsAreDiscarded()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var finished = new ManualResetEventSlim(false);
        var apartment = ApartmentState.Unknown;
        using var tracker = new ModernDesktopMenuTracker(_ =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            finished.Set();
            return [new ModernDesktopRefreshItem(IntPtr.Zero, new RectangleF(0, 0, 100, 100))];
        });
        try
        {
            tracker.Track(IntPtr.Zero, Environment.TickCount64 + 10000);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(ApartmentState.MTA, apartment);
            Assert.False(tracker.IsRefreshAt(IntPtr.Zero, 10, 10));
            tracker.Stop();
        }
        finally { release.Set(); }
        Assert.True(finished.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(tracker.IsRefreshAt(IntPtr.Zero, 10, 10));
    }
}
