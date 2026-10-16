namespace Platform.Api.Features.Analytics;

/// <summary>
/// In-memory percentile/median math for the analytics endpoints. Durations are computed here
/// rather than in SQL on purpose: the SQLite test provider stores <see cref="DateTimeOffset"/>
/// as ticks and has no percentile functions, and the row counts involved (deploys/candidates
/// within a reporting window) are small enough that materialize-then-compute is the simpler
/// and portable answer.
/// </summary>
public static class Percentiles
{
    /// <summary>
    /// The p-th percentile (p in [0,1]) using linear interpolation between closest ranks —
    /// same convention as numpy's default. Null for an empty input.
    /// </summary>
    public static double? Compute(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        if (p <= 0) return values.Min();
        if (p >= 1) return values.Max();

        var sorted = values.OrderBy(v => v).ToArray();
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        var frac = rank - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    public static double? Median(IReadOnlyList<double> values) => Compute(values, 0.5);
}
