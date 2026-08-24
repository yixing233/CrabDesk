using System.Windows.Forms;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FluentContextMenuStripTests
{
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
}
