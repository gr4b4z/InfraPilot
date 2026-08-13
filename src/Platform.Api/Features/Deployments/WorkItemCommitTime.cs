using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Deployments;

/// <summary>
/// Resolves the lead-time clock start for a <c>work-item</c> reference: when the change carrying
/// the ticket entered trunk. Pure — takes the event's (or candidate's) full reference list and
/// answers from it alone, so both projections (<see cref="Models.DeployEventWorkItem"/> and
/// <c>PromotionWorkItem</c>) share one definition of "committed".
///
/// <para>Resolution order, mirroring how <c>WorkItemApprovalService</c> hydrates a ticket's
/// change set (work-item <c>Commits</c> → commit refs by <c>Key</c> → pull-request refs by
/// <c>Revision</c>):</para>
/// <list type="number">
///   <item>min <see cref="ReferenceDto.OccurredAt"/> over pull-request references matched via the
///         ticket's declared commit hashes (a PR's timestamp is its merge time — the moment the
///         change landed);</item>
///   <item>else min over the matched commit references themselves;</item>
///   <item>else, when the ticket declares no commits: the event's <b>single</b> pull-request
///         reference (real producers send 0 or 1 PR per deploy — one squashed PR is the whole
///         change), then its single commit reference. With several PRs/commits and no declared
///         hashes there is no defensible attribution, so the answer is null rather than a guess.</item>
/// </list>
/// </summary>
public static class WorkItemCommitTime
{
    public static DateTimeOffset? Resolve(ReferenceDto workItem, IReadOnlyList<ReferenceDto> allReferences)
    {
        var commits = allReferences
            .Where(r => string.Equals(r.Type, "commit", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pullRequests = allReferences
            .Where(r => string.Equals(r.Type, "pull-request", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var declared = (workItem.Commits ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (declared.Count > 0)
        {
            var viaPr = Min(pullRequests
                .Where(pr => pr.Revision is not null && declared.Contains(pr.Revision)));
            if (viaPr is not null) return viaPr;

            var viaCommit = Min(commits
                .Where(c => c.Key is not null && declared.Contains(c.Key)));
            return viaCommit;
        }

        // No declared hashes: only an unambiguous single change can be attributed.
        if (pullRequests.Count == 1 && pullRequests[0].OccurredAt is not null)
            return pullRequests[0].OccurredAt;
        if (commits.Count == 1 && commits[0].OccurredAt is not null)
            return commits[0].OccurredAt;
        return null;
    }

    private static DateTimeOffset? Min(IEnumerable<ReferenceDto> refs)
    {
        DateTimeOffset? min = null;
        foreach (var r in refs)
        {
            if (r.OccurredAt is not { } at) continue;
            if (min is null || at < min) min = at;
        }
        return min;
    }
}
