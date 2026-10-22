using Waikan.Infrastructure;

namespace Waikan.Tests;

public sealed class TimeRangeParserTests
{
    [Theory]
    [InlineData("2026-10-22 00:00:00", "2026-10-22 23:59:59")]
    [InlineData("2026-10-22 00:00", "2026-10-22 23:59")]
    [InlineData("2026/10/22 8:01:02", "2026/10/22 23:59:59")]
    public void Parses_Supported_Formats(string start, string end)
    {
        Assert.True(TimeRangeParser.TryParseRange(start, end, out var range, out var error), error);
        Assert.True(range.From < range.To);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(range.From.DateTime), range.From.Offset);
        Assert.True(range.To > range.From);
    }

    [Fact]
    public void End_Is_Inclusive_Of_The_Final_Second()
    {
        Assert.True(TimeRangeParser.TryParseRange(
            "2026-10-22 23:00:00",
            "2026-10-22 23:59:59",
            out var range,
            out _));
        Assert.Equal(59, range.To.Second);
        Assert.True(range.To.Millisecond >= 999);

        Assert.True(TimeRangeParser.TryParseRange(
            "2026-10-22 23:00",
            "2026-10-22 23:59",
            out var minuteRange,
            out _));
        Assert.Equal(59, minuteRange.To.Second);
        Assert.True(minuteRange.To.Millisecond >= 999);
    }

    [Fact]
    public void Rejects_End_Before_Start()
    {
        Assert.False(TimeRangeParser.TryParseRange(
            "2026-10-22 12:00:00",
            "2026-10-22 11:00:00",
            out _,
            out var error));
        Assert.Contains("结束", error);
    }

    [Theory]
    [InlineData("", "2026-10-22 11:00:00")]
    [InlineData("2026-10-22 11:00:00", "not a date")]
    public void Rejects_Missing_Or_Invalid_Input(string start, string end)
    {
        Assert.False(TimeRangeParser.TryParseRange(start, end, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Rejects_Intervals_Longer_Than_Ten_Years()
    {
        Assert.False(TimeRangeParser.TryParseRange(
            "2000-01-01 00:00:00",
            "2026-10-22 00:00:00",
            out _,
            out var error));
        Assert.Contains("十年", error);
    }
}
