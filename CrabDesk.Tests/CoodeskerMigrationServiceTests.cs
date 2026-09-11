using System.IO;
using CrabDesk.Core;
using Xunit;

namespace CrabDesk.Tests;

public class CoodeskerMigrationServiceTests
{
    private static readonly MonitorLayout Primary2560 = new()
    {
        Id = "MONITOR_PRIMARY",
        DeviceName = @"\\.\DISPLAY1",
        DpiScale = 1.0,
        Bounds = new LayoutRect(0, 0, 2560, 1440),
        WorkArea = new LayoutRect(0, 0, 2560, 1400),
        PixelBounds = new LayoutRect(0, 0, 2560, 1440),
        PixelWorkArea = new LayoutRect(0, 0, 2560, 1400),
        IsPrimary = true
    };

    [Fact]
    public void ParseCoodeskerLayoutJson_HandlesValidAndEmptyJson()
    {
        Assert.Empty(CoodeskerMigrationService.ParseCoodeskerLayoutJson(string.Empty));
        Assert.Empty(CoodeskerMigrationService.ParseCoodeskerLayoutJson("invalid json"));

        const string sampleJson = """
        [
            {
                "title": "专业",
                "pos_left": 100,
                "pos_top": 200,
                "pos_right": 500,
                "pos_bottom": 600,
                "min_state": 1,
                "apps": [
                    { "name": "CrabDesk", "file_path": "C:\\Users\\Administrator\\Desktop\\CrabDesk.lnk", "position": 0 },
                    { "name": "ZCode", "file_path": "C:\\Users\\Administrator\\Desktop\\ZCode.lnk", "position": 1 }
                ]
            }
        ]
        """;

        var boxes = CoodeskerMigrationService.ParseCoodeskerLayoutJson(sampleJson);
        Assert.Single(boxes);
        Assert.Equal("专业", boxes[0].Title);
        Assert.Equal(400, boxes[0].Width);
        Assert.Equal(400, boxes[0].Height);
        Assert.True(boxes[0].IsCollapsed);
        Assert.Equal(2, boxes[0].Items.Count);
        Assert.Equal("CrabDesk", boxes[0].Items[0].Name);
    }

    [Fact]
    public void CreateOverwriteState_MapsExplicitTabsAndPreservesChildItemAssignments()
    {
        var coodeskerJson = """
        [
            {
                "id": "100",
                "title": "开发工具",
                "pos_left": 800,
                "pos_top": 100,
                "pos_right": 1200,
                "pos_bottom": 500,
                "tabs": [
                    {
                        "id": "tab-1",
                        "title": "前端",
                        "items": [
                            { "name": "VSCode", "file_path": "C:\\Users\\Administrator\\Desktop\\VSCode.lnk", "position": 0 }
                        ]
                    },
                    {
                        "id": "tab-2",
                        "title": "后端",
                        "items": [
                            { "name": "Docker", "file_path": "C:\\Users\\Administrator\\Desktop\\Docker.lnk", "position": 0 }
                        ]
                    }
                ]
            }
        ]
        """;

        var boxes = CoodeskerMigrationService.ParseCoodeskerLayoutJson(coodeskerJson);
        var desktopItems = new List<DesktopItemRef>
        {
            CreateItem("C:\\Users\\Administrator\\Desktop\\VSCode.lnk", "VSCode"),
            CreateItem("C:\\Users\\Administrator\\Desktop\\Docker.lnk", "Docker"),
            CreateItem("C:\\Users\\Administrator\\Desktop\\Other.lnk", "Other")
        };

        var state = CoodeskerMigrationService.CreateOverwriteState(boxes, new CrabDeskState(), [Primary2560], desktopItems);

        Assert.Single(state.Boxes);
        var box = state.Boxes[0];
        Assert.Equal("开发工具", box.Title);
        Assert.Equal(2, box.ManualTabs.Count);

        var frontTab = box.ManualTabs[0];
        var backTab = box.ManualTabs[1];
        Assert.Equal("前端", frontTab.Title);
        Assert.Equal("后端", backTab.Title);

        var vscodeKey = "path:C:\\Users\\Administrator\\Desktop\\VSCode.lnk";
        var dockerKey = "path:C:\\Users\\Administrator\\Desktop\\Docker.lnk";

        Assert.Equal(box.Id, state.Assignments[vscodeKey]);
        Assert.Equal(box.Id, state.Assignments[dockerKey]);
        Assert.Equal(frontTab.Id, box.ItemTabAssignments[vscodeKey]);
        Assert.Equal(backTab.Id, box.ItemTabAssignments[dockerKey]);

        // Unrelated desktop items must not be assigned
        Assert.False(state.Assignments.ContainsKey("path:C:\\Users\\Administrator\\Desktop\\Other.lnk"));
    }

    [Fact]
    public void CreateOverwriteState_MapsSubBoxesToDistinctManualTabs()
    {
        var coodeskerJson = """
        [
            {
                "id": "parent-box",
                "title": "工作区",
                "pos_left": 200,
                "pos_top": 200,
                "pos_right": 600,
                "pos_bottom": 600,
                "sub_boxes": [
                    {
                        "id": "sub-1",
                        "title": "设计",
                        "apps": [
                            { "name": "Figma", "file_path": "C:\\Users\\Administrator\\Desktop\\Figma.lnk" }
                        ]
                    },
                    {
                        "id": "sub-2",
                        "title": "文档",
                        "apps": [
                            { "name": "Word", "file_path": "C:\\Users\\Administrator\\Desktop\\Word.lnk" }
                        ]
                    }
                ]
            }
        ]
        """;

        var boxes = CoodeskerMigrationService.ParseCoodeskerLayoutJson(coodeskerJson);
        var desktopItems = new List<DesktopItemRef>
        {
            CreateItem("C:\\Users\\Administrator\\Desktop\\Figma.lnk", "Figma"),
            CreateItem("C:\\Users\\Administrator\\Desktop\\Word.lnk", "Word")
        };

        var state = CoodeskerMigrationService.CreateOverwriteState(boxes, new CrabDeskState(), [Primary2560], desktopItems);

        Assert.Single(state.Boxes);
        var box = state.Boxes[0];
        Assert.Equal("工作区", box.Title);
        Assert.Equal(2, box.ManualTabs.Count);

        var designTab = box.ManualTabs.First(t => t.Title == "设计");
        var docTab = box.ManualTabs.First(t => t.Title == "文档");

        Assert.Equal(designTab.Id, box.ItemTabAssignments["path:C:\\Users\\Administrator\\Desktop\\Figma.lnk"]);
        Assert.Equal(docTab.Id, box.ItemTabAssignments["path:C:\\Users\\Administrator\\Desktop\\Word.lnk"]);
    }

    [Fact]
    public void CreateOverwriteState_RejectsIncompleteCoordinatesRatherThanGuessingZero()
    {
        var invalidBoxJson = """
        [
            {
                "title": "无坐标盒子",
                "pos_left": 0,
                "pos_top": 0,
                "pos_right": 0,
                "pos_bottom": 0
            }
        ]
        """;

        var boxes = CoodeskerMigrationService.ParseCoodeskerLayoutJson(invalidBoxJson);
        Assert.Throws<InvalidDataException>(() =>
            CoodeskerMigrationService.CreateOverwriteState(boxes, new CrabDeskState(), [Primary2560], []));
    }

    [Fact]
    public void CreateOverwriteState_RejectsSameItemInMultipleTabsOrBoxes()
    {
        var conflictingJson = """
        [
            {
                "title": "盒子A",
                "pos_left": 100, "pos_top": 100, "pos_right": 400, "pos_bottom": 400,
                "tabs": [
                    {
                        "id": "tab-a", "title": "A",
                        "items": [ { "name": "Same", "file_path": "C:\\Users\\Administrator\\Desktop\\Same.lnk" } ]
                    },
                    {
                        "id": "tab-b", "title": "B",
                        "items": [ { "name": "Same", "file_path": "C:\\Users\\Administrator\\Desktop\\Same.lnk" } ]
                    }
                ]
            }
        ]
        """;

        var boxes = CoodeskerMigrationService.ParseCoodeskerLayoutJson(conflictingJson);
        var items = new[] { CreateItem("C:\\Users\\Administrator\\Desktop\\Same.lnk", "Same") };

        Assert.Throws<InvalidDataException>(() =>
            CoodeskerMigrationService.CreateOverwriteState(boxes, new CrabDeskState(), [Primary2560], items));
    }

    private static DesktopItemRef CreateItem(string fullPath, string displayName) =>
        new()
        {
            Key = DesktopItemKey.Parse($"path:{fullPath}"),
            DisplayName = displayName,
            ParsingName = fullPath,
            FileSystemPath = fullPath,
            Kind = DesktopItemKind.File
        };
}
