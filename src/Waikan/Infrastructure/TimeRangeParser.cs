using System.Globalization;

namespace Waikan.Infrastructure;

public readonly record struct LocalTimeRange(DateTimeOffset From, DateTimeOffset To);

public static class TimeRangeParser
{
    private static readonly string[] Formats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy/M/d H:mm:ss",
        "yyyy/M/d H:mm",
        "yyyy.MM.dd HH:mm:ss",
        "yyyy.MM.dd HH:mm",
    };

    public static bool TryParseRange(
        string? startText,
        string? endText,
        out LocalTimeRange range,
        out string error)
    {
        range = default;
        error = string.Empty;

        if (!TryParseDateTime(startText, out var start))
        {
            error = "开始时间格式无效，请使用 yyyy-MM-dd HH:mm:ss。";
            return false;
        }

        if (!TryParseDateTime(endText, out var end))
        {
            error = "结束时间格式无效，请使用 yyyy-MM-dd HH:mm:ss。";
            return false;
        }

        if (end < start)
        {
            error = "结束时间不能早于开始时间。";
            return false;
        }

        if (end - start > TimeSpan.FromDays(3660))
        {
            error = "时间区间过长，请缩小到十年以内。";
            return false;
        }

        // The end is inclusive. If the user supplied whole seconds, include the
        // whole final second so records at 23:59:59.6 are not silently omitted.
        var inclusiveEnd = ToDateTimeOffset(end);
        inclusiveEnd = HasExplicitSeconds(endText)
            ? inclusiveEnd.AddMilliseconds(999.999)
            : inclusiveEnd.AddSeconds(59.999);

        range = new LocalTimeRange(ToDateTimeOffset(start), inclusiveEnd);
        return true;
    }

    private static bool HasExplicitSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var timePart = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return timePart.Count(character => character == ':') >= 2;
    }

    public static bool TryParseDateTime(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (DateTime.TryParseExact(
                text,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out result))
        {
            return true;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out result);
    }

    public static DateTimeOffset ToDateTimeOffset(DateTime localDateTime)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }
}
