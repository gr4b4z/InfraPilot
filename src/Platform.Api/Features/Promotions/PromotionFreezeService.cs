using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Creates, lists and lifts Promotion Freezes (GLOSSARY.md, ADR-0001, ADR-0002). The freeze itself
/// is enforced by <see cref="PromotionService.EvaluateGateAsync"/>; this service only owns the
/// freeze rows, their held counts, and the re-evaluation sweep when a freeze is lifted.
/// <para>Authorization (administrators only) is enforced at the endpoint group.</para>
/// </summary>
public class PromotionFreezeService
{
    /// <summary>Matches the <c>promotion_freezes.Reason</c> column length.</summary>
    private const int MaxReasonLength = 1000;

    public const int DefaultHistoryLimit = 10;
    public const int MaxHistoryLimit = 50;
    private const string FrozenAction = "promotion.frozen";
    private const string UnfrozenAction = "promotion.unfrozen";

    private readonly PlatformDbContext _db;
    private readonly PromotionService _promotions;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ILogger<PromotionFreezeService> _logger;

    public PromotionFreezeService(
        PlatformDbContext db,
        PromotionService promotions,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IWebhookDispatcher webhookDispatcher,
        ILogger<PromotionFreezeService> logger)
    {
        _db = db;
        _promotions = promotions;
        _currentUser = currentUser;
        _audit = audit;
        _webhookDispatcher = webhookDispatcher;
        _logger = logger;
    }

    /// <summary>Every active freeze, with the number of Pending candidates of its pair it currently holds.</summary>
    public async Task<IReadOnlyList<ActiveFreeze>> GetActiveAsync(CancellationToken ct = default)
    {
        var freezes = await _db.PromotionFreezes.AsNoTracking()
            .OrderBy(f => f.Product).ThenBy(f => f.TargetEnv)
            .ToListAsync(ct);

        var result = new List<ActiveFreeze>(freezes.Count);
        foreach (var f in freezes)
            result.Add(ToActive(f, await CountHeldAsync(f.Product, f.TargetEnv, ct)));
        return result;
    }

    /// <summary>
    /// Ids of the <paramref name="candidates"/> held by an active freeze: Pending candidates whose
    /// (product, targetEnv) pair is frozen. Loads the active freezes once and matches in memory.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> GetHeldByFreezeAsync(
        IReadOnlyCollection<PromotionCandidate> candidates, CancellationToken ct = default)
    {
        if (!candidates.Any(c => c.Status == PromotionStatus.Pending)) return new HashSet<Guid>();

        var frozen = (await _db.PromotionFreezes.AsNoTracking()
                .Select(f => new { f.Product, f.TargetEnv })
                .ToListAsync(ct))
            .Select(f => (f.Product, f.TargetEnv))
            .ToHashSet();

        return candidates
            .Where(c => c.Status == PromotionStatus.Pending && frozen.Contains((c.Product, c.TargetEnv)))
            .Select(c => c.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Freezes <c>(product, targetEnv)</c>. Throws <see cref="ArgumentException"/> when the reason
    /// (or the pair) is blank, <see cref="NoApprovalPolicyForPairException"/> when no approval policy
    /// targets the pair, and <see cref="FreezeAlreadyActiveException"/> when it is already frozen.
    /// </summary>
    public async Task<ActiveFreeze> FreezeAsync(
        string product, string targetEnv, string reason, CancellationToken ct = default)
    {
        var p = (product ?? "").Trim();
        var env = (targetEnv ?? "").Trim();
        var why = (reason ?? "").Trim();
        if (p.Length == 0) throw new ArgumentException("Product is required.", nameof(product));
        if (env.Length == 0) throw new ArgumentException("Target environment is required.", nameof(targetEnv));
        if (why.Length == 0) throw new ArgumentException("A reason is required to freeze promotions.", nameof(reason));
        if (why.Length > MaxReasonLength)
            throw new ArgumentException($"The reason must be at most {MaxReasonLength} characters.", nameof(reason));

        // Any policy row for the pair (any service, any source env) means the pair can receive promotions.
        if (!await _db.PromotionPolicies.AsNoTracking().AnyAsync(x => x.Product == p && x.TargetEnv == env, ct))
            throw new NoApprovalPolicyForPairException(p, env);

        if (await _db.PromotionFreezes.AsNoTracking().AnyAsync(f => f.Product == p && f.TargetEnv == env, ct))
            throw new FreezeAlreadyActiveException(p, env);

        var freeze = new PromotionFreeze
        {
            Id = Guid.NewGuid(),
            Product = p,
            TargetEnv = env,
            Reason = why,
            FrozenBy = _currentUser.Name,
            FrozenByEmail = _currentUser.Email,
            FrozenAt = DateTimeOffset.UtcNow,
        };
        _db.PromotionFreezes.Add(freeze);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a concurrent create on the unique (Product, TargetEnv) index.
            throw new FreezeAlreadyActiveException(p, env);
        }

        await _audit.Log(
            "promotions", "promotion.frozen",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionFreeze", freeze.Id, null,
            new { product = p, targetEnv = env, reason = why });

        _logger.LogInformation(
            "Promotions of {Product} to {TargetEnv} frozen by {Email}",
            LogSanitizer.Clean(p), LogSanitizer.Clean(env), _currentUser.Email);

        var active = ToActive(freeze, await CountHeldAsync(p, env, ct));
        await DispatchWebhookAsync("promotion.frozen", active, active.HeldCandidates, freeze.FrozenAt);
        return active;
    }

    /// <summary>
    /// Lifts the freeze on <c>(product, targetEnv)</c> and re-evaluates every Pending candidate of
    /// that pair, so whatever already satisfies its gate becomes Approved now. Returns <c>false</c>
    /// when the pair is not frozen.
    /// </summary>
    public async Task<bool> LiftAsync(string product, string targetEnv, CancellationToken ct = default)
    {
        var freeze = await _db.PromotionFreezes
            .FirstOrDefaultAsync(f => f.Product == product && f.TargetEnv == targetEnv, ct);
        if (freeze is null) return false;

        _db.PromotionFreezes.Remove(freeze);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.unfrozen",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionFreeze", freeze.Id, null,
            new
            {
                product = freeze.Product,
                targetEnv = freeze.TargetEnv,
                reason = freeze.Reason,
                frozenBy = freeze.FrozenBy,
                frozenAt = freeze.FrozenAt,
            });

        _logger.LogInformation(
            "Promotions of {Product} to {TargetEnv} unfrozen by {Email}",
            LogSanitizer.Clean(freeze.Product), LogSanitizer.Clean(freeze.TargetEnv), _currentUser.Email);

        // The lift is committed: the sweep must not be aborted by a cancelled request, or the
        // remaining held candidates would stay Pending with nothing to re-evaluate them later.
        var held = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Product == freeze.Product && c.TargetEnv == freeze.TargetEnv
                     && c.Status == PromotionStatus.Pending)
            .Select(c => c.Id)
            .ToListAsync(CancellationToken.None);

        // Announce the lift before the sweep so subscribers see promotion.unfrozen ahead of the
        // promotion.approved burst it triggers.
        await DispatchWebhookAsync("promotion.unfrozen", ToActive(freeze, held.Count), held.Count, DateTimeOffset.UtcNow);

        foreach (var id in held)
            await TryReevaluateAsync(id);

        return true;
    }

    /// <summary>
    /// Non-fatal per candidate, like <c>WorkItemApprovalService.TryReevaluateCandidateAsync</c>: a
    /// failure is logged and the sweep moves on. The change tracker is cleared so a candidate whose
    /// save failed is not saved as Approved by the next candidate's <c>SaveChangesAsync</c>.
    /// </summary>
    private async Task TryReevaluateAsync(Guid candidateId)
    {
        try
        {
            await _promotions.ReevaluateAsync(candidateId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex,
                "Gate re-evaluation failed for candidate {CandidateId} after freeze lift", candidateId);
        }
    }

    /// <summary>
    /// Non-fatal, like the existing promotion webhooks: a failed dispatch is logged and never fails
    /// the freeze/unfreeze. <paramref name="heldCandidates"/> is the Pending count at freeze time, or
    /// the number of candidates re-evaluated when lifting.
    /// </summary>
    private async Task DispatchWebhookAsync(
        string eventType, ActiveFreeze freeze, int heldCandidates, DateTimeOffset at)
    {
        try
        {
            var payload = new
            {
                eventType,
                product = freeze.Product,
                targetEnv = freeze.TargetEnv,
                reason = freeze.Reason,
                frozenBy = freeze.FrozenBy,
                frozenAt = freeze.FrozenAt,
                actor = new { name = _currentUser.Name, email = _currentUser.Email },
                at,
                heldCandidates,
            };
            var filters = new WebhookEventFilters(Product: freeze.Product, Environment: freeze.TargetEnv);

            await _webhookDispatcher.DispatchAsync(eventType, payload, filters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Webhook dispatch '{EventType}' failed for {Product} to {TargetEnv}",
                eventType, LogSanitizer.Clean(freeze.Product), LogSanitizer.Clean(freeze.TargetEnv));
        }
    }

    /// <summary>
    /// Freeze and lift events, newest first, derived from the audit log (<c>promotion.frozen</c> /
    /// <c>promotion.unfrozen</c>). <paramref name="limit"/> is clamped to 1..<see cref="MaxHistoryLimit"/>.
    /// </summary>
    public async Task<IReadOnlyList<FreezeHistoryEntry>> GetHistoryAsync(
        int limit = DefaultHistoryLimit, CancellationToken ct = default)
    {
        var rows = await _db.AuditLog.AsNoTracking()
            .Where(a => a.Action == FrozenAction || a.Action == UnfrozenAction)
            .OrderByDescending(a => a.Timestamp)
            .Take(ClampHistoryLimit(limit))
            .ToListAsync(ct);

        return rows.Select(ToHistoryEntry).ToList();
    }

    public static int ClampHistoryLimit(int limit) => Math.Clamp(limit, 1, MaxHistoryLimit);

    /// <summary>How many lifted freezes <see cref="GetWindowsAsync"/> reads back from the audit log.</summary>
    public const int MaxLiftedWindows = 500;

    /// <summary>
    /// The periods each (product, targetEnv) pair was frozen, newest first: every active freeze
    /// (<c>Until</c> null) plus the last <see cref="MaxLiftedWindows"/> lifted ones, rebuilt from
    /// their <c>promotion.unfrozen</c> audit entries (which carry <c>frozenAt</c>). The deployments
    /// pages use them to mark a deploy that landed while its environment was frozen for its product,
    /// which has to keep working after the freeze row is gone.
    /// </summary>
    public async Task<IReadOnlyList<FreezeWindow>> GetWindowsAsync(string? product, CancellationToken ct = default)
    {
        var p = string.IsNullOrWhiteSpace(product) ? null : product.Trim();

        var active = await _db.PromotionFreezes.AsNoTracking()
            .Where(f => p == null || f.Product == p)
            .ToListAsync(ct);

        var lifted = await _db.AuditLog.AsNoTracking()
            .Where(a => a.Action == UnfrozenAction)
            .OrderByDescending(a => a.Timestamp)
            .Take(MaxLiftedWindows)
            .ToListAsync(ct);

        return active
            .Select(f => new FreezeWindow(f.Product, f.TargetEnv, f.Reason, f.FrozenBy, f.FrozenAt, null, null))
            .Concat(lifted.Select(ToLiftedWindow).OfType<FreezeWindow>())
            .Where(w => p == null || w.Product == p)
            .OrderByDescending(w => w.From)
            .ToList();
    }

    // The unfrozen payload is { product, targetEnv, reason, frozenBy, frozenAt }; an entry without a
    // pair or a start time cannot be placed on a timeline and is skipped.
    private static FreezeWindow? ToLiftedWindow(AuditEntry a)
    {
        if (string.IsNullOrWhiteSpace(a.AfterState)) return null;
        try
        {
            using var doc = JsonDocument.Parse(a.AfterState);
            var root = doc.RootElement;
            var product = ReadString(root, "product");
            var targetEnv = ReadString(root, "targetEnv");
            if (string.IsNullOrEmpty(product) || string.IsNullOrEmpty(targetEnv)) return null;
            if (!root.TryGetProperty("frozenAt", out var at) || at.ValueKind != JsonValueKind.String
                || !at.TryGetDateTimeOffset(out var frozenAt))
                return null;

            return new FreezeWindow(
                product, targetEnv, ReadString(root, "reason") ?? "", ReadString(root, "frozenBy") ?? "",
                frozenAt, a.Timestamp, a.ActorName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The audit payload (AfterState) is { product, targetEnv, reason, ... } for both actions.
    private static FreezeHistoryEntry ToHistoryEntry(AuditEntry a)
    {
        string product = "", targetEnv = "";
        string? reason = null;
        if (!string.IsNullOrWhiteSpace(a.AfterState))
        {
            try
            {
                using var doc = JsonDocument.Parse(a.AfterState);
                var root = doc.RootElement;
                product = ReadString(root, "product") ?? "";
                targetEnv = ReadString(root, "targetEnv") ?? "";
                reason = ReadString(root, "reason");
            }
            catch (JsonException)
            {
                // Malformed payload: keep the event with blank fields rather than hiding it.
            }
        }

        var action = a.Action == FrozenAction ? "frozen" : "unfrozen";
        return new FreezeHistoryEntry(action, product, targetEnv, reason, a.ActorName, a.Timestamp);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private Task<int> CountHeldAsync(string product, string targetEnv, CancellationToken ct)
        => _db.PromotionCandidates.AsNoTracking()
            .CountAsync(c => c.Product == product && c.TargetEnv == targetEnv
                          && c.Status == PromotionStatus.Pending, ct);

    private static ActiveFreeze ToActive(PromotionFreeze f, int held)
        => new(f.Product, f.TargetEnv, f.Reason, f.FrozenBy, f.FrozenAt, held);
}

/// <summary>Read model of an active Promotion Freeze; <see cref="HeldCandidates"/> = Pending candidates of the pair.</summary>
public record ActiveFreeze(
    string Product, string TargetEnv, string Reason, string FrozenBy, DateTimeOffset FrozenAt, int HeldCandidates);

/// <summary>One journal event: <see cref="Action"/> is "frozen" or "unfrozen".</summary>
public record FreezeHistoryEntry(
    string Action, string Product, string TargetEnv, string? Reason, string Actor, DateTimeOffset At);

/// <summary>
/// One period a (product, targetEnv) pair was frozen. <see cref="Until"/> and <see cref="LiftedBy"/>
/// are null while the freeze is still active.
/// </summary>
public record FreezeWindow(
    string Product, string TargetEnv, string Reason, string FrozenBy,
    DateTimeOffset From, DateTimeOffset? Until, string? LiftedBy);

/// <summary>Thrown by <see cref="PromotionFreezeService.FreezeAsync"/> when the pair is already frozen (409).</summary>
public class FreezeAlreadyActiveException : InvalidOperationException
{
    public FreezeAlreadyActiveException(string product, string targetEnv)
        : base($"Promotions of {product} to {targetEnv} are already frozen.") { }
}

/// <summary>
/// Thrown by <see cref="PromotionFreezeService.FreezeAsync"/> when no approval policy targets the
/// pair (422): such a pair cannot receive promotions, so freezing it would do nothing (ADR-0002).
/// </summary>
public class NoApprovalPolicyForPairException : InvalidOperationException
{
    public NoApprovalPolicyForPairException(string product, string targetEnv)
        : base($"{product} has no approval policy targeting {targetEnv} — there is nothing to freeze.") { }
}
