using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public sealed class CoodeskerItemModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("tab_id"), JsonConverter(typeof(CoodeskerSourceIdConverter))]
    public string? TabId { get; set; }
}

public sealed class CoodeskerTabModel
{
    [JsonPropertyName("id"), JsonConverter(typeof(CoodeskerSourceIdConverter))]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get => Title; set => Title = value; }

    [JsonPropertyName("items")]
    public List<CoodeskerItemModel> Items { get; set; } = [];

    [JsonPropertyName("apps")]
    public List<CoodeskerItemModel> Apps { get => Items; set => Items = value; }

    [JsonIgnore]
    public bool IsAggregate => string.Equals(Title.Trim(), "全部", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Title.Trim(), "all", StringComparison.OrdinalIgnoreCase);
}

public sealed class CoodeskerBoxModel
{
    [JsonPropertyName("id"), JsonConverter(typeof(CoodeskerSourceIdConverter))]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    [JsonPropertyName("left")]
    public double Left { get; set; }

    [JsonPropertyName("pos_left")]
    public double PosLeft { get => Left; set => Left = value; }

    [JsonPropertyName("top")]
    public double Top { get; set; }

    [JsonPropertyName("pos_top")]
    public double PosTop { get => Top; set => Top = value; }

    [JsonPropertyName("right")]
    public double Right { get; set; }

    [JsonPropertyName("pos_right")]
    public double PosRight { get => Right; set => Right = value; }

    [JsonPropertyName("bottom")]
    public double Bottom { get; set; }

    [JsonPropertyName("pos_bottom")]
    public double PosBottom { get => Bottom; set => Bottom = value; }

    [JsonPropertyName("is_collapsed")]
    public bool IsCollapsed { get; set; }

    [JsonPropertyName("min_state")]
    public int MinState { get => IsCollapsed ? 1 : 0; set => IsCollapsed = (value == 1); }

    [JsonPropertyName("directory")]
    public string Directory { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<CoodeskerItemModel> Items { get; set; } = [];

    [JsonPropertyName("apps")]
    public List<CoodeskerItemModel> Apps { get => Items; set => Items = value; }

    [JsonPropertyName("tabs")]
    public List<CoodeskerTabModel> Tabs { get; set; } = [];

    [JsonPropertyName("sub_boxes")]
    public List<CoodeskerBoxModel> SubBoxes { get; set; } = [];

    [JsonIgnore]
    public double Width => Right > Left ? (Right - Left) : 360;

    [JsonIgnore]
    public double Height => Bottom > Top ? (Bottom - Top) : 280;
}

public sealed class CoodeskerSourceIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => reader.TryGetInt64(out var number)
                ? number.ToString()
                : reader.GetDouble().ToString("0"),
            _ => string.Empty
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
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
            var direct = JsonSerializer.Deserialize<List<CoodeskerBoxModel>>(json, JsonLayoutStore.SerializerOptions);
            if (direct is { Count: > 0 }) return direct;
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var propName in new[] { "boxes", "boxs", "containers", "data" })
                {
                    if (root.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var result = JsonSerializer.Deserialize<List<CoodeskerBoxModel>>(arr.GetRawText(), JsonLayoutStore.SerializerOptions);
                        if (result is { Count: > 0 }) return result;
                    }
                }
            }
        }
        catch { }

        return [];
    }

    public static CrabDeskState CreateOverwriteState(
        IReadOnlyList<CoodeskerBoxModel> coodeskerBoxes,
        CrabDeskState currentState,
        IReadOnlyList<MonitorLayout> monitors,
        IEnumerable<DesktopItemRef> desktopItems)
    {
        if (coodeskerBoxes is null || coodeskerBoxes.Count == 0)
        {
            throw new ArgumentException("酷呆桌面数据为空，未执行覆盖。", nameof(coodeskerBoxes));
        }

        if (monitors is null || monitors.Count == 0)
        {
            throw new ArgumentException("未检测到有效显示器信息。", nameof(monitors));
        }

        var next = JsonSerializer.Deserialize<CrabDeskState>(
            JsonSerializer.Serialize(currentState, JsonLayoutStore.SerializerOptions),
            JsonLayoutStore.SerializerOptions)!;

        next.Boxes.Clear();
        next.Assignments.Clear();
        next.DesktopIconPositions.Clear();
        next.DesktopIconLayout.Clear();
        next.OrganizationRules.RemoveAll(rule => rule.TargetBoxId is not null);

        var items = desktopItems.ToArray();
        var primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        foreach (var source in coodeskerBoxes)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Title)) continue;

            var monitor = monitors.FirstOrDefault(m => m.PixelBounds.Contains(source.Left, source.Top))
                ?? primaryMonitor;

            var targetRect = ResolveBoxBounds(source, next.Boxes.Count, monitor.WorkArea);

            var box = new DesktopBox
            {
                Title = source.Title.Trim(),
                MonitorId = monitor.Id,
                StackOrder = next.Boxes.Count,
                Bounds = targetRect.Clamp(new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height)),
                ExpandOnHover = source.IsCollapsed,
                IsCollapsed = source.IsCollapsed,
                SortMode = BoxSortMode.Manual
            };
            next.Boxes.Add(box);

            var tabs = GetTabs(source);
            var tabIdMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            foreach (var tab in tabs)
            {
                if (tab.IsAggregate) continue; // "全部" is built-in in CrabDesk

                var manualTab = new DesktopBoxTab
                {
                    Id = Guid.NewGuid(),
                    Title = tab.Title.Trim()
                };
                box.ManualTabs.Add(manualTab);

                if (!string.IsNullOrEmpty(tab.Id))
                {
                    tabIdMap[tab.Id] = manualTab.Id;
                }

                // Explicit items in sub-tab
                foreach (var tabItem in tab.Items.OrderBy(i => i.Position))
                {
                    AssignItem(tabItem, manualTab.Id);
                }
            }

            // Explicit items in box
            foreach (var item in source.Items.OrderBy(i => i.Position))
            {
                Guid? targetTabId = null;
                if (!string.IsNullOrEmpty(item.TabId) && tabIdMap.TryGetValue(item.TabId, out var mappedId))
                {
                    targetTabId = mappedId;
                }
                AssignItem(item, targetTabId);
            }

            // Auto-classify matching desktop items for category boxes (excluding "0")
            if (box.Title != "0")
            {
                foreach (var dItem in items)
                {
                    if (dItem.IsSystem) continue; // Keep "This PC", "Recycle Bin" on desktop
                    var key = dItem.Key.ToString();
                    if (next.Assignments.ContainsKey(key)) continue;

                    if (IsItemMatchingBox(dItem, box.Title))
                    {
                        next.Assignments[key] = box.Id;
                        box.ItemOrder.Add(key);
                        // If box has sub-tabs, leave tab assignment null so it appears in "全部"
                    }
                }
            }

            void AssignItem(CoodeskerItemModel sourceItem, Guid? tabId)
            {
                var matched = !string.IsNullOrWhiteSpace(sourceItem.FilePath)
                    ? items.FirstOrDefault(i => string.Equals(NormalizePath(i.FileSystemPath), NormalizePath(sourceItem.FilePath), StringComparison.OrdinalIgnoreCase))
                    : items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(sourceItem.Name) && string.Equals(i.DisplayName, sourceItem.Name, StringComparison.OrdinalIgnoreCase));

                if (matched is null) return;

                var key = matched.Key.ToString();
                if (!next.Assignments.ContainsKey(key))
                {
                    next.Assignments[key] = box.Id;
                    box.ItemOrder.Add(key);
                }

                if (tabId.HasValue)
                {
                    box.ItemTabAssignments[key] = tabId.Value;
                }
            }
        }

        return next;
    }

    private static LayoutRect ResolveBoxBounds(CoodeskerBoxModel cBox, int index, LayoutRect workArea)
    {
        double scaleX = Math.Abs(workArea.Width - 2560) < 50 ? 1.0 : (workArea.Width / 2560.0);
        double scaleY = Math.Abs(workArea.Height - 1440) < 60 ? 1.0 : (workArea.Height / 1440.0);
        if (scaleX <= 0) scaleX = 1.0;
        if (scaleY <= 0) scaleY = 1.0;

        if (cBox.Right > cBox.Left && cBox.Bottom > cBox.Top && (cBox.Left > 0 || cBox.Top > 0))
        {
            return new LayoutRect(cBox.Left * scaleX, cBox.Top * scaleY, cBox.Width * scaleX, cBox.Height * scaleY);
        }

        var title = cBox.Title.Trim();
        var (px, py, pw, ph) = title switch
        {
            "工具" => (780.0, 20.0, 480.0, 300.0),
            "0" => (1320.0, 20.0, 520.0, 300.0),
            "文档" => (1900.0, 20.0, 520.0, 300.0),
            "游戏" => (450.0, 460.0, 360.0, 320.0),
            "图片" => (830.0, 460.0, 320.0, 280.0),
            "浏览器" => (1200.0, 460.0, 390.0, 280.0),
            "网络" => (1640.0, 460.0, 330.0, 280.0),
            "AI" => (2030.0, 460.0, 390.0, 280.0),
            "office" => (830.0, 820.0, 320.0, 280.0),
            "专业" => (1820.0, 720.0, 350.0, 320.0),
            _ => (100.0 + (index % 3) * 400.0, 100.0 + (index / 3) * 320.0, 360.0, 280.0)
        };

        return new LayoutRect(px * scaleX, py * scaleY, pw * scaleX, ph * scaleY);
    }

    private static IReadOnlyList<CoodeskerTabModel> GetTabs(CoodeskerBoxModel source)
    {
        var result = new List<CoodeskerTabModel>();

        if (source.Tabs.Count > 0)
        {
            result.AddRange(source.Tabs);
        }
        else if (source.SubBoxes.Count > 0)
        {
            foreach (var child in source.SubBoxes)
            {
                if (child is null) continue;
                result.Add(new CoodeskerTabModel
                {
                    Id = child.Id,
                    Title = string.IsNullOrWhiteSpace(child.Title) ? "新标签" : child.Title.Trim(),
                    Items = child.Items ?? []
                });
            }
        }
        else if (source.Title is "图片")
        {
            result.Add(new CoodeskerTabModel { Title = "新标签" });
        }

        return result;
    }

    private static bool IsItemMatchingBox(DesktopItemRef item, string boxTitle)
    {
        var name = item.DisplayName.ToLowerInvariant();
        var path = (item.FileSystemPath ?? item.ParsingName ?? string.Empty).ToLowerInvariant();
        var ext = Path.GetExtension(path);

        return boxTitle switch
        {
            "图片" => ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico" or ".webp" or ".svg" or ".psd",
            "文档" => ext is ".doc" or ".docx" or ".pdf" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf" or ".csv" or ".dwg" or ".ahk"
                      || name.Contains("知云") || name.Contains("zotero") || name.Contains("pdf") || name.Contains("阅读器"),
            "游戏" => name.Contains("无畏契约") || name.Contains("炉石传说") || name.Contains("和平精英") || name.Contains("steam")
                      || name.Contains("wegame") || name.Contains("epic") || name.Contains("游戏") || name.Contains("加加")
                      || name.Contains("土豆兄弟") || name.Contains("firestone") || name.Contains("hearthstone"),
            "浏览器" => name.Contains("browser") || name.Contains("浏览器") || name.Contains("firefox") || name.Contains("edge")
                        || name.Contains("chrome") || name.Contains("夸克") || name.Contains("百度网盘") || name.Contains("阿里云盘") || name.Contains("迅雷"),
            "AI" => name.Contains("ai") || name.Contains("豆包") || name.Contains("元宝") || name.Contains("codebuddy")
                    || name.Contains("codex") || name.Contains("chat") || name.Contains("gpt") || name.Contains("deepseek")
                    || name.Contains("lm studio") || name.Contains("llm wiki") || name.Contains("astrbot"),
            "专业" => name.Contains("code") || name.Contains("crabdesk") || name.Contains("zcode") || name.Contains("docker")
                    || name.Contains("trae") || name.Contains("qoder") || name.Contains("antigravity") || name.Contains("微信开发者工具")
                    || name.Contains("visual studio") || name.Contains("git") || name.Contains("dev") || name.Contains("开发"),
            "网络" => name.Contains("远程") || name.Contains("uu远程") || name.Contains("todesk") || name.Contains("easyconnect")
                    || name.Contains("sakurafrp") || name.Contains("rustdesk") || name.Contains("anydesk") || name.Contains("cc switch")
                    || name.Contains("discord") || name.Contains("telegram") || name.Contains("雷神加速器") || name.Contains("小黑盒"),
            "office" => name.Contains("office") || name.Contains("wps") || name.Contains("visio") || name.Contains("word")
                        || name.Contains("excel") || name.Contains("powerpoint") || name.Contains("邮箱") || name.Contains("会议") || name.Contains("企业微信"),
            "工具" => name.Contains("tool") || name.Contains("工具") || name.Contains("管家") || name.Contains("驱动") || name.Contains("凌豹")
                    || name.Contains("atk") || name.Contains("cockpit") || name.Contains("deskpins") || name.Contains("ev录屏")
                    || name.Contains("everywhere") || name.Contains("imetool") || name.Contains("ktc") || name.Contains("listary")
                    || name.Contains("mobaxterm") || name.Contains("nexclip") || name.Contains("pastex") || name.Contains("pi-desk")
                    || name.Contains("quicklook") || name.Contains("tiez") || name.Contains("umi-ocr") || name.Contains("wiztree")
                    || name.Contains("tinybar") || name.Contains("origin"),
            _ => false
        };
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim().TrimEnd('\\', '/'); }
    }
}

