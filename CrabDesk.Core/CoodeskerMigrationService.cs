namespace CrabDesk.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class CoodeskerItemModel
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int Position { get; set; }
}

public sealed class CoodeskerBoxModel
{
    public string Title { get; set; } = string.Empty;
    public int CategoryId { get; set; }

    [JsonPropertyName("left")]
    public double Left { get; set; }

    [JsonPropertyName("pos_left")]
    public double PosLeft { set => Left = value; }

    [JsonPropertyName("top")]
    public double Top { get; set; }

    [JsonPropertyName("pos_top")]
    public double PosTop { set => Top = value; }

    [JsonPropertyName("right")]
    public double Right { get; set; }

    [JsonPropertyName("pos_right")]
    public double PosRight { set => Right = value; }

    [JsonPropertyName("bottom")]
    public double Bottom { get; set; }

    [JsonPropertyName("pos_bottom")]
    public double PosBottom { set => Bottom = value; }

    public bool IsCollapsed { get; set; }

    [JsonPropertyName("min_state")]
    public int MinState { set => IsCollapsed = (value == 1); }

    public string Directory { get; set; } = string.Empty;
    public List<CoodeskerItemModel> Items { get; set; } = [];

    [JsonIgnore]
    public double Width => Math.Max(180, Right - Left);

    [JsonIgnore]
    public double Height => Math.Max(120, Bottom - Top);
}

public sealed record CoodeskerMigrationResult(
    bool Success,
    string Message,
    int ImportedBoxCount,
    int AssignedItemCount,
    IReadOnlyList<string> ImportedBoxTitles);

public static class CoodeskerMigrationService
{
    public static List<CoodeskerBoxModel> ParseCoodeskerLayoutJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            return JsonSerializer.Deserialize<List<CoodeskerBoxModel>>(json, options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static CrabDeskState CreateOverwriteState(
        IReadOnlyList<CoodeskerBoxModel> coodeskerBoxes,
        CrabDeskState currentState,
        IReadOnlyList<MonitorLayout> monitors,
        IEnumerable<DesktopItemRef> desktopItems)
    {
        var primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        var monitorId = primaryMonitor?.Id ?? string.Empty;
        var monitorBounds = primaryMonitor?.Bounds ?? new LayoutRect(0, 0, 1920, 1080);

        var nextState = new CrabDeskState
        {
            SchemaVersion = currentState.SchemaVersion,
            Settings = currentState.Settings,
            Organization = currentState.Organization,
            OrganizationRules = new List<OrganizationRule>(currentState.OrganizationRules),
            Boxes = [],
            Assignments = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase),
            DesktopIconPositions = new Dictionary<string, DesktopIconPlacement>(StringComparer.OrdinalIgnoreCase),
            DesktopIconLayout = new Dictionary<string, DesktopIconLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        };

        var availableItems = desktopItems.ToList();
        int stackOrder = 0;

        foreach (var cBox in coodeskerBoxes)
        {
            if (string.IsNullOrWhiteSpace(cBox.Title)) continue;

            var boxWidth = cBox.Width > 50 ? cBox.Width : 360;
            var boxHeight = cBox.Height > 50 ? cBox.Height : 280;
            var boxX = cBox.Left >= 0 ? cBox.Left : 100;
            var boxY = cBox.Top >= 0 ? cBox.Top : 100;

            var targetRect = new LayoutRect(boxX, boxY, boxWidth, boxHeight);
            if (primaryMonitor is not null)
            {
                targetRect = targetRect.Clamp(monitorBounds);
            }

            var newBox = new DesktopBox
            {
                Id = Guid.NewGuid(),
                Title = cBox.Title.Trim(),
                MonitorId = monitorId,
                StackOrder = stackOrder++,
                Bounds = targetRect,
                IsCollapsed = cBox.IsCollapsed,
                ExpandOnHover = cBox.IsCollapsed,
                ItemOrder = []
            };

            foreach (var cItem in cBox.Items)
            {
                var matched = availableItems.FirstOrDefault(item =>
                    (!string.IsNullOrEmpty(item.FileSystemPath) && !string.IsNullOrEmpty(cItem.FilePath) &&
                     string.Equals(item.FileSystemPath, cItem.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(item.DisplayName) && !string.IsNullOrEmpty(cItem.Name) &&
                     string.Equals(item.DisplayName, cItem.Name, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(item.ParsingName) && !string.IsNullOrEmpty(cItem.Name) &&
                     string.Equals(Path.GetFileNameWithoutExtension(item.ParsingName), cItem.Name, StringComparison.OrdinalIgnoreCase)));

                if (matched is not null)
                {
                    var itemKeyStr = matched.Key.ToString();
                    if (!nextState.Assignments.ContainsKey(itemKeyStr))
                    {
                        nextState.Assignments[itemKeyStr] = newBox.Id;
                        newBox.ItemOrder.Add(itemKeyStr);
                    }
                }
            }

            nextState.Boxes.Add(newBox);
        }

        return nextState;
    }
}
