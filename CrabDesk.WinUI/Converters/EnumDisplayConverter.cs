using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml.Data;

namespace CrabDesk.WinUI.Converters;

public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        GetLabel(value);

    public object ConvertBack(object value, Type targetType, object parameter, string language) => value;

    public static string GetLabel(object? value) => value switch
    {
        ApplicationThemeMode.System => "跟随系统",
        ApplicationThemeMode.Light => "浅色",
        ApplicationThemeMode.Dark => "深色",

        OrganizationRuleAction.AssignToBox => "放入盒子",
        OrganizationRuleAction.KeepUnassigned => "保留在桌面",
        OrganizationRuleAction.Ignore => "忽略，不整理",

        BackdropKind.Mica => "云母",
        BackdropKind.MicaAlt => "云母（增强）",
        BackdropKind.Acrylic => "亚克力",

        BoxMaterialKind.Solid => "纯色",
        BoxMaterialKind.AcrylicPreview => "Acrylic（预览）",

        BoxTitleAlignment.Left => "左对齐",
        BoxTitleAlignment.Center => "居中",

        BoxViewMode.Grid => "图标",
        BoxViewMode.List => "列表",

        BoxSortMode.Manual => "手动",
        BoxSortMode.Name => "名称",
        BoxSortMode.Type => "类型",
        BoxSortMode.Modified => "修改时间",

        UpdateChannel.Stable => "稳定版",
        UpdateChannel.Preview => "预览版",

        _ => value?.ToString() ?? string.Empty
    };
}
