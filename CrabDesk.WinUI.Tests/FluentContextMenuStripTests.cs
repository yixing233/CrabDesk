using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FluentContextMenuStripTests
{
    [Fact]
    public void DisabledMenuItemCanStillRenderPointerHoverFeedback()
    {
        var method = typeof(FluentMenuRenderer).GetMethod(
            "ShouldRenderItemBackground",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.True((bool)method!.Invoke(null, [false, false, true])!);
    }

    [Fact]
    public void MenuItemHoverBackgroundKeepsSymmetricInsetsWithinItemBounds()
    {
        var bounds = FluentMenuRenderer.CalculateItemBackgroundBounds(
            new Size(145, 30),
            dpiScale: 1f);

        Assert.Equal(4, bounds.Left);
        Assert.Equal(4, 145 - bounds.Right);
        Assert.Equal(137, bounds.Width);
        Assert.Equal(28, bounds.Height);
    }

    [Fact]
    public void DisabledMenuItemUsesMutedForegroundColor()
    {
        var requested = Color.FromArgb(32, 36, 42);

        Assert.Equal(
            Color.FromArgb(145, 150, 158),
            FluentMenuRenderer.ResolveItemTextColor(false, requested, isDark: false));
        Assert.Equal(
            Color.FromArgb(125, 130, 138),
            FluentMenuRenderer.ResolveItemTextColor(false, requested, isDark: true));
        Assert.Equal(
            requested,
            FluentMenuRenderer.ResolveItemTextColor(true, requested, isDark: false));
    }

    [Fact]
    public void MenuItemsStretchToBalancedHorizontalMargins()
    {
        using var menu = new FluentContextMenuStrip { AutoSize = false, Size = new Size(160, 100) };
        using var item = new FluentToolStripMenuItem("自定义颜色...")
        {
            AutoSize = false,
            Size = new Size(150, 30),
            Margin = new Padding(1, 0, 1, 0)
        };
        menu.Items.Add(item);
        menu.PerformLayout();
        var leftGap = item.Bounds.Left + item.Margin.Left + 1;
        var rightGap = menu.ClientSize.Width - item.Bounds.Right;
        Assert.Equal(leftGap, rightGap);
    }

    [Fact]
    public void BoxMenuMetricsUsePreferredMonitorDpiBeforeHandleCreation()
    {
        using var menu = new FluentContextMenuStrip
        {
            PreferredDpiScale = 1.5f
        };

        Assert.Equal(1.5f, CrabDeskRuntime.GetMenuDpiScale(menu));
    }

    [Fact]
    public void RootMetricsDoNotConfigureClosedSubmenus()
    {
        using var root = new FluentContextMenuStrip();
        using var parent = new FluentToolStripMenuItem("parent");
        parent.DropDownItems.Add("child");
        root.Items.Add(parent);

        var configured = CrabDeskRuntime.GetMenuLevelsToConfigure(root, includeClosedSubmenus: false);

        Assert.Single(configured);
        Assert.Same(root, configured[0]);
    }

    [Fact]
    public void BoxAccentIsLiftedWhenItHasLowContrastAgainstBackground()
    {
        var background = Color.FromArgb(42, 70, 72);
        var accent = Color.FromArgb(45, 34, 255);

        var resolved = DesktopBoxForm.ResolveAccentColor(background, accent);

        Assert.NotEqual(accent, resolved);
        Assert.True(resolved.R > accent.R || resolved.G > accent.G);
    }

    [Fact]
    public void BoxAccentRemainsUnchangedWhenContrastIsAlreadySufficient()
    {
        var background = Color.FromArgb(42, 70, 72);
        var accent = Color.FromArgb(242, 184, 75);

        Assert.Equal(accent, DesktopBoxForm.ResolveAccentColor(background, accent));
    }
}
