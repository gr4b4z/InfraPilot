using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.ReleaseNotes;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Settings;

/// <summary>
/// Folds several environment names that mean the same environment into one.
///
/// <para>Aliases (<see cref="EnvironmentAliasMap"/>) are forward-only: listing "productions" as an
/// alias of "prod" fixes what arrives next and leaves the years that arrived before it filed under
/// the old name. That is the safe default but not the end state — until the history follows, the
/// deployment matrix still shows two production columns, analytics still counts two pipelines, and
/// the promotion edges into each are separate. This is the deliberate second step, preview-then-apply
/// like the other maintenance repairs, so an admin sees the row counts before anything moves.</para>
///
/// <para><b>The settings row moves with the data.</b> An apply that records aliases also removes the
/// merged-away environments from the configured list, because the two halves cannot be done
/// separately without passing through a state the platform refuses to hold: an alias that is also
/// an environment of its own is ambiguous, and <see cref="EnvironmentAliasValidator"/> rejects it.
/// One operation, one consistent outcome.</para>
///
/// <para><b>What is deliberately left behind.</b> Rows the merge would collapse onto an existing
/// row protected by a unique index — a promotion policy for an edge the target already has, a
/// ticket sign-off the same approver already gave against the target, a rollback policy for the
/// target product/env — stay where they are and are counted as conflicts. The target's row is the
/// one that governs from now on, and overwriting it with the duplicate's contents would silently
/// replace live configuration. Promotion rows whose two ends would become the same environment
/// (a "prod → productions" edge) also stay, counted as <c>DegenerateEdges</c>: a promotion from an
/// environment to itself is not a thing the platform can represent, and which of the two the admin
/// meant is not recoverable here.</para>
/// </summary>
public class EnvironmentMergeService
{
    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;
    private readonly ILogger<EnvironmentMergeService> _logger;

    public EnvironmentMergeService(
        PlatformDbContext db, AppSettingsService settings, ILogger<EnvironmentMergeService> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>What to merge into what.</summary>
    /// <param name="Into">The canonical environment key everything lands on.</param>
    /// <param name="From">The environment names being folded in. Entries equal to
    /// <paramref name="Into"/> are dropped — merging an environment into itself is a no-op, not an
    /// error, so a UI that pre-selects the target needs no special case.</param>
    /// <param name="RecordAliases">
    /// Whether to record the merged names as aliases of the target, so new traffic under the old
    /// names keeps landing on it. On by default: without it the merge is a one-off tidy-up that the
    /// next pipeline run undoes.
    /// </param>
    public record MergeRequest(string Into, List<string> From, bool RecordAliases = true);

    /// <summary>Row counts for a merge — what would move (preview) or what did (apply).</summary>
    /// <param name="Deployments">Deploy events whose environment changes.</param>
    /// <param name="PromotionPolicies">Promotion policies whose source or target edge changes.</param>
    /// <param name="PromotionPolicyConflicts">
    /// Policies left in place because the target already has a policy for the same
    /// <c>(product, service, source → target)</c> edge.
    /// </param>
    /// <param name="PromotionCandidates">Promotion candidates whose source or target changes.</param>
    /// <param name="OpenPromotionCandidates">
    /// How many of those are still in flight (pending / approved / deploying). Worth waiting out: a
    /// candidate whose ticket sign-offs hit a conflict below can come out of the merge needing
    /// approval again.
    /// </param>
    /// <param name="PromotionWorkItems">Ticket index rows hanging off those candidates.</param>
    /// <param name="WorkItemApprovals">Ticket sign-offs whose target environment changes.</param>
    /// <param name="WorkItemApprovalConflicts">
    /// Sign-offs left in place because the same approver already decided the same ticket against the
    /// target environment.
    /// </param>
    /// <param name="WorkItemComments">Sign-off discussion entries that move.</param>
    /// <param name="ReleaseNotes">Published release notes whose environment changes.</param>
    /// <param name="ReleaseNoteTemplates">Per-environment release-note templates that move.</param>
    /// <param name="ReleaseNoteTemplateConflicts">
    /// Templates left in place because the target already has one for that product.
    /// </param>
    /// <param name="RollbackRequests">Rollback requests whose target or reference environment changes.</param>
    /// <param name="RollbackPolicies">Rollback policies whose environment changes.</param>
    /// <param name="RollbackPolicyConflicts">
    /// Rollback policies left in place because the target product/environment already has one.
    /// </param>
    /// <param name="WebhookSubscriptions">Webhook subscriptions filtered on the old environment.</param>
    /// <param name="DegenerateEdges">
    /// Promotion policies and candidates left in place because the merge would make their source and
    /// target the same environment.
    /// </param>
    public record MergeCounts(
        int Deployments,
        int PromotionPolicies,
        int PromotionPolicyConflicts,
        int PromotionCandidates,
        int OpenPromotionCandidates,
        int PromotionWorkItems,
        int WorkItemApprovals,
        int WorkItemApprovalConflicts,
        int WorkItemComments,
        int ReleaseNotes,
        int ReleaseNoteTemplates,
        int ReleaseNoteTemplateConflicts,
        int RollbackRequests,
        int RollbackPolicies,
        int RollbackPolicyConflicts,
        int WebhookSubscriptions,
        int DegenerateEdges)
    {
        public static readonly MergeCounts Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public MergeCounts Add(MergeCounts o) => new(
            Deployments + o.Deployments,
            PromotionPolicies + o.PromotionPolicies,
            PromotionPolicyConflicts + o.PromotionPolicyConflicts,
            PromotionCandidates + o.PromotionCandidates,
            OpenPromotionCandidates + o.OpenPromotionCandidates,
            PromotionWorkItems + o.PromotionWorkItems,
            WorkItemApprovals + o.WorkItemApprovals,
            WorkItemApprovalConflicts + o.WorkItemApprovalConflicts,
            WorkItemComments + o.WorkItemComments,
            ReleaseNotes + o.ReleaseNotes,
            ReleaseNoteTemplates + o.ReleaseNoteTemplates,
            ReleaseNoteTemplateConflicts + o.ReleaseNoteTemplateConflicts,
            RollbackRequests + o.RollbackRequests,
            RollbackPolicies + o.RollbackPolicies,
            RollbackPolicyConflicts + o.RollbackPolicyConflicts,
            WebhookSubscriptions + o.WebhookSubscriptions,
            DegenerateEdges + o.DegenerateEdges);

        /// <summary>Rows that move. Conflicts and degenerate edges are excluded — they stay put.</summary>
        public int Moved => Deployments + PromotionPolicies + PromotionCandidates + PromotionWorkItems
                          + WorkItemApprovals + WorkItemComments + ReleaseNotes + ReleaseNoteTemplates
                          + RollbackRequests + RollbackPolicies + WebhookSubscriptions;

        /// <summary>Rows the merge cannot take with it, for the "not everything moved" warning.</summary>
        public int LeftBehind => PromotionPolicyConflicts + WorkItemApprovalConflicts
                               + ReleaseNoteTemplateConflicts + RollbackPolicyConflicts + DegenerateEdges;
    }

    /// <summary>
    /// What a merge involves. <paramref name="Sources"/> is the request's <c>From</c> after dropping
    /// the target and de-duplicating — the honest answer to "what is this about to touch".
    /// </summary>
    /// <param name="AliasesRecorded">Whether the sources were written to the target's alias list.</param>
    /// <param name="RemovedEnvironments">
    /// Sources that were configured environments in their own right and were removed from the list.
    /// </param>
    public record MergePlan(
        string Into,
        List<string> Sources,
        MergeCounts Counts,
        bool AliasesRecorded,
        List<string> RemovedEnvironments,
        bool Applied);

    /// <summary>
    /// Every environment name the stored data actually uses, with what hangs off it — the input to a
    /// merge decision. The configured list answers "what did an admin curate"; this answers "what is
    /// really in there", which is the only way to notice that three pipelines have been writing
    /// "dev", "develop" and "development" for two years.
    ///
    /// <para><see cref="EnvironmentUsage.ResolvesTo"/> is the tell that a name is already aliased but
    /// its history has not followed: new deploys land on the canonical key while these rows stay
    /// behind. Ordered by deploy count, busiest first, so the environments that matter are at the
    /// top of the picker.</para>
    /// </summary>
    public async Task<List<EnvironmentUsage>> UsageAsync(CancellationToken ct = default)
    {
        var deploys = await _db.DeployEvents.AsNoTracking()
            .GroupBy(e => e.Environment)
            .Select(g => new { Key = g.Key, Count = g.Count(), Last = g.Max(e => e.DeployedAt) })
            .ToListAsync(ct);

        var promotions = await _db.PromotionCandidates.AsNoTracking()
            .GroupBy(c => c.TargetEnv)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var notes = await _db.ReleaseNotes.AsNoTracking()
            .GroupBy(r => r.Environment)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var settings = await _settings.GetSettings(ct);
        var map = EnvironmentAliasMap.Build(settings.Environments);
        var configured = (settings.Environments ?? [])
            .Select(e => e.Key.Trim())
            .ToHashSet(StringComparer.Ordinal);

        // Ordinal union: two spellings are two stored values, and the whole point of this list is to
        // show them as the separate things they currently are.
        var keys = deploys.Select(d => d.Key)
            .Concat(promotions.Select(p => p.Key))
            .Concat(notes.Select(n => n.Key))
            .Concat(configured)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal);

        return keys
            .Select(key =>
            {
                var resolved = map.Resolve(key);
                return new EnvironmentUsage(
                    Key: key,
                    Deployments: deploys.FirstOrDefault(d => d.Key == key)?.Count ?? 0,
                    Promotions: promotions.FirstOrDefault(p => p.Key == key)?.Count ?? 0,
                    ReleaseNotes: notes.FirstOrDefault(n => n.Key == key)?.Count ?? 0,
                    LastDeployedAt: deploys.FirstOrDefault(d => d.Key == key)?.Last,
                    Configured: configured.Contains(key),
                    ResolvesTo: string.Equals(resolved, key, StringComparison.Ordinal) ? null : resolved);
            })
            .OrderByDescending(u => u.Deployments)
            .ThenBy(u => u.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One environment name as the stored data uses it.</summary>
    /// <param name="Configured">Whether an admin has a row for this exact key in Settings → Environments.</param>
    /// <param name="ResolvesTo">
    /// The canonical key an alias already redirects this name to, or null when it is its own
    /// environment. Non-null means the alias is in place but the history has not been merged yet.
    /// </param>
    public record EnvironmentUsage(
        string Key,
        int Deployments,
        int Promotions,
        int ReleaseNotes,
        DateTimeOffset? LastDeployedAt,
        bool Configured,
        string? ResolvesTo);

    /// <summary>
    /// Counts what a merge would move, changing nothing. Throws <see cref="ArgumentException"/> for
    /// a request with no target or nothing left to merge.
    /// </summary>
    public Task<MergePlan> PreviewAsync(MergeRequest req, CancellationToken ct = default)
        => RunAsync(req, apply: false, ct);

    /// <summary>
    /// Moves the history and (unless the request opts out) records the aliases.
    ///
    /// <para>Runs inside a transaction: a merge is a dozen statements across eleven tables plus the
    /// settings row, and a failure halfway through leaves one environment under two names with the
    /// alias already recorded — worse than not having started. The execution-strategy wrapper is
    /// required because both providers are configured with <c>EnableRetryOnFailure</c> and EF refuses
    /// a user-initiated transaction under a retrying strategy otherwise (see
    /// <c>ServiceProductRemapService.ApplyAsync</c> for the longer version). The strategy may
    /// re-execute the delegate, so each attempt clears the change tracker and recomputes from
    /// current state.</para>
    /// </summary>
    public async Task<MergePlan> ApplyAsync(MergeRequest req, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var plan = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await RunAsync(req, apply: true, ct);
            await tx.CommitAsync(ct);
            return result;
        });

        _logger.LogInformation(
            "Merged environment(s) [{Sources}] into {Into}: {Deployments} deployment(s), "
            + "{Candidates} promotion(s), {Policies} policy/policies, {Approvals} ticket sign-off(s), "
            + "{Notes} release note(s); {LeftBehind} row(s) left in place (aliases recorded: {Aliases})",
            LogSanitizer.Clean(string.Join(", ", plan.Sources)), LogSanitizer.Clean(plan.Into),
            plan.Counts.Deployments, plan.Counts.PromotionCandidates, plan.Counts.PromotionPolicies,
            plan.Counts.WorkItemApprovals, plan.Counts.ReleaseNotes, plan.Counts.LeftBehind,
            plan.AliasesRecorded);

        return plan;
    }

    private async Task<MergePlan> RunAsync(MergeRequest req, bool apply, CancellationToken ct)
    {
        var into = (req.Into ?? "").Trim();
        if (into.Length == 0) throw new ArgumentException("into is required.", nameof(req));

        // Ordinal distinct: two spellings of the same name are two stored values and both need
        // moving, so both must survive to the update step. Only an exact match with the target drops
        // out — a case-only difference IS a rewrite worth doing.
        var sources = (req.From ?? [])
            .Select(s => (s ?? "").Trim())
            .Where(s => s.Length > 0 && !string.Equals(s, into, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sources.Count == 0)
            throw new ArgumentException(
                $"Nothing to merge — 'from' is empty once '{into}' itself is excluded.", nameof(req));

        // Every name that ends up as `into`, target included. A promotion whose BOTH ends are in
        // here is degenerate — it would become an edge from an environment to itself — and that is a
        // property of the whole merge, not of one source, so it is decided once here rather than per
        // source. Deciding it per source made the answer depend on the order the sources happened to
        // be processed in, and made a preview disagree with the apply that followed it.
        var universe = new List<string>(sources) { into };

        var counts = (await MergeDegenerateEdgesAsync(universe, ct));
        foreach (var from in sources) counts = counts.Add(await MergeOneAsync(from, into, universe, apply, ct));

        var removed = req.RecordAliases ? await RecordAliasesAsync(into, sources, apply, ct) : [];

        return new MergePlan(into, sources, counts, req.RecordAliases, removed, apply);
    }

    /// <summary>
    /// Promotion policies and candidates the merge cannot take with it because both of their ends
    /// resolve to the target. Counted once for the whole merge and never rewritten — a promotion from
    /// an environment to itself is not a thing the platform can represent, and which of the two ends
    /// the admin meant is not recoverable here.
    /// </summary>
    private async Task<MergeCounts> MergeDegenerateEdgesAsync(
        List<string> universe, CancellationToken ct)
    {
        var policies = await _db.PromotionPolicies.CountAsync(
            p => universe.Contains(p.SourceEnv) && universe.Contains(p.TargetEnv), ct);
        var candidates = await _db.PromotionCandidates.CountAsync(
            c => universe.Contains(c.SourceEnv) && universe.Contains(c.TargetEnv), ct);

        return MergeCounts.Empty with { DegenerateEdges = policies + candidates };
    }

    private async Task<MergeCounts> MergeOneAsync(
        string from, string into, List<string> universe, bool apply, CancellationToken ct)
    {
        var deployments = await MoveAsync(
            _db.DeployEvents.Where(e => e.Environment == from),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(e => e.Environment, into), c),
            apply, ct);

        var (policies, policyConflicts) = await MergePromotionPoliciesAsync(from, into, universe, apply, ct);
        var (candidates, openCandidates, workItems) =
            await MergePromotionCandidatesAsync(from, into, universe, apply, ct);
        var (approvals, approvalConflicts) = await MergeWorkItemApprovalsAsync(from, into, apply, ct);

        var comments = await MoveAsync(
            _db.WorkItemComments.Where(x => x.TargetEnv == from),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.TargetEnv, into), c),
            apply, ct);

        var releaseNotes = await MoveAsync(
            _db.ReleaseNotes.Where(x => x.Environment == from),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.Environment, into), c),
            apply, ct);

        var (templates, templateConflicts) = await MergeReleaseNoteTemplatesAsync(from, into, apply, ct);

        // Two statements: a request can name the environment on either end (the target it rolls back,
        // or the reference it aligns to), and one row can carry both.
        var rollbackTargets = await MoveAsync(
            _db.RollbackRequests.Where(x => x.TargetEnv == from),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.TargetEnv, into), c),
            apply, ct);
        var rollbackReferences = await MoveAsync(
            _db.RollbackRequests.Where(x => x.ReferenceEnv == from),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReferenceEnv, into), c),
            apply, ct);

        var (rollbackPolicies, rollbackPolicyConflicts) = await MergeRollbackPoliciesAsync(from, into, apply, ct);

        var webhooks = await MoveAsync(
            _db.WebhookSubscriptions.Where(x => x.FilterEnvironment == from),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.FilterEnvironment, into), c),
            apply, ct);

        return new MergeCounts(
            Deployments: deployments,
            PromotionPolicies: policies,
            PromotionPolicyConflicts: policyConflicts,
            PromotionCandidates: candidates,
            OpenPromotionCandidates: openCandidates,
            PromotionWorkItems: workItems,
            WorkItemApprovals: approvals,
            WorkItemApprovalConflicts: approvalConflicts,
            WorkItemComments: comments,
            ReleaseNotes: releaseNotes,
            ReleaseNoteTemplates: templates,
            ReleaseNoteTemplateConflicts: templateConflicts,
            RollbackRequests: rollbackTargets + rollbackReferences,
            RollbackPolicies: rollbackPolicies,
            RollbackPolicyConflicts: rollbackPolicyConflicts,
            WebhookSubscriptions: webhooks,
            // Counted once for the whole merge, in MergeDegenerateEdgesAsync.
            DegenerateEdges: 0);
    }

    /// <summary>
    /// Counts (preview) or performs (apply) one bulk rewrite, so neither mode can drift from the
    /// other's predicate. Bulk rather than tracked: an environment with years of deploy history is
    /// tens of thousands of rows, and pulling them through the change tracker to set one string each
    /// is how this operation times out.
    /// </summary>
    private static async Task<int> MoveAsync<T>(
        IQueryable<T> rows,
        Func<IQueryable<T>, CancellationToken, Task<int>> update,
        bool apply,
        CancellationToken ct) where T : class
        => apply ? await update(rows, ct) : await rows.CountAsync(ct);

    /// <summary>
    /// Promotion policies, whose unique <c>(Product, Service, SourceEnv, TargetEnv)</c> is what makes
    /// this more than a rename. Product/service comparison is exact: the unique index is on the raw
    /// values, so an exact match is the only collision the database will actually reject.
    /// <para>Rows whose OTHER end is also being merged are excluded from both passes: those are the
    /// degenerate edges <see cref="MergeDegenerateEdgesAsync"/> has already accounted for, and
    /// rewriting one end of one would leave a self-referential edge behind.</para>
    /// </summary>
    private async Task<(int Moved, int Conflicts)> MergePromotionPoliciesAsync(
        string from, string into, List<string> universe, bool apply, CancellationToken ct)
    {
        var sourceConflicts = await _db.PromotionPolicies.CountAsync(
            p => p.SourceEnv == from && !universe.Contains(p.TargetEnv)
              && _db.PromotionPolicies.Any(t => t.Product == p.Product && t.Service == p.Service
                  && t.SourceEnv == into && t.TargetEnv == p.TargetEnv), ct);

        var targetConflicts = await _db.PromotionPolicies.CountAsync(
            p => p.TargetEnv == from && !universe.Contains(p.SourceEnv)
              && _db.PromotionPolicies.Any(t => t.Product == p.Product && t.Service == p.Service
                  && t.SourceEnv == p.SourceEnv && t.TargetEnv == into), ct);

        var movedSource = await MoveAsync(
            _db.PromotionPolicies.Where(p => p.SourceEnv == from && !universe.Contains(p.TargetEnv)
                && !_db.PromotionPolicies.Any(t => t.Product == p.Product && t.Service == p.Service
                    && t.SourceEnv == into && t.TargetEnv == p.TargetEnv)),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(p => p.SourceEnv, into), c),
            apply, ct);

        var movedTarget = await MoveAsync(
            _db.PromotionPolicies.Where(p => p.TargetEnv == from && !universe.Contains(p.SourceEnv)
                && !_db.PromotionPolicies.Any(t => t.Product == p.Product && t.Service == p.Service
                    && t.SourceEnv == p.SourceEnv && t.TargetEnv == into)),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(p => p.TargetEnv, into), c),
            apply, ct);

        return (movedSource + movedTarget, sourceConflicts + targetConflicts);
    }

    /// <summary>
    /// Promotion candidates and the ticket index hanging off them. No unique index here, so nothing
    /// is left behind except the degenerate edges — two candidates for the same edge and version is a
    /// state the supersede/reconcile pass already knows how to settle.
    /// <para>The ticket rows go first, in both modes: their predicate reaches through to the parent's
    /// OLD environment, so doing it after the parents moved would find nothing.</para>
    /// </summary>
    private async Task<(int Moved, int Open, int WorkItems)> MergePromotionCandidatesAsync(
        string from, string into, List<string> universe, bool apply, CancellationToken ct)
    {
        // Only the target side matters for the index rows: TargetEnv is the only environment they
        // carry, denormalised from the parent.
        var workItems = await MoveAsync(
            _db.PromotionWorkItems.Where(w => _db.PromotionCandidates.Any(
                c => c.Id == w.CandidateId && c.TargetEnv == from && !universe.Contains(c.SourceEnv))),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(w => w.TargetEnv, into), c),
            apply, ct);

        // The two sides are disjoint: a candidate matching both would have both ends in the universe,
        // and those are excluded as degenerate. So the counts add up rather than double-count.
        var open = await _db.PromotionCandidates.CountAsync(
            c => ((c.SourceEnv == from && !universe.Contains(c.TargetEnv))
               || (c.TargetEnv == from && !universe.Contains(c.SourceEnv)))
              && (c.Status == PromotionStatus.Pending
               || c.Status == PromotionStatus.Approved
               || c.Status == PromotionStatus.Deploying), ct);

        var movedSource = await MoveAsync(
            _db.PromotionCandidates.Where(c => c.SourceEnv == from && !universe.Contains(c.TargetEnv)),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceEnv, into), c),
            apply, ct);

        var movedTarget = await MoveAsync(
            _db.PromotionCandidates.Where(c => c.TargetEnv == from && !universe.Contains(c.SourceEnv)),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.TargetEnv, into), c),
            apply, ct);

        return (movedSource + movedTarget, open, workItems);
    }

    /// <summary>
    /// Ticket sign-offs, unique on <c>(WorkItemKey, Product, TargetEnv, ApproverEmail)</c>. An
    /// approver who signed the same ticket off against both names already has a decision that applies
    /// to the target; the duplicate stays where it is rather than overwriting it, because the two can
    /// disagree (approved under one name, rejected under the other) and picking a winner is not this
    /// operation's call.
    /// </summary>
    private async Task<(int Moved, int Conflicts)> MergeWorkItemApprovalsAsync(
        string from, string into, bool apply, CancellationToken ct)
    {
        var conflicts = await _db.WorkItemApprovals.CountAsync(
            a => a.TargetEnv == from
              && _db.WorkItemApprovals.Any(t => t.WorkItemKey == a.WorkItemKey && t.Product == a.Product
                  && t.TargetEnv == into && t.ApproverEmail == a.ApproverEmail), ct);

        var moved = await MoveAsync(
            _db.WorkItemApprovals.Where(a => a.TargetEnv == from
                && !_db.WorkItemApprovals.Any(t => t.WorkItemKey == a.WorkItemKey && t.Product == a.Product
                    && t.TargetEnv == into && t.ApproverEmail == a.ApproverEmail)),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(a => a.TargetEnv, into), c),
            apply, ct);

        return (moved, conflicts);
    }

    /// <summary>
    /// Rollback policies, unique on <c>(Product, TargetEnv)</c>. The target's policy is the one that
    /// governs the merged environment from now on, so a product with a policy under both names keeps
    /// the target's and leaves the other in place.
    /// </summary>
    private async Task<(int Moved, int Conflicts)> MergeRollbackPoliciesAsync(
        string from, string into, bool apply, CancellationToken ct)
    {
        var conflicts = await _db.RollbackPolicies.CountAsync(
            p => p.TargetEnv == from
              && _db.RollbackPolicies.Any(t => t.Product == p.Product && t.TargetEnv == into), ct);

        var moved = await MoveAsync(
            _db.RollbackPolicies.Where(p => p.TargetEnv == from
                && !_db.RollbackPolicies.Any(t => t.Product == p.Product && t.TargetEnv == into)),
            (rows, c) => rows.ExecuteUpdateAsync(s => s.SetProperty(p => p.TargetEnv, into), c),
            apply, ct);

        return (moved, conflicts);
    }

    /// <summary>
    /// Per-environment release-note templates, which live in <c>platform_settings</c> under
    /// <c>release-notes.template.{product}.{environment}</c>.
    ///
    /// <para>The settings key IS the row's primary key, so this deletes and re-inserts rather than
    /// assigning to it — EF refuses to modify a key property on a tracked entity. Tracked entities at
    /// all (rather than a bulk update) because there are a handful of template rows and the scope has
    /// to be parsed in C# anyway.</para>
    ///
    /// <para>A product name may itself contain dots, so the key is split from the right: the last
    /// segment is the environment and there must be at least one segment before it. That is what
    /// keeps the product-default scope (<c>release-notes.template.prod</c>, for a product named
    /// "prod") and the global <c>release-notes.template.default</c> out of the match.</para>
    /// </summary>
    private async Task<(int Moved, int Conflicts)> MergeReleaseNoteTemplatesAsync(
        string from, string into, bool apply, CancellationToken ct)
    {
        // Every template row, then the suffix match in C#. EndsWith would translate to a provider
        // LIKE, where an environment name containing _ or % is a wildcard — "cloudiq_test" would
        // match "cloudiqXtest" too. There are a handful of template rows, so scanning is free.
        var rows = await _db.PlatformSettings
            .Where(s => s.Key.StartsWith(ReleaseNoteTemplateService.KeyPrefix))
            .ToListAsync(ct);

        var moved = 0;
        var conflicts = 0;
        foreach (var row in rows)
        {
            var scope = row.Key[ReleaseNoteTemplateService.KeyPrefix.Length..];
            var split = scope.LastIndexOf('.');
            // No dot inside the scope means the whole thing is a product name, not (product, env).
            if (split <= 0) continue;
            if (!string.Equals(scope[(split + 1)..], from, StringComparison.Ordinal)) continue;

            var target = ReleaseNoteTemplateService.KeyFor(scope[..split], into);
            // Checked against the rows loaded above as well as the database: an earlier source in the
            // same merge may already have claimed the target key in this transaction.
            if (rows.Any(r => string.Equals(r.Key, target, StringComparison.Ordinal))
                || await _db.PlatformSettings.AnyAsync(s => s.Key == target, ct))
            {
                conflicts++;
                continue;
            }

            if (apply)
            {
                _db.PlatformSettings.Remove(row);
                _db.PlatformSettings.Add(new PlatformSetting
                {
                    Key = target,
                    Value = row.Value,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedBy = "system",
                });
            }
            moved++;
        }

        if (apply && moved > 0) await _db.SaveChangesAsync(ct);
        return (moved, conflicts);
    }

    /// <summary>
    /// Records the merged names as aliases of the target and drops them as environments of their own,
    /// returning the ones that had a configured row. The target is created if it had none —
    /// inheriting the label, colour and production flag of the first configured source, so a merge
    /// into a key that only ever arrived from pipelines does not come out looking unconfigured.
    /// </summary>
    private async Task<List<string>> RecordAliasesAsync(
        string into, List<string> sources, bool apply, CancellationToken ct)
    {
        var settings = await _settings.GetSettings(ct);
        var configured = settings.Environments ?? [];

        bool IsSource(EnvironmentConfigDto e) =>
            sources.Any(s => string.Equals(s, e.Key.Trim(), StringComparison.OrdinalIgnoreCase));
        bool IsTarget(EnvironmentConfigDto e) =>
            string.Equals(e.Key.Trim(), into, StringComparison.OrdinalIgnoreCase);

        var removed = configured.Where(IsSource).Select(e => e.Key.Trim()).ToList();
        if (!apply) return removed;

        var existingTarget = configured.FirstOrDefault(IsTarget);
        var donor = configured.FirstOrDefault(IsSource);
        var target = existingTarget ?? new EnvironmentConfigDto(
            into,
            string.IsNullOrWhiteSpace(donor?.DisplayName) ? into : donor.DisplayName,
            donor?.Color,
            configured.Any(e => IsSource(e) && e.IsProduction));

        // The merged names, plus any aliases the merged-away rows carried — an alias of an alias is
        // an alias of the target, and dropping those would quietly re-split the traffic they cover.
        var inherited = configured.Where(IsSource).SelectMany(e => e.Aliases ?? []);
        var aliases = EnvironmentAliasValidator.CleanAliases(
            target.Key, [.. target.Aliases ?? [], .. sources, .. inherited]);
        target = target with { Aliases = aliases };

        var updated = new List<EnvironmentConfigDto>();
        var placed = false;
        foreach (var env in configured)
        {
            if (IsTarget(env))
            {
                updated.Add(target);
                placed = true;
            }
            else if (IsSource(env))
            {
                // The target takes the earliest position any merged row held, so a merge does not
                // shuffle the pipeline order the admin arranged.
                if (!placed && existingTarget is null)
                {
                    updated.Add(target);
                    placed = true;
                }
            }
            else
            {
                updated.Add(env);
            }
        }
        if (!placed) updated.Add(target);

        await _settings.SaveSettings(settings with { Environments = updated }, ct);
        return removed;
    }
}
