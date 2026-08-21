using System.Text.RegularExpressions;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Deployments;

/// <summary>
/// Resolves the two display lines for a <c>work-item</c> reference: the tracker's own name of the
/// item on top, the messages of every commit behind it underneath. Pure — answers from the
/// reference list alone, so both projections (<see cref="Models.DeployEventWorkItem"/> and
/// <c>PromotionWorkItem</c>) and the promotion page's reference rows name a ticket identically.
///
/// <para>One ticket routinely rides several commits, so a commit subject cannot be the item's name:
/// with three commits there are three subjects and no reason to prefer one of them. The tracker's
/// summary is the one stable name a reviewer recognises, and the commit messages — all of them —
/// are what actually changed, which is a list and belongs on the second line.</para>
///
/// <para>Producers describe the same thing in one of two shapes, both handled here:</para>
/// <list type="bullet">
///   <item><b>Title only</b> (mpt-release, and marketplace since the flip) — the tracker summary,
///         with <see cref="ReferenceDto.Commits"/> carrying the hashes behind it.</item>
///   <item><b>Title + SubTitle</b> (older marketplace payloads, still in storage) — the commit
///         subject on <c>Title</c> and the tracker summary on <c>SubTitle</c>. Whenever a producer
///         sends both, <c>SubTitle</c> is the tracker's own summary by contract, which makes it the
///         title here.</item>
/// </list>
/// </summary>
public static class WorkItemDisplay
{
    /// <summary>Both projections cap the column at 500 characters (see <c>PlatformDbContext</c>).</summary>
    public const int MaxSubTitleLength = 500;

    /// <summary>What separates one commit message from the next on the subtitle line.</summary>
    public const string CommitSeparator = " • ";

    /// <summary>
    /// The bookkeeping a squash merge prepends to the commit subject it kept — Azure DevOps writes
    /// "Merged PR 150156: " in front of the branch's own message. The PR number is already a
    /// reference of its own on the item, so on this line it is noise in front of the sentence a
    /// reviewer is trying to read. Stripped from the subtitle only: a `commit` reference's title is
    /// the commit's real subject and stays verbatim wherever it is shown as one.
    /// </summary>
    private static readonly Regex MergeNoisePrefix = new(
        @"^\s*Merged PR \d+:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The title / subtitle pair to display for <paramref name="workItem"/>.
    /// <paramref name="trackerTitleFallback"/> is the enrichment label (a Jira summary fetched after
    /// ingest) used when the reference itself carries no name at all.
    /// </summary>
    public static (string? Title, string? SubTitle) Resolve(
        ReferenceDto workItem,
        IReadOnlyList<ReferenceDto> allReferences,
        string? trackerTitleFallback = null)
    {
        var title = FirstNonBlank(workItem.SubTitle, workItem.Title, trackerTitleFallback);

        // The commit messages, when the ticket declares hashes we can hydrate. Falling back to the
        // producer's own commit subject — which is what Title holds in the two-line shape — keeps a
        // trimmed payload (one whose `commit` references were dropped) from losing the line entirely.
        var subTitle = CommitMessages(workItem, allReferences)
            ?? (string.IsNullOrWhiteSpace(workItem.SubTitle) ? null : StripMergeNoise(workItem.Title));

        // A second line repeating the first says nothing worth the space — the single-commit case,
        // where the ticket and its one commit are named the same thing.
        if (string.IsNullOrWhiteSpace(subTitle) || string.Equals(subTitle, title, StringComparison.Ordinal))
            return (title, null);

        return (title, Truncate(subTitle!));
    }

    /// <summary>
    /// Copy of <paramref name="references"/> with every <c>work-item</c> reference's title/subtitle
    /// replaced by <see cref="Resolve"/>'s answer, for read paths that hand the raw reference list to
    /// the client (the promotion list and detail endpoints). Display only: what was ingested stays in
    /// storage verbatim, so a later re-read can still tell the two producer shapes apart.
    /// </summary>
    public static List<ReferenceDto> ApplyToReferences(IReadOnlyList<ReferenceDto> references)
        => references
            .Select(r =>
            {
                if (!string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase)) return r;
                var (title, subTitle) = Resolve(r, references);
                return r with { Title = title, SubTitle = subTitle };
            })
            .ToList();

    /// <summary>
    /// The messages of the commits the ticket declares, in declared order, deduped on both hash and
    /// message (a squash and its original can carry the same subject) and joined with
    /// <see cref="CommitSeparator"/>. Null when the ticket declares no hashes or none of them
    /// resolves to a <c>commit</c> reference carrying a message — matching
    /// <c>WorkItemApprovalService.ResolveChangeSet</c>, which hydrates the same linkage.
    /// </summary>
    private static string? CommitMessages(ReferenceDto workItem, IReadOnlyList<ReferenceDto> allReferences)
    {
        if (workItem.Commits is not { Count: > 0 } declared) return null;

        var messages = new List<string>();
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in declared)
        {
            var hash = (raw ?? "").Trim();
            if (hash.Length == 0 || !seenHashes.Add(hash)) continue;

            var message = allReferences.FirstOrDefault(r =>
                string.Equals(r.Type, "commit", StringComparison.OrdinalIgnoreCase)
                && GitHash.Matches(r.Key, hash))?.Title;

            var trimmed = StripMergeNoise(message);
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (seenMessages.Add(trimmed!)) messages.Add(trimmed!);
        }

        return messages.Count == 0 ? null : string.Join(CommitSeparator, messages);
    }

    /// <summary>
    /// Drops the squash-merge prefix (see <see cref="MergeNoisePrefix"/>) and surrounding whitespace.
    /// Returns the input unchanged when there is nothing to drop, and leaves a subject that is
    /// <i>only</i> the prefix alone rather than reducing it to nothing.
    /// </summary>
    private static string? StripMergeNoise(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        var stripped = MergeNoisePrefix.Replace(message.Trim(), "").Trim();
        return stripped.Length > 0 ? stripped : message.Trim();
    }

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private static string Truncate(string value)
        => value.Length <= MaxSubTitleLength
            ? value
            : value[..(MaxSubTitleLength - 1)].TrimEnd() + "…";
}
