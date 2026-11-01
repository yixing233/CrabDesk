using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class CalendarDateDetailsTests
{
    // Lunar dates and solar terms checked against the Hong Kong Observatory
    // Gregorian-Lunar conversion tables for 2023, 2025 and 2026.
    [Theory]
    [InlineData(2026, 2, 17, "正月初一", "春节")]
    [InlineData(2026, 2, 16, "腊月廿九", "除夕")]
    [InlineData(2026, 6, 19, "五月初五", "端午节")]
    [InlineData(2026, 9, 25, "八月十五", "中秋节")]
    public void TraditionalFestivalsFollowTheLunarDate(int year, int month, int day, string lunarDate, string festival)
    {
        var details = CalendarDateDetails.ForDate(new(year, month, day));
        Assert.EndsWith(lunarDate, details.LunarDate);
        Assert.Contains(festival, details.Festivals);
        Assert.Contains(festival, details.ToolTipText);
    }

    [Theory]
    [InlineData(2026, 1, 5, "小寒")]
    [InlineData(2026, 2, 4, "立春")]
    [InlineData(2026, 4, 5, "清明")]
    [InlineData(2026, 6, 21, "夏至")]
    [InlineData(2026, 12, 22, "冬至")]
    public void SolarTermsAppearOnlyOnTheirActualDay(int year, int month, int day, string term)
    {
        var date = new DateOnly(year, month, day);
        var details = CalendarDateDetails.ForDate(date);
        Assert.Equal(term, details.SolarTerm);
        Assert.Contains("节气 · " + term, details.ToolTipText);
        Assert.Empty(CalendarDateDetails.ForDate(date.AddDays(1)).SolarTerm);
    }

    [Theory]
    [InlineData(2023, 3, 22, "闰二月初一")]
    [InlineData(2025, 7, 25, "闰六月初一")]
    public void LeapMonthsKeepTheLeapMarkerWithoutDuplicatingTraditionalFestivals(int year, int month, int day, string expected)
    {
        var details = CalendarDateDetails.ForDate(new(year, month, day));
        Assert.EndsWith(expected, details.LunarDate);
        Assert.Empty(details.Festivals);
    }

    [Fact]
    public void CoincidingSolarAndLunarFestivalsBothAppear()
    {
        var details = CalendarDateDetails.ForDate(new(2020, 10, 1));
        Assert.Contains("国庆节", details.Festivals);
        Assert.Contains("中秋节", details.Festivals);
        Assert.Equal(details.Festivals.Count, details.Festivals.Distinct().Count());
    }

    [Fact]
    public void OrdinaryDaysShowLunarDateAndWeekdayWithoutEmptyEventLabels()
    {
        var details = CalendarDateDetails.ForDate(new(2026, 10, 29));
        Assert.Equal("丙午年 九月二十", details.LunarDate);
        Assert.Contains("2026年10月29日 星期四", details.ToolTipText);
        Assert.DoesNotContain("节日", details.ToolTipText);
        Assert.DoesNotContain("节气", details.ToolTipText);
    }

    [Theory]
    [InlineData(1900)]
    [InlineData(2101)]
    public void DatesOutsideTheSupportedConversionRangeStillHaveAUsableTooltip(int year)
    {
        var details = CalendarDateDetails.ForDate(new(year, 1, 1));
        Assert.Empty(details.LunarDate);
        Assert.Contains("暂无农历信息", details.ToolTipText);
    }

    [Fact]
    public void UpcomingEventsIncludeTodayBothKindsAndCalendarDayCountdowns()
    {
        var from = new DateOnly(2026, 2, 17);
        var events = CalendarDateDetails.GetUpcomingEvents(from);
        Assert.Equal(4, events.Count);
        Assert.Equal(2, events.Count(item => item.IsSolarTerm));
        Assert.Equal(2, events.Count(item => !item.IsSolarTerm));
        Assert.Equal(events.OrderBy(item => item.Date), events);
        Assert.Contains(events, item => item.Name == "春节" && item.Date == from && item.DaysRemaining == 0);
        Assert.Contains(events, item => item.Name == "雨水" && item.Date == new DateOnly(2026, 2, 18) && item.DaysRemaining == 1);
        foreach (var item in events) Assert.Equal(item.Date.DayNumber - from.DayNumber, item.DaysRemaining);
    }

    [Fact]
    public void UpcomingEventsCrossYearsAndRefreshTheirCountsTheNextDay()
    {
        var yearEnd = CalendarDateDetails.GetUpcomingEvents(new(2026, 12, 31));
        Assert.Contains(yearEnd, item => item.Name == "元旦节" && item.Date == new DateOnly(2027, 1, 1) && item.DaysRemaining == 1);
        Assert.All(yearEnd, item => Assert.True(item.DaysRemaining >= 0));
        var nextDay = CalendarDateDetails.GetUpcomingEvents(new(2027, 1, 1));
        Assert.Contains(nextDay, item => item.Name == "元旦节" && item.DaysRemaining == 0);
    }

    [Fact]
    public void CoincidingUpcomingFestivalsArePreservedAndYesterdayIsExcluded()
    {
        var today = new DateOnly(2020, 10, 1);
        var events = CalendarDateDetails.GetUpcomingEvents(today);
        Assert.Contains(events, item => item.Name == "国庆节" && item.DaysRemaining == 0);
        Assert.Contains(events, item => item.Name == "中秋节" && item.DaysRemaining == 0);
        var next = CalendarDateDetails.GetUpcomingEvents(today.AddDays(1));
        Assert.All(next, item => Assert.True(item.Date > today));
    }
}
