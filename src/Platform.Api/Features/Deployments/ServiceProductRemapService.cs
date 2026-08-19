using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

/// <summary>
/// Moves already-stored history for a service into the product its
/// <see cref="ServiceProductOverride"/> says it belongs to.
///
/// <para>Overrides are forward-only by design: writing one fixes what arrives next, and leaves the rows
/// that arrived before it alone. That is the safe default, but it is not the end state of a migration —
/// a service whose first two years sit under the old product still reads as two half-services on the
/// deployment matrix and in analytics. This is the deliberate second step, preview-then-apply like the
/// other Maintenance repairs, so an admin sees the row counts before anything moves.</para>
///
/// <para><b>Which rows move is decided by the resolver, not by this class.</b> For every product that
/// currently holds rows for the service, <see cref="ServiceProductOverrideService.ResolveAsync"/> is
/// asked where that product's entities should live; a product moves only when the answer is <i>this</i>
/// override. That is what stops a catch-all row from dragging along entities that a more specific
/// <c>FromProduct</c> row governs — the same rule at ingest and at repair, so history cannot end up
/// somewhere new traffic would never go.</para>
///
/// <para><b>What is deliberately left behind.</b> Ticket approvals and comments key on
/// <c>(WorkItemKey, Product, TargetEnv)</c> with no service column, so a ticket spanning two services
/// cannot be attributed to one of them; moving those rows would silently reassign another service's
/// approvals. They stay under the old product and are counted as
/// <see cref="RemapCounts.StrandedTicketApprovals"/> instead. Promotion and rollback <i>policies</i>
/// also stay — they are configuration an admin owns, and the target product's policies are the ones
/// that should apply from now on. Rollback requests stay because a request carries one product across
/// many services; moving it for one service would misfile the rest.</para>
/// </summary>
public class ServiceProductRemapService
{
    private readonly PlatformDbContext _db;
    private readonly ServiceProductOverrideService _overrides;
    private readonly ILogger<ServiceProductRemapService> _logger;

    public ServiceProductRemapService(
        PlatformDbContext db,
        ServiceProductOverrideService overrides,
        ILogger<ServiceProductRemapService> logger)
    {
        _db = db;
        _overrides = overrides;
        _logger = logger;
    }

    /// <summary>
    /// Row counts for a remap — what would move (preview) or what did (apply).
    /// </summary>
    /// <param name="Deployments">Deploy events whose product changes.</param>
    /// <param name="DeployWorkItems">Ticket index rows hanging off those events.</param>
    /// <param name="Builds">Build registrations that can move.</param>
    /// <param name="BuildConflicts">
    /// Builds left in place because the target product already has a registration for the same service
    /// and version. Moving them would collide with the unique key, and the existing row is the one the
    /// target product's promotions already point at, so the duplicate stays where it is.
    /// </param>
    /// <param name="Promotions">Promotion candidates whose product changes.</param>
    /// <param name="OpenPromotions">
    /// How many of those candidates are still in flight (pending / approved / deploying). Worth waiting
    /// out: their recorded ticket approvals do not move (see <paramref name="StrandedTicketApprovals"/>),
    /// so an open candidate can come out of the remap needing approval again.
    /// </param>
    /// <param name="PromotionWorkItems">Ticket index rows hanging off those candidates.</param>
    /// <param name="Retirements">Service retirement tombstones whose product changes.</param>
    /// <param name="RetirementMerges">
    /// Tombstones folded into one the target product already had, keeping the later retirement date.
    /// </param>
    /// <param name="StrandedTicketApprovals">
    /// Ticket approvals that stay under the old product because they are not service-scoped.
    /// </param>
    public record RemapCounts(
        int Deployments,
        int DeployWorkItems,
        int Builds,
        int BuildConflicts,
        int Promotions,
        int OpenPromotions,
        int PromotionWorkItems,
        int Retirements,
        int RetirementMerges,
        int StrandedTicketApprovals)
    {
        public static readonly RemapCounts Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public RemapCounts Add(RemapCounts o) => new(
            Deployments + o.Deployments,
            DeployWorkItems + o.DeployWorkItems,
            Builds + o.Builds,
            BuildConflicts + o.BuildConflicts,
            Promotions + o.Promotions,
            OpenPromotions + o.OpenPromotions,
            PromotionWorkItems + o.PromotionWorkItems,
            Retirements + o.Retirements,
            RetirementMerges + o.RetirementMerges,
            StrandedTicketApprovals + o.StrandedTicketApprovals);

        public int Total => Deployments + DeployWorkItems + Builds + Promotions + PromotionWorkItems
                          + Retirements + RetirementMerges;
    }

    /// <summary>
    /// What a remap of one override involves. <paramref name="FromProducts"/> names every product the
    /// service's rows are being pulled out of — the honest answer to "what is this about to touch",
    /// which a single count cannot give when a service has drifted across three product names.
    /// </summary>
    public record RemapPlan(
        Guid OverrideId,
        string Service,
        string TargetProduct,
        List<string> FromProducts,
        RemapCounts Counts,
        bool Applied);

    /// <summary>
    /// Counts what a remap would move, changing nothing. Throws <see cref="KeyNotFoundException"/> when
    /// the override no longer exists.
    /// </summary>
    public Task<RemapPlan> PreviewAsync(Guid overrideId, CancellationToken ct = default)
        => RunAsync(overrideId, apply: false, ct);

    /// <summary>
    /// Moves the history. Runs inside a transaction — the only place in this codebase that takes one out
    /// explicitly, because a remap is several statements across five tables and a failure halfway
    /// through would leave one service filed under two products, which is the condition it exists to
    /// cure. Throws <see cref="KeyNotFoundException"/> when the override no longer exists.
    ///
    /// <para><b>Why the execution strategy wrapper.</b> Both providers are configured with
    /// <c>EnableRetryOnFailure</c> (see Program.cs — the first deploy after an Azure serverless
    /// auto-pause has to wait out a cold resume), and EF refuses a user-initiated transaction under a
    /// retrying strategy unless the whole transaction is one retriable unit. Without this the endpoint
    /// throws <see cref="InvalidOperationException"/> before touching a single row. The strategy may
    /// re-execute the delegate, so each attempt starts from a clean change tracker and recomputes the
    /// source products from current state — a retry after a rolled-back attempt therefore sees exactly
    /// what a first attempt would.</para>
    /// </summary>
    public async Task<RemapPlan> ApplyAsync(Guid overrideId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var plan = await strategy.ExecuteAsync(async () =>
        {
            // A previous attempt may have left entities tracked as Modified (the retirement merge below
            // works on tracked rows). Re-applying that stale state on top of a rolled-back transaction
            // is how a retry corrupts what it was meant to repair.
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await RunAsync(overrideId, apply: true, ct);
            await tx.CommitAsync(ct);
            return result;
        });

        _logger.LogInformation(
            "Remapped {Service} from [{FromProducts}] to {Product}: {Deployments} deployment(s), "
            + "{Builds} build(s) ({BuildConflicts} left in place), {Promotions} promotion(s); "
            + "{Stranded} ticket approval(s) stayed under the old product",
            LogSanitizer.Clean(plan.Service), LogSanitizer.Clean(string.Join(", ", plan.FromProducts)),
            LogSanitizer.Clean(plan.TargetProduct), plan.Counts.Deployments, plan.Counts.Builds,
            plan.Counts.BuildConflicts, plan.Counts.Promotions, plan.Counts.StrandedTicketApprovals);

        return plan;
    }

    private async Task<RemapPlan> RunAsync(Guid overrideId, bool apply, CancellationToken ct)
    {
        var row = await _overrides.GetAsync(overrideId, ct)
            ?? throw new KeyNotFoundException($"No service product override with id {overrideId}.");

        var service = row.Service.Trim();
        var target = row.Product.Trim();
        // EF translates ToLower() to the provider's LOWER(); ToLowerInvariant() it cannot translate at
        // all. Comparing lowered columns is what makes the match case-insensitive identically on
        // Postgres (case-sensitive by default) and SQL Server (usually not) — at the cost of the index
        // on service. Acceptable: this runs when an admin clicks a button, not on the ingest path.
        var lowered = service.ToLowerInvariant();

        var sources = await ResolveSourceProductsAsync(row, lowered, ct);
        var counts = RemapCounts.Empty;
        foreach (var from in sources)
            counts = counts.Add(await RemapOneAsync(from, target, lowered, apply, ct));

        return new RemapPlan(row.Id, service, target, sources, counts, apply);
    }

    /// <summary>
    /// The products currently holding rows for the service that this override — and not some other,
    /// more specific one — is responsible for redirecting. Products already equal to the target drop
    /// out, so a second apply is a no-op.
    /// </summary>
    private async Task<List<string>> ResolveSourceProductsAsync(
        ServiceProductOverride row, string lowered, CancellationToken ct)
    {
        var present = new List<string>();
        present.AddRange(await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Service.ToLower() == lowered).Select(e => e.Product).Distinct().ToListAsync(ct));
        present.AddRange(await _db.Builds.AsNoTracking()
            .Where(b => b.Service.ToLower() == lowered).Select(b => b.Product).Distinct().ToListAsync(ct));
        present.AddRange(await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Service.ToLower() == lowered).Select(c => c.Product).Distinct().ToListAsync(ct));
        present.AddRange(await _db.DeletedServices.AsNoTracking()
            .Where(d => d.Service.ToLower() == lowered).Select(d => d.Product).Distinct().ToListAsync(ct));

        // Ordinal distinct: two spellings of the same product name are two stored values, and both need
        // moving, so they must both survive to the update step.
        var candidates = present.Distinct(StringComparer.Ordinal).ToList();

        var sources = new List<string>();
        foreach (var product in candidates)
        {
            var resolution = await _overrides.ResolveAsync(product, row.Service, ct);
            if (resolution.Applied?.Id == row.Id) sources.Add(product);
        }
        return sources.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private async Task<RemapCounts> RemapOneAsync(
        string from, string target, string lowered, bool apply, CancellationToken ct)
    {
        // Ticket index rows first, in both modes: their predicate reaches through to the parent's OLD
        // product, so counting or updating them after the parents moved would find nothing.
        var deployWorkItems = _db.DeployEventWorkItems.Where(w =>
            _db.DeployEvents.Any(e =>
                e.Id == w.DeployEventId && e.Product == from && e.Service.ToLower() == lowered));

        var promotionWorkItems = _db.PromotionWorkItems.Where(w =>
            _db.PromotionCandidates.Any(c =>
                c.Id == w.CandidateId && c.Product == from && c.Service.ToLower() == lowered));

        // Informational only — see the class remarks on why these are not moved.
        var movingTicketKeys = await promotionWorkItems.Select(w => w.WorkItemKey).Distinct().ToListAsync(ct);
        var strandedApprovals = movingTicketKeys.Count == 0
            ? 0
            : await _db.WorkItemApprovals.CountAsync(
                a => a.Product == from && movingTicketKeys.Contains(a.WorkItemKey), ct);

        var deployWorkItemCount = apply
            ? await deployWorkItems.ExecuteUpdateAsync(s => s.SetProperty(w => w.Product, target), ct)
            : await deployWorkItems.CountAsync(ct);

        var promotionWorkItemCount = apply
            ? await promotionWorkItems.ExecuteUpdateAsync(s => s.SetProperty(w => w.Product, target), ct)
            : await promotionWorkItems.CountAsync(ct);

        var events = _db.DeployEvents.Where(e => e.Product == from && e.Service.ToLower() == lowered);
        var eventCount = apply
            ? await events.ExecuteUpdateAsync(s => s.SetProperty(e => e.Product, target), ct)
            : await events.CountAsync(ct);

        var candidates = _db.PromotionCandidates.Where(c => c.Product == from && c.Service.ToLower() == lowered);
        var openCount = await candidates.CountAsync(
            c => c.Status == PromotionStatus.Pending
              || c.Status == PromotionStatus.Approved
              || c.Status == PromotionStatus.Deploying, ct);
        var candidateCount = apply
            ? await candidates.ExecuteUpdateAsync(s => s.SetProperty(c => c.Product, target), ct)
            : await candidates.CountAsync(ct);

        var (builds, buildConflicts) = await RemapBuildsAsync(from, target, lowered, apply, ct);
        var (retirements, retirementMerges) = await RemapRetirementsAsync(from, target, lowered, apply, ct);

        return new RemapCounts(
            Deployments: eventCount,
            DeployWorkItems: deployWorkItemCount,
            Builds: builds,
            BuildConflicts: buildConflicts,
            Promotions: candidateCount,
            OpenPromotions: openCount,
            PromotionWorkItems: promotionWorkItemCount,
            Retirements: retirements,
            RetirementMerges: retirementMerges,
            StrandedTicketApprovals: strandedApprovals);
    }

    /// <summary>
    /// Builds carry a unique <c>(Product, Service, Version)</c>, so a version the target product already
    /// has cannot receive a second row. Those are counted and skipped rather than deleted or renamed:
    /// the target's existing registration is the one its promotions and deploys already reference, and
    /// the stale duplicate under the old product is harmless where it sits.
    /// </summary>
    private async Task<(int Moved, int Conflicts)> RemapBuildsAsync(
        string from, string target, string lowered, bool apply, CancellationToken ct)
    {
        var source = _db.Builds.Where(b => b.Product == from && b.Service.ToLower() == lowered);

        var movable = source.Where(b => !_db.Builds.Any(t =>
            t.Product == target && t.Service.ToLower() == lowered && t.Version == b.Version));

        var conflicts = await source.CountAsync(b => _db.Builds.Any(t =>
            t.Product == target && t.Service.ToLower() == lowered && t.Version == b.Version), ct);

        var moved = apply
            ? await movable.ExecuteUpdateAsync(s => s.SetProperty(b => b.Product, target), ct)
            : await movable.CountAsync(ct);

        return (moved, conflicts);
    }

    /// <summary>
    /// Retirement tombstones are unique on <c>(Product, Service)</c>. When both products have one, the
    /// two are folded into the target's row keeping the later <c>DeletedAt</c> — the retirement decision
    /// is what matters, and the later one is the one a reviving deploy still has to beat. Tracked
    /// entities rather than a bulk update: there are at most a couple of rows, and the merge needs to
    /// compare them.
    /// </summary>
    private async Task<(int Moved, int Merged)> RemapRetirementsAsync(
        string from, string target, string lowered, bool apply, CancellationToken ct)
    {
        var source = await _db.DeletedServices
            .Where(d => d.Product == from && d.Service.ToLower() == lowered)
            .ToListAsync(ct);
        if (source.Count == 0) return (0, 0);

        var existing = await _db.DeletedServices
            .Where(d => d.Product == target && d.Service.ToLower() == lowered)
            .ToListAsync(ct);

        var moved = 0;
        var merged = 0;
        foreach (var row in source)
        {
            var winner = existing.FirstOrDefault();
            if (winner is null)
            {
                if (apply)
                {
                    row.Product = target;
                    existing.Add(row);
                }
                moved++;
            }
            else
            {
                if (apply)
                {
                    if (row.DeletedAt > winner.DeletedAt)
                    {
                        winner.DeletedAt = row.DeletedAt;
                        winner.DeletedById = row.DeletedById;
                        winner.DeletedByName = row.DeletedByName;
                        winner.Reason = row.Reason;
                    }
                    _db.DeletedServices.Remove(row);
                }
                merged++;
            }
        }

        if (apply) await _db.SaveChangesAsync(ct);
        return (moved, merged);
    }
}
