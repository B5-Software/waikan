using System.Globalization;
using Waikan.Models;

namespace Waikan.ViewModels;

public sealed class OccurrenceItemViewModel
{
    public OccurrenceItemViewModel(OccurrenceRecord record, DateTime now)
    {
        Id = record.Id;
        OccurredAt = record.LocalTime;
        TimeText = record.LocalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DateText = record.LocalTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        SourceText = record.SourceLabel;
        RelativeText = FormatRelative(record.LocalTime, now);
        IsToday = record.LocalTime.Date == now.Date;
    }

    public long Id { get; }

    public DateTimeOffset OccurredAt { get; }

    public string TimeText { get; }

    public string DateText { get; }

    public string SourceText { get; }

    public string RelativeText { get; }

    public bool IsToday { get; }

    private static string FormatRelative(DateTimeOffset time, DateTime now)
    {
        var span = now - time.LocalDateTime;
        if (span < TimeSpan.FromSeconds(30))
        {
            return "刚刚";
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)span.TotalSeconds)} 秒前";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟前";
        }

        if (span < TimeSpan.FromHours(6))
        {
            return $"{Math.Max(1, (int)span.TotalHours)} 小时前";
        }

        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}

public sealed class DayOccurrenceGroupViewModel
{
    public DayOccurrenceGroupViewModel(
        DateOnly date,
        IReadOnlyList<OccurrenceItemViewModel> items,
        bool isToday)
    {
        Date = date;
        Items = items;
        Count = items.Count;
        DateTitle = date.ToDateTime(TimeOnly.MinValue).ToString("M月d日", CultureInfo.CurrentCulture);
        Weekday = GetWeekday(date);
        CountText = $"{items.Count} 次";
        Subtitle = isToday ? $"今天 · {Weekday}" : Weekday;
        IsToday = isToday;
    }

    public DateOnly Date { get; }

    public IReadOnlyList<OccurrenceItemViewModel> Items { get; }

    public int Count { get; }

    public string DateTitle { get; }

    public string Weekday { get; }

    public string Subtitle { get; }

    public string CountText { get; }

    public bool IsToday { get; }

    private static string GetWeekday(DateOnly date)
    {
        return date.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            _ => "星期日",
        };
    }
}

public sealed class DailyBarViewModel
{
    public DailyBarViewModel(DateOnly date, int count, double height, bool isToday)
    {
        Date = date;
        Count = count;
        Height = height;
        IsToday = isToday;
        Label = date.ToString("M/d", CultureInfo.InvariantCulture);
        AccessibleLabel = $"{date:yyyy-MM-dd}，{count} 次";
    }

    public DateOnly Date { get; }

    public int Count { get; }

    public double Height { get; }

    public bool IsToday { get; }

    public string Label { get; }

    public string AccessibleLabel { get; }
}
