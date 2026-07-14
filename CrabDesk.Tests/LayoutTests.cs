using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class LayoutTests
{
    [Fact]
    public void DefaultStateStartsWithoutDemoBoxes()
    {
        var state = JsonLayoutStore.CreateDefaultState();

        Assert.Empty(state.Boxes);
        Assert.True(state.Settings.DesktopBehavior.ShowDesktopContextMenu);
    }

    [Fact]
    public void AutomaticBoxLayoutAvoidsOverlapAndStaysVisible()
    {
        var workArea = new LayoutRect(0, 0, 1366, 728);
        var sizes = Enumerable.Range(0, 5)
            .Select(_ => new LayoutRect(0, 0, 360, 260))
            .ToArray();

        var arranged = BoxLayoutPlanner.Arrange(workArea, sizes);

        Assert.Equal(5, arranged.Count);
        Assert.True(arranged[0].X > workArea.Width / 2);
        Assert.All(arranged, box => Assert.Equal(box, box.Clamp(workArea, 260, 160)));
        for (var first = 0; first < arranged.Count; first++)
        {
            for (var second = first + 1; second < arranged.Count; second++)
            {
                Assert.False(arranged[first].Intersects(arranged[second]));
            }
        }
    }

    [Fact]
    public void AutomaticBoxLayoutAvoidsManualBoxes()
    {
        var occupied = new LayoutRect(24, 24, 420, 310);

        var arranged = BoxLayoutPlanner.Arrange(
            new LayoutRect(0, 0, 1920, 1040),
            [new LayoutRect(0, 0, 360, 280)],
            [occupied]);

        Assert.False(arranged[0].Intersects(occupied));
    }

    [Fact]
    public void ClampKeepsBoxInsideMonitor()
    {
        var monitor = new LayoutRect(0, 0, 1920, 1080);
        var result = new LayoutRect(1800, 1000, 500, 400).Clamp(monitor);

        Assert.Equal(1420, result.X);
        Assert.Equal(680, result.Y);
        Assert.Equal(500, result.Width);
        Assert.Equal(400, result.Height);
    }

    [Fact]
    public void MissingMonitorMovesBoxToPrimary()
    {
        var state = JsonLayoutStore.CreateDefaultState("removed-monitor");
        state.Boxes.Add(new DesktopBox { MonitorId = "removed-monitor" });
        var monitors = new[]
        {
            Monitor("primary", true, 1920, 1040),
            Monitor("secondary", false, 1280, 984)
        };

        LayoutCoordinator.NormalizeForMonitors(state, monitors);

        Assert.All(state.Boxes, box => Assert.Equal("primary", box.MonitorId));
    }

    [Fact]
    public void ItemsRemainUnassignedWhenNoBoxesExist()
    {
        var state = JsonLayoutStore.CreateDefaultState();
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("shell", "recycle-bin"),
            DisplayName = "回收站",
            ParsingName = "shell:::recycle-bin",
            Kind = DesktopItemKind.Shell
        };

        var boxId = LayoutCoordinator.ResolveBox(state, item);

        Assert.Equal(Guid.Empty, boxId);
        Assert.Empty(state.Assignments);
    }

    [Fact]
    public void DesktopItemKeyRoundTrips()
    {
        var key = new DesktopItemKey("file", "A:B:C");
        Assert.Equal(key, DesktopItemKey.Parse(key.ToString()));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void MonitorPixelAndDipCoordinatesRoundTrip(double scale)
    {
        var pixels = new LayoutRect(-2560, 180, 2560, 1440);

        var dips = MonitorCoordinateConverter.PixelsToDips(pixels, scale);
        var roundTrip = MonitorCoordinateConverter.DipsToPixels(dips, scale);

        Assert.Equal(pixels.X, roundTrip.X, 6);
        Assert.Equal(pixels.Y, roundTrip.Y, 6);
        Assert.Equal(pixels.Width, roundTrip.Width, 6);
        Assert.Equal(pixels.Height, roundTrip.Height, 6);
    }

    [Fact]
    public void MixedDpiMonitorsClampBoxesInEachMonitorsDipWorkArea()
    {
        var primary = Monitor("primary", true, 1920, 1040);
        var highDpi = new MonitorLayout
        {
            Id = "high-dpi",
            DeviceName = "high-dpi",
            PixelBounds = new LayoutRect(1920, 0, 3840, 2160),
            PixelWorkArea = new LayoutRect(1920, 0, 3840, 2080),
            Bounds = new LayoutRect(960, 0, 1920, 1080),
            WorkArea = new LayoutRect(960, 0, 1920, 1040),
            DpiScale = 2,
            IsPrimary = false
        };
        var state = JsonLayoutStore.CreateDefaultState("primary");
        state.Boxes.Add(new DesktopBox
        {
            Title = "4K",
            MonitorId = highDpi.Id,
            Bounds = new LayoutRect(1750, 980, 420, 300)
        });

        LayoutCoordinator.NormalizeForMonitors(state, [primary, highDpi]);

        var box = state.Boxes.Single(candidate => candidate.Title == "4K");
        Assert.Equal(1500, box.Bounds.X);
        Assert.Equal(740, box.Bounds.Y);
        Assert.Equal(420, box.Bounds.Width);
        Assert.Equal(300, box.Bounds.Height);
    }

    [Fact]
    public void RemovedHighDpiMonitorMigratesAndClampsBoxToPrimary()
    {
        var state = JsonLayoutStore.CreateDefaultState("removed-4k");
        state.Boxes.Add(new DesktopBox { MonitorId = "removed-4k" });
        state.Boxes[0].Bounds = new LayoutRect(1600, 900, 600, 400);

        LayoutCoordinator.NormalizeForMonitors(state, [Monitor("primary", true, 1366, 728)]);

        Assert.Equal("primary", state.Boxes[0].MonitorId);
        Assert.Equal(new LayoutRect(766, 328, 600, 400), state.Boxes[0].Bounds);
    }

    [Fact]
    public void BoxCanMoveAcrossNegativeCoordinateMixedDpiMonitor()
    {
        var primary = Monitor("primary", true, 1920, 1040);
        var secondary = new MonitorLayout
        {
            Id = "left-150",
            DeviceName = "left-150",
            PixelBounds = new LayoutRect(-2560, 0, 2560, 1440),
            PixelWorkArea = new LayoutRect(-2560, 0, 2560, 1380),
            Bounds = new LayoutRect(-1706.666, 0, 1706.666, 960),
            WorkArea = new LayoutRect(-1706.666, 0, 1706.666, 920),
            DpiScale = 1.5,
            IsPrimary = false
        };
        var box = new DesktopBox
        {
            MonitorId = primary.Id,
            Bounds = new LayoutRect(120, 80, 420, 300)
        };

        var moved = LayoutCoordinator.TryMoveBoxToMonitor(
            box,
            [primary, secondary],
            -1280,
            750,
            40,
            20);

        Assert.True(moved);
        Assert.Equal(secondary.Id, box.MonitorId);
        Assert.Equal(813.333, box.Bounds.X, 3);
        Assert.Equal(480, box.Bounds.Y, 3);
    }

    [Fact]
    public void CrossMonitorMoveClampsBoxInsideTargetWorkArea()
    {
        var primary = Monitor("primary", true, 1920, 1040);
        var target = new MonitorLayout
        {
            Id = "right-200",
            DeviceName = "right-200",
            PixelBounds = new LayoutRect(1920, 0, 3840, 2160),
            PixelWorkArea = new LayoutRect(1920, 0, 3840, 2080),
            Bounds = new LayoutRect(960, 0, 1920, 1080),
            WorkArea = new LayoutRect(960, 0, 1920, 1040),
            DpiScale = 2,
            IsPrimary = false
        };
        var box = new DesktopBox
        {
            MonitorId = primary.Id,
            Bounds = new LayoutRect(100, 100, 420, 300)
        };

        var moved = LayoutCoordinator.TryMoveBoxToMonitor(
            box,
            [primary, target],
            5759,
            2159,
            10,
            10);

        Assert.True(moved);
        Assert.Equal(target.Id, box.MonitorId);
        Assert.Equal(new LayoutRect(1500, 740, 420, 300), box.Bounds);
    }

    [Fact]
    public void BoxDoesNotMoveWhenCursorIsOutsideAnotherMonitor()
    {
        var box = new DesktopBox
        {
            MonitorId = "primary",
            Bounds = new LayoutRect(100, 100, 420, 300)
        };

        var moved = LayoutCoordinator.TryMoveBoxToMonitor(
            box,
            [Monitor("primary", true, 1920, 1040)],
            2500,
            1200,
            20,
            20);

        Assert.False(moved);
        Assert.Equal("primary", box.MonitorId);
        Assert.Equal(new LayoutRect(100, 100, 420, 300), box.Bounds);
    }

    [Fact]
    public void ResetLayoutRebuildsBoxesAndDisablesRulesTargetingRemovedBoxes()
    {
        var state = JsonLayoutStore.CreateDefaultState("old-monitor");
        state.Boxes.Add(new DesktopBox { Title = "旧盒子", MonitorId = "old-monitor" });
        var oldTarget = state.Boxes[0].Id;
        state.Settings.ThemeMode = ApplicationThemeMode.Dark;
        state.Assignments["path:test"] = oldTarget;
        state.Boxes.Add(new DesktopBox
        {
            Title = "映射",
            MonitorId = "old-monitor",
            MappedFolder = new MappedFolderSettings { Path = "C:\\Mapped" }
        });
        state.OrganizationRules.Add(new OrganizationRule
        {
            Title = "旧目标",
            Action = OrganizationRuleAction.AssignToBox,
            TargetBoxId = oldTarget
        });

        var disabled = LayoutCoordinator.ResetLayout(state, "new-primary");

        Assert.Equal(1, disabled);
        Assert.Empty(state.Boxes);
        Assert.Empty(state.Assignments);
        Assert.False(state.OrganizationRules[0].Enabled);
        Assert.Null(state.OrganizationRules[0].TargetBoxId);
        Assert.Equal(ApplicationThemeMode.Dark, state.Settings.ThemeMode);
    }

    [Fact]
    public void ManualReorderPreservesSelectedRelativeOrderAndRemovesStaleKeys()
    {
        var box = new DesktopBox
        {
            SortMode = BoxSortMode.Manual,
            ItemOrder = ["C", "stale", "B", "D"]
        };

        var changed = LayoutCoordinator.ReorderItems(
            box,
            ["A", "B", "C", "D"],
            ["D", "B"],
            "C");

        Assert.True(changed);
        Assert.Equal(["B", "D", "C", "A"], box.ItemOrder);
    }

    [Fact]
    public void ManualReorderIgnoresDropOntoSelectionAndNonManualSort()
    {
        var manual = new DesktopBox
        {
            SortMode = BoxSortMode.Manual,
            ItemOrder = ["A", "B", "C"]
        };
        var named = new DesktopBox
        {
            SortMode = BoxSortMode.Name,
            ItemOrder = ["A", "B", "C"]
        };

        Assert.False(LayoutCoordinator.ReorderItems(manual, ["A", "B", "C"], ["B"], "B"));
        Assert.False(LayoutCoordinator.ReorderItems(named, ["A", "B", "C"], ["B"], "A"));
        Assert.Equal(["A", "B", "C"], manual.ItemOrder);
        Assert.Equal(["A", "B", "C"], named.ItemOrder);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(0.5, 83)]
    [InlineData(1, 52)]
    public void CollapseAnimationUsesClampedEaseOutCubic(double progress, double expected)
    {
        Assert.Equal(expected, AnimationMath.Interpolate(300, 52, progress), 0);
    }

    private static MonitorLayout Monitor(string id, bool primary, double width, double height) => new()
    {
        Id = id,
        DeviceName = id,
        Bounds = new LayoutRect(0, 0, width, height),
        WorkArea = new LayoutRect(0, 0, width, height),
        PixelBounds = new LayoutRect(0, 0, width, height),
        PixelWorkArea = new LayoutRect(0, 0, width, height),
        DpiScale = 1,
        IsPrimary = primary
    };
}
