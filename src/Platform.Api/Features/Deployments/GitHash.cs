namespace Platform.Api.Features.Deployments;

/// <summary>
/// Git hash comparison, case-insensitive, tolerating abbreviation on either side: commit messages
/// and build metadata routinely carry a short SHA where the reference carries the full one (the
/// version string in the sample payload does exactly this). A prefix match needs at least
/// <see cref="MinAbbreviatedLength"/> characters so a stray short token can't match everything.
///
/// <para>Shared because two readers resolve the same ticket → commit linkage and must agree about
/// it: <see cref="WorkItemDisplay"/> (which commit messages name the work item) and
/// <c>WorkItemApprovalService.ResolveChangeSet</c> (which commits and PRs the detail page lists).</para>
/// </summary>
public static class GitHash
{
    public const int MinAbbreviatedLength = 7;

    public static bool Matches(string? a, string? b)
    {
        var left = (a ?? "").Trim();
        var right = (b ?? "").Trim();
        if (left.Length == 0 || right.Length == 0) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;

        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length <= right.Length ? right : left;
        return shorter.Length >= MinAbbreviatedLength
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }
}
