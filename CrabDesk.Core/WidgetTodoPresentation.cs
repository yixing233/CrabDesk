using System.Globalization;

namespace CrabDesk.Core;

public sealed record WidgetTodoRow(WidgetTodoItem? Task, bool IsCompletedHeader = false)
{
    public int Height => IsCompletedHeader ? 28 : Task?.HasDateRange == true ? 46 : 30;
}

public static class WidgetTodoPresentation
{
    public static List<WidgetTodoRow> Rows(DesktopWidget widget, bool completedExpanded = false, bool adding = false)
    {
        var rows = widget.Tasks.Where(task => !task.IsCompleted).Select(task => new WidgetTodoRow(task)).ToList();
        if (adding) rows.Add(new(null));
        if (widget.Tasks.Any(task => task.IsCompleted))
        {
            rows.Add(new(null, IsCompletedHeader: true));
            if (completedExpanded) rows.AddRange(widget.Tasks.Where(task => task.IsCompleted).Select(task => new WidgetTodoRow(task)));
        }
        return rows;
    }

    public static string DateLabel(WidgetTodoItem task, int currentYear)
    {
        string Format(DateOnly date) => date.ToString(date.Year == currentYear ? "M月d日" : "yyyy年M月d日", CultureInfo.InvariantCulture);
        return (task.StartDate, task.EndDate) switch
        {
            ({ } start, { } end) when start == end => Format(start),
            ({ } start, { } end) when start.Year != end.Year => $"{Format(start)}—{Format(end)}",
            ({ } start, { } end) when start.Month == end.Month => $"{Format(start)}—{end.Day}日",
            ({ } start, { } end) => $"{Format(start)}—{end:M月d日}",
            ({ } start, null) => $"开始 {Format(start)}",
            (null, { } end) => $"截止 {Format(end)}",
            _ => string.Empty
        };
    }
}
