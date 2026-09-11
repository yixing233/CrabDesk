namespace CrabDesk.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using CrabDesk.Core;
using Xunit;

public class CoodeskerMigrationServiceTests
{
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
                "items": [
                    { "name": "CrabDesk", "file_path": "C:\\Users\\Administrator\\Desktop\\CrabDesk.lnk", "position": 0 },
                    { "name": "ZCode", "file_path": "C:\\Users\\Administrator\\Desktop\\ZCode.lnk", "position": 1 }
                ]
            },
            {
                "title": "AI",
                "pos_left": 600,
                "pos_top": 200,
                "pos_right": 900,
                "pos_bottom": 500,
                "min_state": 0,
                "items": []
            }
        ]
        """;

        var boxes = CoodeskerMigrationService.ParseCoodeskerLayoutJson(sampleJson);
        Assert.Equal(2, boxes.Count);
        Assert.Equal("专业", boxes[0].Title);
        Assert.Equal(400, boxes[0].Width);
        Assert.Equal(400, boxes[0].Height);
        Assert.True(boxes[0].IsCollapsed);
        Assert.Equal(2, boxes[0].Items.Count);
        Assert.Equal("CrabDesk", boxes[0].Items[0].Name);

        Assert.Equal("AI", boxes[1].Title);
        Assert.False(boxes[1].IsCollapsed);
        Assert.Empty(boxes[1].Items);
    }

    [Fact]
    public void CreateOverwriteState_CompletelyReplacesExistingBoxesAndAssignments()
    {
        var oldBoxId = Guid.NewGuid();
        var oldItemKey = new DesktopItemKey("path", "C:\\OldFile.txt");
        var existingState = new CrabDeskState
        {
            Boxes = [
                new DesktopBox { Id = oldBoxId, Title = "旧盒子", Bounds = new LayoutRect(10, 10, 200, 200) }
            ],
            Assignments = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            {
                [oldItemKey.ToString()] = oldBoxId
            }
        };

        var coodeskerBoxes = new List<CoodeskerBoxModel>
        {
            new()
            {
                Title = "新工具",
                Left = 50,
                Top = 50,
                Right = 450,
                Bottom = 350,
                IsCollapsed = false,
                Items = [
                    new() { Name = "ToolA", FilePath = "C:\\Users\\Administrator\\Desktop\\ToolA.lnk" }
                ]
            }
        };

        var monitor = new MonitorLayout
        {
            Id = "MONITOR_1",
            DeviceName = "Primary Display",
            Bounds = new LayoutRect(0, 0, 1920, 1080),
            WorkArea = new LayoutRect(0, 0, 1920, 1040),
            PixelBounds = new LayoutRect(0, 0, 1920, 1080),
            PixelWorkArea = new LayoutRect(0, 0, 1920, 1040),
            IsPrimary = true
        };

        var desktopItems = new List<DesktopItemRef>
        {
            new()
            {
                Key = new DesktopItemKey("path", "C:\\Users\\Administrator\\Desktop\\ToolA.lnk"),
                DisplayName = "ToolA",
                ParsingName = "C:\\Users\\Administrator\\Desktop\\ToolA.lnk",
                FileSystemPath = "C:\\Users\\Administrator\\Desktop\\ToolA.lnk",
                Kind = DesktopItemKind.File
            },
            new()
            {
                Key = new DesktopItemKey("path", "C:\\Users\\Administrator\\Desktop\\Unrelated.txt"),
                DisplayName = "Unrelated",
                ParsingName = "C:\\Users\\Administrator\\Desktop\\Unrelated.txt",
                FileSystemPath = "C:\\Users\\Administrator\\Desktop\\Unrelated.txt",
                Kind = DesktopItemKind.File
            }
        };

        var nextState = CoodeskerMigrationService.CreateOverwriteState(
            coodeskerBoxes,
            existingState,
            [monitor],
            desktopItems);

        // Old box must be completely gone
        Assert.DoesNotContain(nextState.Boxes, b => b.Id == oldBoxId);
        Assert.DoesNotContain(nextState.Assignments, a => a.Value == oldBoxId);

        // New box must exist
        Assert.Single(nextState.Boxes);
        var importedBox = nextState.Boxes[0];
        Assert.Equal("新工具", importedBox.Title);
        Assert.Equal("MONITOR_1", importedBox.MonitorId);
        Assert.Equal(400, importedBox.Bounds.Width);
        Assert.Equal(300, importedBox.Bounds.Height);

        // Item must be assigned to new box
        var assignedKey = "path:C:\\Users\\Administrator\\Desktop\\ToolA.lnk";
        Assert.True(nextState.Assignments.ContainsKey(assignedKey));
        Assert.Equal(importedBox.Id, nextState.Assignments[assignedKey]);
        Assert.Contains(assignedKey, importedBox.ItemOrder);

        // Unrelated item should remain unassigned
        Assert.False(nextState.Assignments.ContainsKey("path:C:\\Users\\Administrator\\Desktop\\Unrelated.txt"));
    }
}
