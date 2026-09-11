using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrabDesk.Core;

// These DTOs describe a structured layout exchange document. They are NOT a
// decoder for Coodesker's proprietary binary backup or arbitrary heap fragments.
public sealed class CoodeskerSourceIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number when reader.TryGetInt64(out var id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException("标签标识必须为字符串或整数。")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public sealed class CoodeskerItemModel
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("file_path")]
    public string SourceFilePath { get => FilePath; set => FilePath = value; }

    public int Position { get; set; }

    [JsonConverter(typeof(CoodeskerSourceIdConverter))]
    public string TabId { get; set; } = string.Empty;

    [JsonPropertyName("tab_id"), JsonConverter(typeof(CoodeskerSourceIdConverter))]
    public string SourceTabId { get => TabId; set => TabId = value; }
}

public sealed class CoodeskerTabModel
{
    [JsonConverter(typeof(CoodeskerSourceIdConverter))]
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get => Title; set => Title = value; }

    public int? Position { get; set; }
    public bool IsAggregate { get; set; }
    public List<CoodeskerItemModel> Items { get; set; } = [];

    [JsonPropertyName("apps")]
    public List<CoodeskerItemModel> Apps { get => Items; set => Items = value; }
}

public sealed class CoodeskerBoxModel
{
    [JsonConverter(typeof(CoodeskerSourceIdConverter))]
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public int CategoryId { get; set; }
    public double Left { get; set; }

    [JsonPropertyName("pos_left")]
    public double PosLeft { get => Left; set => Left = value; }

    public double Top { get; set; }

    [JsonPropertyName("pos_top")]
    public double PosTop { get => Top; set => Top = value; }

    public double Right { get; set; }

    [JsonPropertyName("pos_right")]
    public double PosRight { get => Right; set => Right = value; }

    public double Bottom { get; set; }

    [JsonPropertyName("pos_bottom")]
    public double PosBottom { get => Bottom; set => Bottom = value; }

    public bool IsCollapsed { get; set; }

    [JsonPropertyName("min_state")]
    public int MinState { get => IsCollapsed ? 1 : 0; set => IsCollapsed = value == 1; }

    public string Directory { get; set; } = string.Empty;
    public List<CoodeskerTabModel> Tabs { get; set; } = [];
    public List<CoodeskerBoxModel> SubBoxes { get; set; } = [];

    [JsonPropertyName("sub_boxes")]
    public List<CoodeskerBoxModel> SourceSubBoxes { get => SubBoxes; set => SubBoxes = value; }

    public List<CoodeskerItemModel> Items { get; set; } = [];

    [JsonPropertyName("apps")]
    public List<CoodeskerItemModel> Apps { get => Items; set => Items = value; }

    [JsonIgnore]
    public double Width => Right - Left;

    [JsonIgnore]
    public double Height => Bottom - Top;
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
            return JsonSerializer.Deserialize<List<CoodeskerBoxModel>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 32
            }) ?? [];
        }
        catch (JsonException)
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
        if (coodeskerBoxes.Count == 0 || monitors.Count == 0)
        {
            throw new InvalidDataException("没有有效布局或显示器信息，不能覆盖当前布局。");
        }

        // Deep-clone so mutable settings/rules are never shared with the rollback snapshot.
        var next = JsonSerializer.Deserialize<CrabDeskState>(
            JsonSerializer.Serialize(currentState, JsonLayoutStore.SerializerOptions),
            JsonLayoutStore.SerializerOptions)!;

        next.Boxes.Clear();
        next.Assignments.Clear();
        next.DesktopIconPositions.Clear();
        next.DesktopIconLayout.Clear();
        // Rules targeting old boxes must not undo imported assignments on refresh.
        next.OrganizationRules.RemoveAll(rule => rule.TargetBoxId is not null);

        var items = desktopItems.ToArray();
        foreach (var source in coodeskerBoxes)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Title) ||
                !double.IsFinite(source.Left) || !double.IsFinite(source.Top) ||
                !double.IsFinite(source.Right) || !double.IsFinite(source.Bottom) ||
                source.Width <= 0 || source.Height <= 0 ||
                source.Items is null || source.Tabs is null || source.SubBoxes is null)
            {
                throw new InvalidDataException("盒子缺少完整坐标或内容信息，不能用默认值覆盖当前布局。");
            }

            var monitor = monitors.FirstOrDefault(m => m.PixelBounds.Contains(source.Left, source.Top))
                ?? monitors.FirstOrDefault(m => m.IsPrimary)
                ?? monitors[0];

            var scale = monitor.DpiScale;
            if (!double.IsFinite(scale) || scale <= 0)
            {
                throw new InvalidDataException("显示器缩放信息无效。");
            }

            var box = new DesktopBox
            {
                Title = source.Title.Trim(),
                MonitorId = monitor.Id,
                StackOrder = next.Boxes.Count,
                Bounds = new LayoutRect(
                    (source.Left - monitor.PixelBounds.X) / scale,
                    (source.Top - monitor.PixelBounds.Y) / scale,
                    source.Width / scale,
                    source.Height / scale)
                    .Clamp(new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height)),
                ExpandOnHover = source.IsCollapsed,
                IsCollapsed = source.IsCollapsed,
                SortMode = BoxSortMode.Manual
            };
            next.Boxes.Add(box);

            var tabs = GetTabs(source);
            var ids = new Dictionary<string, Guid?>(StringComparer.Ordinal);
            var tabAssignments = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            // Scoped strictly to this box: never cross-wire tabs between different boxes.
            foreach (var tab in tabs)
            {
                if (tab is null || string.IsNullOrWhiteSpace(tab.Title) || tab.Items is null)
                {
                    throw new InvalidDataException("子标签缺少名称或图标列表。");
                }

                Guid? id = null;
                if (!tab.IsAggregate)
                {
                    var target = new DesktopBoxTab { Title = tab.Title.Trim() };
                    box.ManualTabs.Add(target);
                    id = target.Id;
                }

                if (!string.IsNullOrEmpty(tab.Id) && !ids.TryAdd(tab.Id, id))
                {
                    throw new InvalidDataException($"盒子“{source.Title}”存在重复子标签 ID：{tab.Id}");
                }

                foreach (var item in tab.Items.OrderBy(item => item.Position))
                {
                    Assign(item, id);
                }
            }

            foreach (var item in source.Items.OrderBy(item => item.Position))
            {
                Guid? tabId = null;
                if (!string.IsNullOrEmpty(item.TabId) && !ids.TryGetValue(item.TabId, out tabId))
                {
                    throw new InvalidDataException($"图标引用了不存在的子标签：{item.TabId}");
                }

                Assign(item, tabId);
            }

            void Assign(CoodeskerItemModel sourceItem, Guid? tabId)
            {
                var matches = !string.IsNullOrWhiteSpace(sourceItem.FilePath)
                    ? items.Where(item => string.Equals(
                        NormalizePath(item.FileSystemPath),
                        NormalizePath(sourceItem.FilePath),
                        StringComparison.OrdinalIgnoreCase)).ToArray()
                    : items.Where(item => !string.IsNullOrWhiteSpace(sourceItem.Name) &&
                        string.Equals(item.DisplayName, sourceItem.Name, StringComparison.OrdinalIgnoreCase)).ToArray();

                if (matches.Length == 0) return;
                if (matches.Length != 1)
                {
                    throw new InvalidDataException($"图标名称存在歧义：{sourceItem.Name}");
                }

                var key = matches[0].Key.ToString();
                if (next.Assignments.TryGetValue(key, out var owner) && owner != box.Id)
                {
                    throw new InvalidDataException($"同一图标被分配给多个盒子：{sourceItem.Name}");
                }

                if (next.Assignments.TryAdd(key, box.Id))
                {
                    box.ItemOrder.Add(key);
                }

                if (tabId is not { } targetTab) return;
                if (tabAssignments.TryGetValue(key, out var previousTab) && previousTab != targetTab)
                {
                    throw new InvalidDataException($"同一图标被分配给多个子标签：{sourceItem.Name}");
                }

                tabAssignments[key] = targetTab;
                box.ItemTabAssignments[key] = targetTab;
            }
        }

        return next;
    }

    private static IReadOnlyList<CoodeskerTabModel> GetTabs(CoodeskerBoxModel source)
    {
        if (source.Tabs.Count > 0 && source.SubBoxes.Count > 0)
        {
            throw new InvalidDataException("同时存在 tabs 与 sub_boxes，无法确定标签关系。");
        }

        if (source.SubBoxes.Any(child => child is null || child.SubBoxes.Count > 0 || child.Tabs.Count > 0))
        {
            throw new InvalidDataException("嵌套多级组合盒子尚不支持，未执行覆盖。");
        }

        var tabs = source.Tabs.Count > 0
            ? source.Tabs
            : source.SubBoxes.Select(child => new CoodeskerTabModel
            {
                Id = child.Id,
                Title = child.Title,
                Items = child.Items
            }).ToList();

        return tabs.Select((tab, index) => (tab, index))
            .OrderBy(entry => entry.tab?.Position ?? entry.index)
            .Select(entry => entry.tab)
            .ToArray()!;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("导入文件包含无效路径。", exception);
        }
    }
}
