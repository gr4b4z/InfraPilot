namespace Platform.Api.Features.Promotions;

/// <summary>
/// Orders two version strings when — and only when — it can do so confidently.
///
/// <para>Used by the overtake rule (see <c>PromotionService.SupersedeOvertakenByDeployAsync</c>) to
/// answer one question: is the version that just landed in an environment <b>newer</b> than the one an
/// open promotion is still waiting to deploy? Getting that wrong in the permissive direction would
/// close a promotion that is legitimately queued behind an unrelated hotfix, so the comparison is
/// deliberately conservative: anything it cannot parse as an ordered numeric sequence is reported as
/// un-orderable and the caller leaves the promotion alone.</para>
///
/// <para>The shape it understands is a dot-separated numeric prefix with an optional suffix —
/// <c>6.0.4-g03ce8515</c>, <c>1.94.0</c>, <c>6.0.1337-gbd0a274f</c>. The suffix is ignored: it
/// identifies the build, not its ordering. A bare sha, a branch name or a single number is refused,
/// the last of these because <c>7</c> vs <c>7.1</c> is more likely a different versioning scheme than
/// a comparable pair.</para>
/// </summary>
public static class PromotionVersionOrder
{
    /// <summary>
    /// Compares the numeric prefixes of two versions. Returns <c>false</c> when either side cannot be
    /// ordered, in which case <paramref name="comparison"/> is meaningless and callers must not act.
    /// On <c>true</c>, <paramref name="comparison"/> follows <see cref="IComparable"/> convention:
    /// negative when <paramref name="left"/> is older.
    /// </summary>
    public static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;

        var l = ParseNumericPrefix(left);
        var r = ParseNumericPrefix(right);
        if (l is null || r is null) return false;

        // Missing trailing components count as zero, so 6.0 and 6.0.0 are the same version.
        var length = Math.Max(l.Count, r.Count);
        for (var i = 0; i < length; i++)
        {
            var lv = i < l.Count ? l[i] : 0;
            var rv = i < r.Count ? r[i] : 0;
            if (lv != rv)
            {
                comparison = lv.CompareTo(rv);
                return true;
            }
        }

        return true; // equal
    }

    /// <summary>True when <paramref name="candidate"/> is confidently newer than <paramref name="other"/>.</summary>
    public static bool IsNewerThan(string? candidate, string? other) =>
        TryCompare(candidate, other, out var cmp) && cmp > 0;

    private static List<int>? ParseNumericPrefix(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var span = version.Trim();

        // A leading "v" is decoration — "v1.2.3" and "1.2.3" are the same version, and both
        // conventions turn up across producers.
        if (span.Length > 1 && (span[0] == 'v' || span[0] == 'V') && char.IsAsciiDigit(span[1]))
            span = span[1..];

        // Everything from the first '-' or '+' on is build identity, not ordering.
        var cut = span.IndexOfAny(['-', '+']);
        if (cut >= 0) span = span[..cut];

        var parts = span.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // Refuse a single component: "7" vs "7.1" is more likely two schemes than two versions.
        if (parts.Length < 2) return null;

        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var value) || value < 0) return null;
            numbers.Add(value);
        }
        return numbers;
    }
}
