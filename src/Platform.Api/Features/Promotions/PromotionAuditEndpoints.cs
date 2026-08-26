using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Users;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// The promotions activity feed — every recorded action on a promotion, newest first, mounted at
/// <c>/api/promotions/audit</c>.
///
/// <para><b>Why this exists next to <c>/api/audit</c>.</b> The generic audit endpoint answers
/// "what happened to entity X" and is admin-only. The questions people actually ask about
/// promotions are the other way round — "what was approved today?", "what did we let into prod
/// last week, and who signed it off?", "what promotions were created today?" — which need the
/// promotion's own context (product, service, environment, version) on every row and need to be
/// answerable by the same people who do the approving, not just admins. A raw audit row carries
/// only an entity id and a loosely-shaped payload, so this endpoint joins the candidate back on and
/// returns rows that read as sentences.</para>
///
/// <para><b>Candidate-anchored.</b> Rows are INNER JOINed to a promotion candidate the caller is
/// allowed to see, which is what applies hidden products and retired services to this page for
/// free — the same visibility rules the promotions list runs under. Two consequences worth knowing:
/// audit rows the promotions module writes against other entities (the legacy per-row
/// <c>work-item.approved</c> / <c>work-item.blocked</c> duplicates of a sign-off, and
/// <c>work-item.comment.*</c>) are not in this feed, and neither is a work-item sign-off recorded
/// with no live candidate. The first is a feature: a sign-off writes both a
/// <c>WorkItemApproval</c>-scoped row and a candidate-scoped <c>promotion.ticket.*</c> row, and one
/// action should be one line in a feed.</para>
///
/// <para><b>No source IP.</b> The admin audit endpoint returns it; this one deliberately doesn't.
/// The feed is readable by every approver, and "who approved this" does not need to come with a
/// colleague's IP address attached.</para>
/// </summary>
public static class PromotionAuditEndpoints
{
    /// <summary>Audit rows written by the promotions feature all carry this module name.</summary>
    private const string Module = "promotions";

    /// <summary>The gate opened (or was forced open) — the promotion may now deploy.</summary>
    private const string ApprovedAction = "promotion.approved";

    /// <summary>One signature towards a gate, carrying the human who gave it.</summary>
    private const string ApprovalRecordedAction = "promotion.approval.recorded";

    public static RouteGroupBuilder MapPromotionAuditEndpoints(this RouteGroupBuilder group)
    {
        // The feed itself: a page of rows, plus the facet counts the UI's tabs and dropdowns are
        // built from. One request backs the whole page.
        group.MapGet("/", async (
            PlatformDbContext db,
            UserPreferencesService prefs,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? days,
            string? action,
            string? category,
            string? actor,
            string? product,
            string? service,
            string? targetEnv,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            var (rangeFrom, rangeTo) = ResolveRange(from, to, days);

            // Which candidates this caller can see at all. Every filter that describes a promotion
            // rather than an action is applied here, so it narrows the join and therefore the counts
            // too — a product filter has to shrink the tab badges, not just the visible rows.
            var candidates = db.PromotionCandidates.AsNoTracking().AsQueryable();
            var hidden = await prefs.GetHiddenProductsAsync(ct);
            if (hidden.Count > 0) candidates = candidates.Where(c => !hidden.Contains(c.Product));
            candidates = candidates.ExcludingDeletedServices(db);

            if (!string.IsNullOrWhiteSpace(product))
                candidates = candidates.Where(c => c.Product == product);
            if (!string.IsNullOrWhiteSpace(targetEnv))
                candidates = candidates.Where(c => c.TargetEnv == targetEnv);
            // Substring, case-insensitive — same as the promotions list, where people type the
            // fragment of a service name they remember rather than the whole thing.
            if (!string.IsNullOrWhiteSpace(service))
            {
                var needle = service.Trim().ToLower();
                candidates = candidates.Where(c => c.Service.ToLower().Contains(needle));
            }

            var inWindow = db.AuditLog.AsNoTracking()
                .Where(a => a.Module == Module && a.EntityId != null)
                .Join(candidates, a => a.EntityId, c => (Guid?)c.Id, (a, c) => new { Audit = a, Candidate = c });

            if (rangeFrom.HasValue)
                inWindow = inWindow.Where(x => x.Audit.Timestamp >= rangeFrom.Value);
            if (rangeTo.HasValue)
                inWindow = inWindow.Where(x => x.Audit.Timestamp <= rangeTo.Value);

            // Actor: an id match for a link built from a row, a name substring for a human typing
            // "kowalski" into the box. Both, because the dropdown sends an id and the API is also
            // called by hand.
            //
            // Kept as a separate step from `inWindow` so the actor facet below can be counted without
            // it — see the facet comment.
            var actorNeedle = (actor ?? "").Trim();
            var joined = inWindow;
            if (actorNeedle.Length > 0)
            {
                var lowered = actorNeedle.ToLower();
                joined = joined.Where(x =>
                    x.Audit.ActorId == actorNeedle || x.Audit.ActorName.ToLower().Contains(lowered));
            }

            // Facets never count their own filter — that's what makes them a description of what
            // selecting something *would* show rather than of what is already selected. So the action
            // counts come from before the action/category filter, and the actor counts from before the
            // actor filter: a page filtered to one person must still offer everybody else, or picking a
            // name is a one-way door out of the view.
            var actionFacets = await joined
                .GroupBy(x => x.Audit.Action)
                .Select(g => new { Action = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var actorRows = await inWindow
                .GroupBy(x => new { x.Audit.ActorId, x.Audit.ActorName, x.Audit.ActorType })
                .Select(g => new
                {
                    g.Key.ActorId,
                    g.Key.ActorName,
                    g.Key.ActorType,
                    Count = g.Count(),
                })
                .ToListAsync(ct);

            // Collapsed to one entry per actor **id**, because the id is what the filter matches on and
            // the same id legitimately appears under more than one name: the system writes "System" for
            // an auto-approval and "System (gate satisfied)" for a gate it opened. Two dropdown entries
            // that filter to exactly the same rows is a dropdown that looks broken, so the busiest
            // spelling represents the actor (ties broken by name, so the answer is stable).
            var actorFacets = actorRows
                .GroupBy(a => a.ActorId, StringComparer.Ordinal)
                .Select(g =>
                {
                    var primary = g
                        .OrderByDescending(a => a.Count)
                        .ThenBy(a => a.ActorName, StringComparer.Ordinal)
                        .First();
                    return new
                    {
                        Id = g.Key,
                        Name = primary.ActorName,
                        Type = primary.ActorType,
                        Count = g.Sum(a => a.Count),
                    };
                })
                .ToList();

            // `category` is a named set of actions, nothing more (see PromotionAuditCategories), so
            // both filters collapse into one action list and the total falls out of the facet counts
            // above — no second COUNT query, and no chance of it disagreeing with the tab badge.
            var requested = ResolveActionFilter(action, category, actionFacets.Select(f => f.Action));
            var selected = requested is null
                ? actionFacets
                : actionFacets.Where(f => requested.Contains(f.Action)).ToList();
            var total = selected.Sum(f => f.Count);

            // The names to query with are the ones the facets came back with, not the ones the caller
            // typed: the SQL `IN (…)` runs under the database's collation (case-sensitive on Postgres,
            // not on SQL Server) while the match above is deliberately case-insensitive. Resolving
            // through the facets is what stops the page and its own row count from disagreeing.
            var filterActions = requested is null ? null : selected.Select(f => f.Action).ToList();

            var resolvedPage = Math.Max(page ?? 1, 1);
            var resolvedPageSize = Math.Clamp(pageSize ?? 50, 1, 200);

            var rows = joined;
            if (filterActions is not null) rows = rows.Where(x => filterActions.Contains(x.Audit.Action));

            var entries = await rows
                // Id as the tiebreak: several rows commonly share a timestamp (an approval that
                // satisfies the gate writes two), and without a deterministic order the boundary
                // between two pages can drop or repeat one of them.
                .OrderByDescending(x => x.Audit.Timestamp)
                .ThenBy(x => x.Audit.Id)
                .Skip((resolvedPage - 1) * resolvedPageSize)
                .Take(resolvedPageSize)
                .Select(x => new
                {
                    x.Audit.Id,
                    x.Audit.Timestamp,
                    x.Audit.CorrelationId,
                    x.Audit.Action,
                    x.Audit.ActorId,
                    x.Audit.ActorName,
                    x.Audit.ActorType,
                    x.Audit.AfterState,
                    x.Audit.Metadata,
                    CandidateId = x.Candidate.Id,
                    x.Candidate.Product,
                    x.Candidate.Service,
                    x.Candidate.SourceEnv,
                    x.Candidate.TargetEnv,
                    x.Candidate.Version,
                    CandidateStatus = x.Candidate.Status,
                })
                .ToListAsync(ct);

            // "Who approved this?" is not answerable from a promotion.approved row on its own. When a
            // gate opens, that row is written by the evaluator with the system as its actor; the human
            // who tipped it is on the promotion.approval.recorded row that triggered the re-evaluation.
            // Both are written inside the same HTTP request, so they share a correlation id — and the
            // trail is where the answer has to come from, not the candidate's current approval rows:
            // cancelling an approval deletes those, and a historical line must keep saying what
            // happened. One extra query per page, on an indexed column.
            var gateOpened = entries
                .Where(e => e.Action == ApprovedAction)
                .Select(e => e.CorrelationId)
                .Distinct()
                .ToList();

            var approversByCorrelation = gateOpened.Count == 0
                ? new Dictionary<Guid, List<AuditActor>>()
                : (await db.AuditLog.AsNoTracking()
                        .Where(a => a.Action == ApprovalRecordedAction && gateOpened.Contains(a.CorrelationId))
                        .Select(a => new { a.CorrelationId, a.ActorId, a.ActorName })
                        .ToListAsync(ct))
                    .GroupBy(a => a.CorrelationId)
                    .ToDictionary(
                        g => g.Key,
                        // Distinct because a gate needing several signatures can be satisfied by a
                        // batch approval, which records them all under the one correlation.
                        g => g.Select(a => new AuditActor(a.ActorId, a.ActorName)).Distinct().ToList());

            return Results.Ok(new
            {
                entries = entries.Select(e =>
                {
                    // The action's own payload. Every promotions audit call passes its object as the
                    // after-state (positionally, ahead of the logger's separate metadata slot), so
                    // that is where the interesting fields are; the metadata slot is read as a
                    // fallback so a future call that uses it isn't silently blank here.
                    var details = ParseJson(e.AfterState) ?? ParseJson(e.Metadata);
                    return new
                    {
                        e.Id,
                        e.Timestamp,
                        e.CorrelationId,
                        e.Action,
                        category = PromotionAuditCategories.For(e.Action),
                        e.ActorId,
                        e.ActorName,
                        e.ActorType,
                        e.CandidateId,
                        e.Product,
                        e.Service,
                        e.SourceEnv,
                        e.TargetEnv,
                        e.Version,
                        candidateStatus = e.CandidateStatus.ToString(),
                        // Lifted out of the payload because they're the part a reader wants in the row
                        // itself: the comment left with an approval or rejection, the reason given for
                        // a bypass, and which ticket a work-item action was about.
                        comment = Str(details, "comment"),
                        reason = Str(details, "reason"),
                        workItemKey = Str(details, "workItemKey"),
                        role = Str(details, "role"),
                        referenceKey = Str(details, "referenceKey"),
                        trigger = Str(details, "trigger"),
                        // Only on a gate-opening row, and only when a human tipped it — an
                        // auto-approved promotion has nobody to name (see above).
                        approvedBy = e.Action == ApprovedAction
                            && approversByCorrelation.TryGetValue(e.CorrelationId, out var by)
                                ? by
                                : null,
                        // The whole payload as well — actions differ in what they record, and the UI's
                        // details expansion shows whatever a given action happened to carry.
                        details,
                    };
                }),
                total,
                page = resolvedPage,
                pageSize = resolvedPageSize,
                range = new { from = rangeFrom, to = rangeTo },
                // Per-action counts, with each action's category so the client can group them
                // without duplicating the mapping.
                actions = actionFacets
                    .OrderByDescending(f => f.Count)
                    .Select(f => new
                    {
                        f.Action,
                        category = PromotionAuditCategories.For(f.Action),
                        f.Count,
                    }),
                actors = actorFacets
                    .OrderByDescending(a => a.Count)
                    .ThenBy(a => a.Name, StringComparer.Ordinal)
                    .Select(a => new { id = a.Id, name = a.Name, type = a.Type, a.Count }),
            });
        });

        return group;
    }

    /// <summary>
    /// Resolves the window to query. Explicit <paramref name="from"/>/<paramref name="to"/> win;
    /// <paramref name="days"/> is the convenience form for a caller writing the URL by hand
    /// (<c>?days=7</c>). With none of them the feed is unbounded — "all time", which the UI offers
    /// and which is the only honest default for an audit trail.
    ///
    /// <para>Note what is <b>not</b> here: a "today" option. A calendar day belongs to whoever is
    /// reading, so the client resolves its own midnight and sends an absolute instant.</para>
    /// </summary>
    private static (DateTimeOffset? From, DateTimeOffset? To) ResolveRange(
        DateTimeOffset? from, DateTimeOffset? to, int? days)
    {
        if (from.HasValue || !days.HasValue || days.Value <= 0) return (from, to);
        return (DateTimeOffset.UtcNow.AddDays(-Math.Min(days.Value, 3650)), to);
    }

    /// <summary>
    /// The set of actions to return, from an explicit action list, a category list, or both — null
    /// when neither is given, meaning "every action". Both parameters take a comma-separated list so
    /// a link can carry more than one, and unknown names simply match nothing rather than erroring:
    /// these values are typed and hand-edited, and an action that no longer exists is a legitimate
    /// thing to have bookmarked.
    ///
    /// <para><paramref name="present"/> is the actions the current query actually contains, which is
    /// how the <c>other</c> category — defined as "everything the map doesn't know", so not expressible
    /// as a fixed list — becomes selectable. Resolving it against what is present means the feed's
    /// "everything else" tab holds a newly-added audit action without anyone editing this file.</para>
    /// </summary>
    private static HashSet<string>? ResolveActionFilter(
        string? action, string? category, IEnumerable<string> present)
    {
        var actions = Split(action);
        var categories = Split(category);
        if (actions.Count == 0 && categories.Count == 0) return null;

        var result = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
        foreach (var name in categories)
        {
            if (string.Equals(name, PromotionAuditCategories.Other, StringComparison.OrdinalIgnoreCase))
            {
                result.UnionWith(present.Where(a => PromotionAuditCategories.For(a) == PromotionAuditCategories.Other));
                continue;
            }
            result.UnionWith(PromotionAuditCategories.ActionsIn(name));
        }
        return result;
    }

    private static List<string> Split(string? raw) =>
        (raw ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>
    /// An audit payload as a JSON value rather than the string it is stored as, so the client doesn't
    /// have to parse a string out of a JSON document. Unparseable JSON degrades to null: a malformed
    /// blob must not cost the reader the row it belongs to.
    /// </summary>
    private static JsonElement? ParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a string property out of an audit payload, case-insensitively. The casing is genuinely
    /// inconsistent across actions — the blobs are serialised from anonymous types, so a property
    /// written as <c>candidate.Product</c> lands as <c>Product</c> and one written as
    /// <c>product = prod</c> lands as <c>product</c> — and normalising that at read time is cheaper
    /// than rewriting history.
    /// </summary>
    private static string? Str(JsonElement? payload, string property)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } obj) return null;

        if (obj.TryGetProperty(property, out var direct))
            return direct.ValueKind == JsonValueKind.String ? direct.GetString() : null;

        foreach (var candidate in obj.EnumerateObject())
        {
            if (!string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)) continue;
            return candidate.Value.ValueKind == JsonValueKind.String ? candidate.Value.GetString() : null;
        }
        return null;
    }
}

/// <summary>
/// A person (or the system) named on an audit row. Only used for the approvers lifted onto a
/// gate-opening row, where the row's own actor is the evaluator rather than anybody who decided
/// anything.
/// </summary>
public record AuditActor(string Id, string Name);

/// <summary>
/// Groups the promotions module's audit actions into the handful of kinds a reader thinks in.
///
/// <para>This lives on the server because it is domain knowledge, not copy: which actions count as
/// "an approval" is the same fact for the feed's tabs, for a saved link, and for anything else that
/// later asks the API "what was approved". The wording each category gets is the client's business.</para>
///
/// <para>An action this map hasn't heard of is <c>other</c> rather than an error, so a new audit
/// action added elsewhere in the module shows up in the feed the day it ships — miscategorised at
/// worst, never missing.</para>
/// </summary>
public static class PromotionAuditCategories
{
    /// <summary>A promotion cleared its gate (or was forced through) and may now deploy.</summary>
    public const string Approved = "approved";
    /// <summary>One signature towards a gate — not the gate opening.</summary>
    public const string ApprovalStep = "approval-step";
    /// <summary>Someone turned a promotion down.</summary>
    public const string Rejected = "rejected";
    /// <summary>An approval taken back before the promotion was dispatched.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>A promotion came into existence.</summary>
    public const string Created = "created";
    /// <summary>Its change set or its policy snapshot moved under it.</summary>
    public const string Updated = "updated";
    /// <summary>It landed in the target environment.</summary>
    public const string Deployed = "deployed";
    /// <summary>A ticket-level decision (sign-off, issue, block) or a reset of those.</summary>
    public const string WorkItem = "work-item";
    /// <summary>Discussion on the promotion.</summary>
    public const string Comment = "comment";
    /// <summary>Who is attached to the promotion or to one of its work items.</summary>
    public const string People = "people";
    /// <summary>An action written by the module that this map doesn't know yet.</summary>
    public const string Other = "other";

    private static readonly Dictionary<string, string> ByAction = new(StringComparer.OrdinalIgnoreCase)
    {
        // A bypass is an approval that skipped the gate. It belongs with the approvals because that
        // is where someone asking "what got approved for prod?" will look for it — the row itself
        // says it was forced, and carries the reason.
        ["promotion.approved"] = Approved,
        ["promotion.bypassed"] = Approved,
        ["promotion.approval.recorded"] = ApprovalStep,
        ["promotion.rejected"] = Rejected,
        ["promotion.approval.cancelled"] = Cancelled,
        ["promotion.candidate.created"] = Created,
        ["promotion.candidate.updated"] = Updated,
        ["promotion.policy.reapplied"] = Updated,
        ["promotion.deployed"] = Deployed,
        ["promotion.ticket.approved"] = WorkItem,
        ["promotion.ticket.issue-raised"] = WorkItem,
        ["promotion.ticket.blocked"] = WorkItem,
        ["work-item.decisions.reset"] = WorkItem,
        ["promotion.comment.added"] = Comment,
        ["promotion.participant.upserted"] = People,
        ["promotion.participant.removed"] = People,
        ["promotion.reference.participant.upserted"] = People,
        ["promotion.reference.participant.removed"] = People,
    };

    /// <summary>The category an action belongs to; <see cref="Other"/> for anything unmapped.</summary>
    public static string For(string action) =>
        ByAction.TryGetValue(action, out var category) ? category : Other;

    /// <summary>
    /// The actions in a category, for turning a category filter into an action filter. An unknown
    /// category yields nothing — see <c>ResolveActionFilter</c> for why that beats a 400.
    ///
    /// <para><see cref="Other"/> is the one category this cannot answer: it is defined as "everything
    /// not listed here", which is not a list of names. <c>ResolveActionFilter</c> resolves it against
    /// the actions the query actually contains instead.</para>
    /// </summary>
    public static IEnumerable<string> ActionsIn(string category) =>
        ByAction.Where(kv => string.Equals(kv.Value, category, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key);
}
