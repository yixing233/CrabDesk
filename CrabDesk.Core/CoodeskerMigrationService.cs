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

public sealed class CoodeskerTabModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "新标签";

    [JsonPropertyName("name")]
    public string Name { set => Title = value; }

    [JsonPropertyName("items")]
    public List<CoodeskerItemModel> Items { get; set; } = [];
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
    public List<CoodeskerTabModel> Tabs { get; set; } = [];
    public List<CoodeskerBoxModel> SubBoxes { get; set; } = [];
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

            var targetRect = ResolveBoxBounds(cBox, stackOrder, monitorBounds);





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

            var tabModels = new List<CoodeskerTabModel>();
            if (cBox.Tabs.Count > 0)
            {
                tabModels.AddRange(cBox.Tabs);
            }
            else if (cBox.SubBoxes.Count > 0)
            {
                foreach (var sb in cBox.SubBoxes)
                {
                    tabModels.Add(new CoodeskerTabModel
                    {
                        Title = string.IsNullOrWhiteSpace(sb.Title) ? "新标签" : sb.Title,
                        Items = sb.Items
                    });
                }
            }
            else if (cBox.Title is "图片" or "组合盒子")
            {
                tabModels.Add(new CoodeskerTabModel { Title = "全部" });
                tabModels.Add(new CoodeskerTabModel { Title = "新标签" });
            }

            foreach (var tm in tabModels)
            {
                var dTab = new DesktopBoxTab
                {
                    Id = Guid.NewGuid(),
                    Title = tm.Title
                };
                newBox.ManualTabs.Add(dTab);

                foreach (var tabItem in tm.Items)
                {
                    var matched = availableItems.FirstOrDefault(item =>
                        (!string.IsNullOrEmpty(item.FileSystemPath) && !string.IsNullOrEmpty(tabItem.FilePath) &&
                         string.Equals(item.FileSystemPath, tabItem.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(item.DisplayName) && !string.IsNullOrEmpty(tabItem.Name) &&
                         string.Equals(item.DisplayName, tabItem.Name, StringComparison.OrdinalIgnoreCase)));

                    if (matched is not null)
                    {
                        var itemKeyStr = matched.Key.ToString();
                        newBox.ItemTabAssignments[itemKeyStr] = dTab.Id;
                        if (!nextState.Assignments.ContainsKey(itemKeyStr))
                        {
                            nextState.Assignments[itemKeyStr] = newBox.Id;
                            newBox.ItemOrder.Add(itemKeyStr);
                        }
                    }
                }
            }


            if (cBox.Title != "0" || cBox.Items.Count < 20)
            {
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

            }

            foreach (var item in availableItems)
            {
                if (item.IsSystem) continue;
                var itemKeyStr = item.Key.ToString();
                if (nextState.Assignments.ContainsKey(itemKeyStr)) continue;

                if (IsItemMatchingBox(item, newBox.Title))
                {
                    nextState.Assignments[itemKeyStr] = newBox.Id;
                    newBox.ItemOrder.Add(itemKeyStr);
                }
            }

            nextState.Boxes.Add(newBox);
        }

        return nextState;
    }

    private static LayoutRect ResolveBoxBounds(CoodeskerBoxModel cBox, int index, LayoutRect monitorBounds)
    {
        double scaleX = monitorBounds.Width / 2560.0;
        double scaleY = monitorBounds.Height / 1440.0;
        if (scaleX <= 0) scaleX = 1.0;
        if (scaleY <= 0) scaleY = 1.0;

        if (cBox.Right > cBox.Left && cBox.Bottom > cBox.Top && (cBox.Left > 0 || cBox.Top > 0))
        {
            return new LayoutRect(cBox.Left * scaleX, cBox.Top * scaleY, cBox.Width * scaleX, cBox.Height * scaleY);
        }

        var title = (cBox.Title ?? string.Empty).Trim();
        var (px, py, pw, ph) = title switch
        {
            "工具" => (780.0, 20.0, 480.0, 280.0),
            "0" => (1320.0, 20.0, 520.0, 280.0),
            "文档" => (1900.0, 20.0, 520.0, 280.0),
            "图片" => (830.0, 480.0, 320.0, 280.0),
            "浏览器" => (1200.0, 480.0, 390.0, 280.0),
            "网络" => (1640.0, 480.0, 330.0, 280.0),
            "AI" => (2030.0, 480.0, 390.0, 280.0),
            "office" => (830.0, 820.0, 320.0, 280.0),
            "专业" => (1820.0, 720.0, 350.0, 320.0),
            _ => (750.0 + (index % 3) * 400.0, 100.0 + (index / 3) * 320.0, 360.0, 280.0)
        };

        return new LayoutRect(px * scaleX, py * scaleY, pw * scaleX, ph * scaleY);
    }

    private static bool IsItemMatchingBox(DesktopItemRef item, string boxTitle)
    {
        var name = item.DisplayName.ToLowerInvariant();
        var path = (item.FileSystemPath ?? item.ParsingName ?? string.Empty).ToLowerInvariant();
        var ext = Path.GetExtension(path);

        return boxTitle switch
        {
            "图片" => ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico" or ".webp" or ".svg" or ".psd",
            "文档" => ext is ".doc" or ".docx" or ".pdf" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf" or ".csv"
                      || name.Contains("知云") || name.Contains("zotero") || name.Contains("pdf") || name.Contains("阅读器"),
            "浏览器" => name.Contains("browser") || name.Contains("浏览器") || name.Contains("edge") || name.Contains("chrome")
                        || name.Contains("firefox") || name.Contains("夸克") || name.Contains("百度网盘") || name.Contains("网盘"),
            "AI" => name.Contains("ai") || name.Contains("豆包") || name.Contains("元宝") || name.Contains("codebuddy")
                    || name.Contains("codex") || name.Contains("chat") || name.Contains("gpt") || name.Contains("deepseek"),
            "专业" => name.Contains("code") || name.Contains("crabdesk") || name.Contains("ecopaste") || name.Contains("zcode")
                    || name.Contains("docker") || name.Contains("trae") || name.Contains("qoder") || name.Contains("wiki")
                    || name.Contains("git") || name.Contains("studio") || name.Contains("dev") || name.Contains("开发"),
            "网络" => name.Contains("远程") || name.Contains("todesk") || name.Contains("easyconnect") || name.Contains("sakurafrp")
                    || name.Contains("rustdesk") || name.Contains("anydesk") || name.Contains("vpn") || name.Contains("switch"),
            "office" => name.Contains("office") || name.Contains("wps") || name.Contains("word") || name.Contains("excel")
                        || name.Contains("powerpoint") || name.Contains("邮箱") || name.Contains("会议") || name.Contains("企业微信"),
            "工具" => name.Contains("tool") || name.Contains("工具") || name.Contains("管家") || name.Contains("驱动")
                    || name.Contains("清理") || name.Contains("clean") || name.Contains("助手") || name.Contains("tinybar")
                    || name.Contains("hub") || name.Contains("quicklook") || name.Contains("nexclip") || name.Contains("wiztree")
                    || name.Contains("mchose") || name.Contains("napcat") || name.Contains("imetool") || name.Contains("tiez")
                    || name.Contains("pastex") || name.Contains("mklink"),
            _ => false
        };
    }
}
