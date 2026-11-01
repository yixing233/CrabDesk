using System.Globalization;

namespace CrabDesk.Core;

public sealed record CalendarDateDetails(DateOnly Date, string LunarDate, IReadOnlyList<string> Festivals, string SolarTerm)
{
    private static readonly object UpcomingGate = new();
    private static DateOnly? _upcomingFrom;
    private static IReadOnlyList<UpcomingCalendarEvent> _upcomingEvents = [];

    public static IReadOnlyList<UpcomingCalendarEvent> GetUpcomingEvents(DateOnly from)
    {
        if (from.Year is < 1901 or > 2100) return [];
        lock (UpcomingGate)
        {
            if (_upcomingFrom == from) return _upcomingEvents;
            var events = new List<UpcomingCalendarEvent>();
            var festivalCount = 0; var termCount = 0;
            for (var offset = 0; offset <= 370; offset++)
            {
                var date = from.AddDays(offset);
                if (date.Year > 2100) break;
                var details = ForDate(date);
                foreach (var festival in details.Festivals)
                {
                    if (festivalCount == 2) break;
                    events.Add(new(date, festival, false, offset)); festivalCount++;
                }
                if (termCount < 2 && details.SolarTerm.Length > 0)
                { events.Add(new(date, details.SolarTerm, true, offset)); termCount++; }
                if (festivalCount == 2 && termCount == 2) break;
            }
            _upcomingFrom = from;
            return _upcomingEvents = events.OrderBy(item => item.Date).ThenBy(item => item.IsSolarTerm).ToList().AsReadOnly();
        }
    }

    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { Date.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")) };
            lines.Add(LunarDate.Length > 0 ? "农历 " + LunarDate : "此日期暂无农历信息");
            if (Festivals.Count > 0) lines.Add("节日 · " + string.Join("、", Festivals));
            if (SolarTerm.Length > 0) lines.Add("节气 · " + SolarTerm);
            return string.Join(Environment.NewLine, lines);
        }
    }

    public static CalendarDateDetails ForDate(DateOnly date)
    {
        // Match the published modern Chinese calendar conversion range.
        // Outside it the month grid remains usable without inventing lunar dates.
        if (date.Year is < 1901 or > 2100) return new(date, string.Empty, [], string.Empty);
        var solar = global::Lunar.Solar.FromDate(date.ToDateTime(TimeOnly.MinValue));
        var lunar = solar.Lunar;
        return new(date, $"{lunar.YearInGanZhi}年 {lunar.MonthInChinese}月{lunar.DayInChinese}",
            solar.Festivals.Concat(lunar.Festivals).Distinct(StringComparer.Ordinal).ToArray(), lunar.JieQi);
    }
}

public sealed record UpcomingCalendarEvent(DateOnly Date, string Name, bool IsSolarTerm, int DaysRemaining)
{
    public string KindLabel => IsSolarTerm ? "节气" : "节日";
    public string DateLabel => Date.ToString("M月d日", CultureInfo.InvariantCulture);
}
