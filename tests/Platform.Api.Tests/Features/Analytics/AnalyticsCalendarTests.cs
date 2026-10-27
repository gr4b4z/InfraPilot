using Platform.Api.Features.Analytics;

namespace Platform.Api.Tests.Features.Analytics;

public class AnalyticsCalendarTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    [Fact]
    public void BucketStart_Month_IsFirstOfLocalMonth()
    {
        // 23:30 UTC on 31 July is already 1 August in Warsaw.
        var at = new DateTimeOffset(2026, 07, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal("2026-08-01", AnalyticsCalendar.BucketStart(at, "month", Warsaw));
        Assert.Equal("2026-07-01", AnalyticsCalendar.BucketStart(at, "month", TimeZoneInfo.Utc));
    }

    [Fact]
    public void BucketStart_Week_IsMonday()
    {
        var sunday = new DateTimeOffset(2026, 08, 09, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("2026-08-03", AnalyticsCalendar.BucketStart(sunday, "week", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Labels_AreHalfOpen_SoAWindowEndingOnTheFirstHasNoExtraMonth()
    {
        var from = new DateTimeOffset(2026, 07, 01, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 09, 01, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(["2026-07-01", "2026-08-01"], AnalyticsCalendar.Labels(from, to, "month", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Labels_Day_CoversEveryDayInWindow()
    {
        var from = new DateTimeOffset(2026, 08, 01, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 08, 04, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(["2026-08-01", "2026-08-02", "2026-08-03"], AnalyticsCalendar.Labels(from, to, "day", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Labels_EmptyWindow_IsEmpty()
    {
        var at = new DateTimeOffset(2026, 08, 01, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(AnalyticsCalendar.Labels(at, at, "week", TimeZoneInfo.Utc));
    }
}
