namespace Platform.Api.Features.Analytics;

/// <summary>
/// Bucket arithmetic shared by the analytics endpoints: a timestamp → the local-calendar bucket it
/// falls in, for <c>day</c>, <c>week</c> (Monday start) and <c>month</c>. Buckets are cut at local
/// midnights in the caller's timezone so a chart drawn in Warsaw doesn't put a 00:30 deploy on the
/// previous day. Labels are the bucket's first day as <c>yyyy-MM-dd</c>.
/// </summary>
public static class AnalyticsCalendar
{
    public static readonly string[] Buckets = ["day", "week", "month"];

    public static string BucketStart(DateTimeOffset at, string bucket, TimeZoneInfo tz)
        => LocalBucketDate(at, bucket, tz).ToString("yyyy-MM-dd");

    public static DateTime LocalBucketDate(DateTimeOffset at, string bucket, TimeZoneInfo tz)
    {
        var date = TimeZoneInfo.ConvertTime(at, tz).Date;
        return bucket switch
        {
            "week" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), // back to Monday
            "month" => new DateTime(date.Year, date.Month, 1),
            _ => date,
        };
    }

    public static DateTime Step(DateTime cursor, string bucket) => bucket switch
    {
        "week" => cursor.AddDays(7),
        "month" => cursor.AddMonths(1),
        _ => cursor.AddDays(1),
    };

    /// <summary>
    /// Every bucket label that intersects the half-open window [from, to), in order. The window end
    /// is exclusive, so a "last month" window ending at the 1st 00:00 does not grow a zero bucket for
    /// the month after.
    /// </summary>
    public static List<string> Labels(DateTimeOffset from, DateTimeOffset to, string bucket, TimeZoneInfo tz)
    {
        var labels = new List<string>();
        if (to <= from) return labels;
        var cursor = LocalBucketDate(from, bucket, tz);
        var endLocal = TimeZoneInfo.ConvertTime(to.AddTicks(-1), tz).Date;
        while (cursor <= endLocal)
        {
            labels.Add(cursor.ToString("yyyy-MM-dd"));
            cursor = Step(cursor, bucket);
        }
        return labels;
    }
}
