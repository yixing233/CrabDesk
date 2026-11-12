using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public enum WidgetFileShelfMode { Reference, Copy }

public sealed class WidgetFileShelfItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OriginalPath { get; set; } = string.Empty;
    public string StoredRelativePath { get; set; } = string.Empty;
    public WidgetFileShelfMode Mode { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    [JsonIgnore] public string Name => Path.GetFileName(OriginalPath ?? string.Empty);
    // Availability is refreshed by the runtime away from the UI thread.
    [JsonIgnore] public bool? IsAvailable { get; set; }
}

public static class WidgetFileShelf
{
    public static void Normalize(DesktopWidget widget)
    {
        widget.FileShelfFloating ??= new();
        widget.FileShelfFloating.Normalize();
        if (widget.Kind == DesktopWidgetKind.FileShelf && widget.Title == "文件暂存") widget.Title = string.Empty;
        if (!Enum.IsDefined(widget.FileShelfMode)) widget.FileShelfMode = WidgetFileShelfMode.Reference;
        widget.FileShelfItems ??= [];
        widget.FileShelfStatus ??= string.Empty;
        widget.FileShelfItems.RemoveAll(item => item is null);
        var ids = new HashSet<Guid>();
        foreach (var item in widget.FileShelfItems)
        {
            item.OriginalPath ??= string.Empty;
            item.StoredRelativePath ??= string.Empty;
            if (!Enum.IsDefined(item.Mode)) item.Mode = WidgetFileShelfMode.Reference;
            if (item.Id == Guid.Empty || !ids.Add(item.Id))
            {
                item.Id = Guid.NewGuid();
                ids.Add(item.Id);
            }
        }
    }
}

// Shared by the desktop renderer and the settings preview.
public static class WidgetFileShelfPresentation
{
    public readonly record struct Layout(LayoutRect Mode, LayoutRect Count, LayoutRect List, LayoutRect Footer, double RowHeight, int Rows);

    public static LayoutRect AddButton(double cornerRadius) =>
        new(Math.Max(8, Math.Clamp(cornerRadius, 0, 20) * .75), 4, 24, 24);

    // Body-relative coordinates.
    public static Layout Arrange(double width, double height, double fontScale)
    {
        var scale = Math.Max(1, WidgetTypography.NormalizeScale(fontScale));
        var toolbarHeight = 20 * scale;
        var footerHeight = 16 * scale;
        var countWidth = Math.Min(width / 2, 48 * scale);
        var modeWidth = Math.Min(110 * scale, Math.Max(0, width - countWidth - 6));
        var rowHeight = 32 * scale;
        var listHeight = Math.Max(0, height - toolbarHeight - footerHeight - 4);
        return new(new(0, 0, modeWidth, toolbarHeight), new(width - countWidth, 0, countWidth, toolbarHeight),
            new(0, toolbarHeight + 2, width, listHeight), new(0, height - footerHeight, width, footerHeight),
            rowHeight, Math.Max(0, (int)(listHeight / rowHeight)));
    }

    public static string ModeLabel(DesktopWidget widget, double width) =>
        width < 96 * WidgetTypography.NormalizeScale(widget.FontScale)
            ? widget.FileShelfMode == WidgetFileShelfMode.Copy ? "复制" : "引用"
            : widget.FileShelfMode == WidgetFileShelfMode.Copy ? "复制暂存" : "引用原文件";

    public static string ItemDetail(WidgetFileShelfItem item)
    {
        if (item.IsAvailable == false) return "文件不可用";
        var mode = item.Mode == WidgetFileShelfMode.Copy ? "副本" : "引用";
        var folder = Path.GetFileName(Path.GetDirectoryName(item.OriginalPath));
        return string.IsNullOrEmpty(folder) ? mode : $"{mode} · {folder}";
    }

    public static string Footer(DesktopWidget widget, int selected = 0)
    {
        if (widget.FileShelfBusy) return string.IsNullOrEmpty(widget.FileShelfStatus) ? "正在处理文件…" : widget.FileShelfStatus;
        if (selected > 0) return $"已选 {selected} 项 · 拖出使用";
        if (!string.IsNullOrEmpty(widget.FileShelfStatus)) return widget.FileShelfStatus;
        return widget.FileShelfItems.Count == 0 ? "支持拖入多个文件" : "Ctrl 多选 · 拖出使用";
    }
}
