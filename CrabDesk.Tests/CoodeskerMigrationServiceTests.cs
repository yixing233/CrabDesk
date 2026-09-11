using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrabDesk.Core;
using Xunit;

namespace CrabDesk.Tests;

public class CoodeskerMigrationServiceTests
{
    [Fact]
    public void ParseCoodeskerLayoutJson_HandlesValidAndEmptyJson()
    {
        Assert.Empty(CoodeskerMigrationService.ParseCoodeskerLayoutJson(""));
        Assert.Empty(CoodeskerMigrationService.ParseCoodeskerLayoutJson("   "));
        Assert.Empty(CoodeskerMigrationService.ParseCoodeskerLayoutJson("invalid json"));

        var sampleJson = """
        [
            {
                "title": "专业",
                "left": 100,
                "top": 200,
                "right": 500,
                "bottom": 600,
                "min_state": 1,
                "apps": [
                    { "name": "CrabDesk", "file_path": "C:\\Desktop\\CrabDesk.lnk", "position": 0 },
                    { "name": "VSCode", "file_path": "C:\\Desktop\\VSCode.lnk", "position": 1 }
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
                "title": "图片",
                "pos_left": 800,
                "pos_top": 100,
                "pos_right": 1200,
                "pos_bottom": 500,
                "tabs": [
                    {
                        "id": "tab-1",
                        "title": "全部",
                        "apps": []
                    },
                    {
                        "id": "tab-2",
                        "title": "新标签",
                        "apps": [
                            { "name": "截图2", "file_path": "C:\\Desktop\\shot2.png", "position": 0 }
                        ]
                    }
                ],
                "apps": [
                    { "name": "截图1", "file_path": "C:\\Desktop\\shot1.png", "position": 0 }
                ]
            }
        ]
        """;

        var boxes = CoodeskerMigrationService.ParseCoodeskerLayoutJson(coodeskerJson);
        Assert.Single(boxes);

        var monitor = new MonitorLayout
        {
            Id = "MON_1", DeviceName = "Display 1",
            Bounds = new LayoutRect(0, 0, 2560, 1440),
            WorkArea = new LayoutRect(0, 0, 2560, 1400),
            PixelBounds = new LayoutRect(0, 0, 2560, 1440),
            PixelWorkArea = new LayoutRect(0, 0, 2560, 1400),
            IsPrimary = true
        };

        var items = new List<DesktopItemRef>
        {
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\shot1.png"), DisplayName = "截图1", ParsingName = "C:\\Desktop\\shot1.png", FileSystemPath = "C:\\Desktop\\shot1.png" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\shot2.png"), DisplayName = "截图2", ParsingName = "C:\\Desktop\\shot2.png", FileSystemPath = "C:\\Desktop\\shot2.png" }
        };

        var state = CoodeskerMigrationService.CreateOverwriteState(boxes, new CrabDeskState(), [monitor], items);

        Assert.Single(state.Boxes);
        var imgBox = state.Boxes[0];
        Assert.Equal("图片", imgBox.Title);

        // "全部" is built-in in CrabDesk, so only "新标签" should be in ManualTabs
        Assert.Single(imgBox.ManualTabs);
        var subTab = imgBox.ManualTabs[0];
        Assert.Equal("新标签", subTab.Title);

        // Both items should be in the box
        Assert.Equal(2, imgBox.ItemOrder.Count);
        Assert.Equal(imgBox.Id, state.Assignments["path:C:\\Desktop\\shot1.png"]);
        Assert.Equal(imgBox.Id, state.Assignments["path:C:\\Desktop\\shot2.png"]);

        // shot2 must be assigned specifically to the sub-tab!
        Assert.True(imgBox.ItemTabAssignments.TryGetValue("path:C:\\Desktop\\shot2.png", out var assignedTab));
        Assert.Equal(subTab.Id, assignedTab);

        // shot1 is in general list (so it displays when "全部" is selected)
        Assert.False(imgBox.ItemTabAssignments.ContainsKey("path:C:\\Desktop\\shot1.png"));
    }

    [Fact]
    public void RealCoodeskerLayoutResolvesAccurateBoundsAndClassifiedItems()
    {
        var boxes = new List<CoodeskerBoxModel>
        {
            new() { Title = "工具" },
            new() { Title = "0" },
            new() { Title = "文档" },
            new() { Title = "图片" },
            new() { Title = "浏览器" },
            new() { Title = "网络" },
            new() { Title = "AI" },
            new() { Title = "office" },
            new() { Title = "专业" }
        };

        var monitor = new MonitorLayout
        {
            Id = "PRIMARY", DeviceName = "Display 1",
            Bounds = new LayoutRect(0, 0, 2560, 1440),
            WorkArea = new LayoutRect(0, 0, 2560, 1400),
            PixelBounds = new LayoutRect(0, 0, 2560, 1440),
            PixelWorkArea = new LayoutRect(0, 0, 2560, 1400),
            IsPrimary = true
        };

        var items = new List<DesktopItemRef>
        {
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\screenshot.png"), DisplayName = "screenshot", ParsingName = "C:\\Desktop\\screenshot.png", FileSystemPath = "C:\\Desktop\\screenshot.png" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\report.docx"), DisplayName = "report", ParsingName = "C:\\Desktop\\report.docx", FileSystemPath = "C:\\Desktop\\report.docx" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\Edge.lnk"), DisplayName = "Edge", ParsingName = "C:\\Desktop\\Edge.lnk", FileSystemPath = "C:\\Desktop\\Edge.lnk" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\豆包.lnk"), DisplayName = "豆包", ParsingName = "C:\\Desktop\\豆包.lnk", FileSystemPath = "C:\\Desktop\\豆包.lnk" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\VSCode.lnk"), DisplayName = "VSCode", ParsingName = "C:\\Desktop\\VSCode.lnk", FileSystemPath = "C:\\Desktop\\VSCode.lnk" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\ToDesk.lnk"), DisplayName = "ToDesk", ParsingName = "C:\\Desktop\\ToDesk.lnk", FileSystemPath = "C:\\Desktop\\ToDesk.lnk" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\WPS.lnk"), DisplayName = "WPS", ParsingName = "C:\\Desktop\\WPS.lnk", FileSystemPath = "C:\\Desktop\\WPS.lnk" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\TinyBar.lnk"), DisplayName = "TinyBar", ParsingName = "C:\\Desktop\\TinyBar.lnk", FileSystemPath = "C:\\Desktop\\TinyBar.lnk" },
            new() { Key = DesktopItemKey.Parse("path:C:\\Desktop\\无畏契约.lnk"), DisplayName = "无畏契约", ParsingName = "C:\\Desktop\\无畏契约.lnk", FileSystemPath = "C:\\Desktop\\无畏契约.lnk" },
            new() { Key = DesktopItemKey.Parse("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"), DisplayName = "此电脑", ParsingName = "This PC", Kind = DesktopItemKind.Shell }
        };

        var state = CoodeskerMigrationService.CreateOverwriteState(boxes, new CrabDeskState(), [monitor], items);

        Assert.Equal(9, state.Boxes.Count);

        // Check coordinates
        var toolBox = state.Boxes.First(b => b.Title == "工具");
        var aiBox = state.Boxes.First(b => b.Title == "AI");
        Assert.Equal(780, toolBox.Bounds.X);
        Assert.Equal(20, toolBox.Bounds.Y);
        Assert.Equal(2030, aiBox.Bounds.X);
        Assert.Equal(460, aiBox.Bounds.Y);

        // Check multi-tab on 图片 box
        var imgBox = state.Boxes.First(b => b.Title == "图片");
        Assert.Single(imgBox.ManualTabs);
        Assert.Equal("新标签", imgBox.ManualTabs[0].Title);

        // Check icon assignments
        var docBox = state.Boxes.First(b => b.Title == "文档");
        var browserBox = state.Boxes.First(b => b.Title == "浏览器");
        var proBox = state.Boxes.First(b => b.Title == "专业");
        var netBox = state.Boxes.First(b => b.Title == "网络");
        var officeBox = state.Boxes.First(b => b.Title == "office");
        var zeroBox = state.Boxes.First(b => b.Title == "0");

        Assert.Equal(imgBox.Id, state.Assignments["path:C:\\Desktop\\screenshot.png"]);
        Assert.Equal(docBox.Id, state.Assignments["path:C:\\Desktop\\report.docx"]);
        Assert.Equal(browserBox.Id, state.Assignments["path:C:\\Desktop\\Edge.lnk"]);
        Assert.Equal(aiBox.Id, state.Assignments["path:C:\\Desktop\\豆包.lnk"]);
        Assert.Equal(proBox.Id, state.Assignments["path:C:\\Desktop\\VSCode.lnk"]);
        Assert.Equal(netBox.Id, state.Assignments["path:C:\\Desktop\\ToDesk.lnk"]);
        Assert.Equal(officeBox.Id, state.Assignments["path:C:\\Desktop\\WPS.lnk"]);
        Assert.Equal(toolBox.Id, state.Assignments["path:C:\\Desktop\\TinyBar.lnk"]);

        // Games are assigned into "0" box!
        Assert.Equal(zeroBox.Id, state.Assignments["path:C:\\Desktop\\无畏契约.lnk"]);
        Assert.Contains("path:C:\\Desktop\\无畏契约.lnk", zeroBox.ItemOrder);

        // System shell item must stay on desktop (unassigned)
        Assert.False(state.Assignments.ContainsKey("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
    }
}

