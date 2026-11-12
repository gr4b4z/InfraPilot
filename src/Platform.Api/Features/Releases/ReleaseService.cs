using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Releases.Models;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Releases;

public sealed class ReleaseValidationException(string message) : Exception(message);

public sealed class ReleaseConflictException(string message, Guid existingId) : Exception(message)
{
    public Guid ExistingId { get; } = existingId;
}

public sealed class ReleaseForbiddenException(string message) : Exception(message);

/// <summary>
/// Releases and their checklists. Every write goes through here so it validates the same way,
/// derives the release status the same way, leaves an audit entry and tells open pages to refresh.
///
/// <para><b>Who may change a release.</b> Anyone signed in can read them. Administrators and the
/// release managers — the people and directory groups an administrator lists under Settings →
/// Releases (<see cref="ManagersSettingKey"/>) — open releases and run them: edit them, move their
/// steps, close them. The person named as a release's manager can run that release too. Deleting one
/// is for administrators only — a release that happened is cancelled or reopened, not removed.</para>
/// </summary>
public partial class ReleaseService
{
    private const string AuditModule = "releases";
    private const int MaxServiceNames = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Clock skew allowance for "not in the future": a browser a couple of minutes ahead of the API
    // must still be able to say "done just now".
    private static readonly TimeSpan FutureSlack = TimeSpan.FromMinutes(5);

    private readonly PlatformDbContext _db;
    private readonly EnvironmentAliasResolver _environments;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly IPlatformEventPublisher _events;
    private readonly PromotionApprovalAuthorizer _principals;

    public ReleaseService(
        PlatformDbContext db, EnvironmentAliasResolver environments, ICurrentUser user,
        IAuditLogger audit, IPlatformEventPublisher events, PromotionApprovalAuthorizer principals)
    {
        _db = db;
        _environments = environments;
        _user = user;
        _audit = audit;
        _events = events;
        _principals = principals;
    }

    // ── Release managers ─────────────────────────────────────────────────────

    /// <summary>The platform setting holding the release managers, a JSON <see cref="PrincipalSet"/>.</summary>
    public const string ManagersSettingKey = "releases.managers";

    private bool? _isReleaseManager;

    public async Task<PrincipalSet> GetManagersAsync(CancellationToken ct)
    {
        var value = await _db.PlatformSettings.AsNoTracking()
            .Where(s => s.Key == ManagersSettingKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(value)) return new PrincipalSet();
        try
        {
            var set = JsonSerializer.Deserialize<PrincipalSet>(value, Json);
            return set is null ? new PrincipalSet() : new PrincipalSet(set.Groups ?? [], set.Users ?? []);
        }
        catch (JsonException) { return new PrincipalSet(); }
    }

    public async Task<PrincipalSet> SetManagersAsync(PrincipalSet managers, CancellationToken ct)
    {
        if (!_user.IsAdmin)
            throw new ReleaseForbiddenException("Only administrators can choose the release managers.");
        var groups = (managers.Groups ?? [])
            .Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id))
            .Select(g => new GroupRef(g.Id.Trim(), string.IsNullOrWhiteSpace(g.Name) ? g.Id.Trim() : g.Name.Trim()))
            .DistinctBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var users = Names(managers.Users, "users", MaxServiceNames);
        if (groups.Count > MaxServiceNames) throw new ReleaseValidationException($"'groups' has more than {MaxServiceNames} entries.");
        var clean = new PrincipalSet(groups, users);

        var before = await GetManagersAsync(ct);
        var row = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == ManagersSettingKey, ct);
        if (row is null)
        {
            row = new PlatformSetting { Key = ManagersSettingKey };
            _db.PlatformSettings.Add(row);
        }
        row.Value = JsonSerializer.Serialize(clean, Json);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = Actor;
        await _db.SaveChangesAsync(ct);
        _isReleaseManager = null;

        await _audit.Log(AuditModule, "release.managers-updated", _user.Id, _user.Name, "user",
            "ReleaseManagers", null, before, clean);
        await _events.PublishEntityChanged(new EntityChangedEvent { Entity = "release", Action = "managers-updated" });
        return clean;
    }

    /// <summary>
    /// Whether the caller is one of the release managers. An empty list makes nobody one — it never
    /// means everybody — and an administrator does not count as a member of every group here, so the
    /// answer says what the setting says.
    /// </summary>
    public async Task<bool> IsReleaseManagerAsync(CancellationToken ct)
    {
        if (_isReleaseManager is { } known) return known;
        var managers = await GetManagersAsync(ct);
        var email = !string.IsNullOrEmpty(_user.Email) ? _user.Email : _user.Id;
        _isReleaseManager = !managers.IsEmpty && !string.IsNullOrEmpty(email)
            && await _principals.IsInPrincipalSetAsync(managers.Groups, managers.Users, email, ct, allowAdminShortcut: false);
        return _isReleaseManager.Value;
    }

    /// <summary>Administrators and release managers open releases.</summary>
    public async Task<bool> CanCreateAsync(CancellationToken ct) => _user.IsAdmin || await IsReleaseManagerAsync(ct);

    /// <summary>Administrators, release managers, and the person named as this release's manager run it.</summary>
    public async Task<bool> CanManageAsync(Release release, CancellationToken ct) =>
        IsNamedManager(release) || await CanCreateAsync(ct);

    private bool IsNamedManager(Release release) =>
        !string.IsNullOrWhiteSpace(release.ReleaseManager)
        && (string.Equals(release.ReleaseManager, _user.Email, StringComparison.OrdinalIgnoreCase)
            || string.Equals(release.ReleaseManager, _user.Id, StringComparison.OrdinalIgnoreCase));

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<ReleaseListDto> ListAsync(string? status, string? product, int limit, int offset, CancellationToken ct)
    {
        var query = _db.Releases.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status == "active")
                query = query.Where(r => r.Status == ReleaseStatuses.Planned || r.Status == ReleaseStatuses.InProgress);
            else
                query = query.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(product))
        {
            // Products are a small JSON array of names; the quoted name is a whole element.
            var needle = JsonSerializer.Serialize(product.Trim());
            query = query.Where(r => r.ProductsJson.Contains(needle));
        }

        var total = await query.CountAsync(ct);
        // The ones being worked on first, then the ones coming up, then history, newest first.
        var rows = await query
            .OrderBy(r => r.Status == ReleaseStatuses.InProgress ? 0
                : r.Status == ReleaseStatuses.Planned ? 1
                : r.Status == ReleaseStatuses.Completed ? 2 : 3)
            .ThenByDescending(r => r.CompletedAt ?? r.CancelledAt ?? r.CreatedAt)
            .Skip(offset).Take(limit)
            .Include(r => r.Steps)
            .AsSplitQuery()
            .ToListAsync(ct);
        return new ReleaseListDto(rows.Select(ToSummary).ToList(), total, await CanCreateAsync(ct));
    }

    public async Task<ReleaseDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await Load(id, tracking: false, ct);
        return row is null ? null : await ToDtoAsync(row, ct);
    }

    internal Task<Release?> Load(Guid id, bool tracking, CancellationToken ct)
    {
        var query = _db.Releases.Include(r => r.Steps).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return query.FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    // ── Release writes ───────────────────────────────────────────────────────

    public async Task<ReleaseDto> CreateAsync(CreateReleaseRequest request, CancellationToken ct)
    {
        if (!await CanCreateAsync(ct))
            throw new ReleaseForbiddenException("Only administrators and release managers can open a release.");

        var clean = await CleanAsync(
            request.Version, request.Products, request.Title, request.StagingEnvironment, request.ProductionEnvironment,
            request.TargetDate, request.ReleaseManager, request.ReleaseManagerName, request.Notes,
            request.IncludedServices, request.ExcludedServices, request.StartedAt, request.Links, ct);
        await EnsureUniqueAsync(clean, exceptId: null, ct);

        var now = DateTimeOffset.UtcNow;
        var row = new Release
        {
            Id = Guid.NewGuid(),
            CreatedBy = Actor,
            CreatedByName = _user.Name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(row, clean);
        // An unnamed release manager is the person opening the release.
        if (row.ReleaseManager is null)
        {
            row.ReleaseManager = Actor;
            row.ReleaseManagerName = string.IsNullOrWhiteSpace(_user.Name) ? null : _user.Name;
        }
        row.Steps = ReleaseStepTemplate.Build(row.Id, row.Version, now);
        row.Status = DeriveStatus(row, now);

        _db.Releases.Add(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.created", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, null, Snapshot(row));
        await PublishAsync(row, "created");
        return await ToDtoAsync(row, ct);
    }

    public async Task<ReleaseDto?> UpdateAsync(Guid id, UpdateReleaseRequest request, CancellationToken ct)
    {
        var row = await Load(id, tracking: true, ct);
        if (row is null) return null;
        await EnsureCanManageAsync(row, ct);

        var clean = await CleanAsync(
            request.Version, request.Products, request.Title, request.StagingEnvironment, request.ProductionEnvironment,
            request.TargetDate, request.ReleaseManager, request.ReleaseManagerName, request.Notes,
            request.IncludedServices, request.ExcludedServices, request.StartedAt, request.Links, ct);
        await EnsureUniqueAsync(clean, exceptId: id, ct);

        var before = Snapshot(row);
        var versionChanged = !string.Equals(row.Version, clean.Version, StringComparison.Ordinal);
        var oldVersion = row.Version;
        Apply(row, clean);
        // A blank release manager on update means "nobody named", not "whoever is editing".
        if (versionChanged) RetitleTemplateSteps(row, oldVersion);
        Touch(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.updated", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, before, Snapshot(row));
        await PublishAsync(row, "updated");
        return await ToDtoAsync(row, ct);
    }

    public async Task<ReleaseDto?> CompleteAsync(Guid id, CompleteReleaseRequest request, CancellationToken ct)
    {
        var row = await Load(id, tracking: true, ct);
        if (row is null) return null;
        await EnsureCanManageAsync(row, ct);
        if (row.CancelledAt is not null)
            throw new ReleaseValidationException("A cancelled release cannot be completed. Reopen it first.");

        var now = DateTimeOffset.UtcNow;
        var completedAt = request.CompletedAt ?? now;
        NotInFuture(completedAt, "completedAt", now);
        if (row.StartedAt is not null && completedAt < row.StartedAt)
            throw new ReleaseValidationException("'completedAt' must not be before the release started.");

        var before = Snapshot(row);
        row.CompletedAt = completedAt;
        Touch(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.completed", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, before, Snapshot(row));
        await PublishAsync(row, "completed");
        return await ToDtoAsync(row, ct);
    }

    public async Task<ReleaseDto?> CancelAsync(Guid id, CancelReleaseRequest request, CancellationToken ct)
    {
        var row = await Load(id, tracking: true, ct);
        if (row is null) return null;
        await EnsureCanManageAsync(row, ct);
        var reason = Required(request?.Reason, "reason", 2000);
        if (row.CompletedAt is not null)
            throw new ReleaseValidationException("A completed release cannot be cancelled. Reopen it first.");

        var before = Snapshot(row);
        row.CancelledAt = DateTimeOffset.UtcNow;
        row.CancelReason = reason;
        Touch(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.cancelled", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, before, Snapshot(row));
        await PublishAsync(row, "cancelled");
        return await ToDtoAsync(row, ct);
    }

    public async Task<ReleaseDto?> ReopenAsync(Guid id, CancellationToken ct)
    {
        var row = await Load(id, tracking: true, ct);
        if (row is null) return null;
        await EnsureCanManageAsync(row, ct);

        var before = Snapshot(row);
        row.CompletedAt = null;
        row.CancelledAt = null;
        row.CancelReason = null;
        Touch(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.reopened", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, before, Snapshot(row));
        await PublishAsync(row, "reopened");
        return await ToDtoAsync(row, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!_user.IsAdmin)
            throw new ReleaseForbiddenException("Only administrators can delete a release. Cancel it instead.");
        var row = await Load(id, tracking: true, ct);
        if (row is null) return false;
        var snapshot = Snapshot(row);
        _db.Releases.Remove(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.deleted", _user.Id, _user.Name, "user",
            nameof(Release), id, snapshot, null);
        await PublishAsync(row, "deleted");
        return true;
    }

    // ── Step writes ──────────────────────────────────────────────────────────

    public async Task<ReleaseDto?> UpdateStepAsync(Guid id, Guid stepId, UpdateReleaseStepRequest request, CancellationToken ct)
    {
        var row = await Load(id, tracking: true, ct);
        if (row is null) return null;
        var step = row.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null) return null;
        await EnsureCanManageAsync(row, ct);
        if (request is null) throw new ReleaseValidationException("request body is required");

        var now = DateTimeOffset.UtcNow;
        var before = StepSnapshot(step);
        if (request.StartedAt is { } s) NotInFuture(s, "startedAt", now);
        if (request.CompletedAt is { } c) NotInFuture(c, "completedAt", now);

        var status = request.Status is null ? step.Status : request.Status.Trim().ToLowerInvariant();
        if (!ReleaseStepStatuses.All.Contains(status))
            throw new ReleaseValidationException($"'status' must be one of {string.Join(", ", ReleaseStepStatuses.All)}.");
        var statusChanged = status != step.Status;

        switch (status)
        {
            case ReleaseStepStatuses.Pending:
                if (request.StartedAt is not null || request.CompletedAt is not null)
                    throw new ReleaseValidationException("A pending step has no dates.");
                step.StartedAt = null;
                step.CompletedAt = null;
                step.CompletedBy = null;
                step.CompletedByName = null;
                break;

            case ReleaseStepStatuses.InProgress:
                if (request.CompletedAt is not null)
                    throw new ReleaseValidationException("A step in progress has no completion date.");
                step.StartedAt = request.StartedAt ?? step.StartedAt ?? now;
                step.CompletedAt = null;
                step.CompletedBy = null;
                step.CompletedByName = null;
                break;

            case ReleaseStepStatuses.Done:
            case ReleaseStepStatuses.Skipped:
                // Re-saving a closed step keeps its completion unless a new one is given.
                step.CompletedAt = request.CompletedAt ?? (statusChanged ? null : step.CompletedAt) ?? now;
                // A step marked done in one go started when it finished; skipping never starts one.
                step.StartedAt = request.StartedAt ?? step.StartedAt
                    ?? (status == ReleaseStepStatuses.Done ? step.CompletedAt : null);
                if (statusChanged || request.CompletedAt is not null)
                {
                    step.CompletedBy = Actor;
                    step.CompletedByName = string.IsNullOrWhiteSpace(_user.Name) ? null : _user.Name;
                }
                break;
        }
        if (step.StartedAt is not null && step.CompletedAt is not null && step.CompletedAt < step.StartedAt)
            throw new ReleaseValidationException("'completedAt' must not be before 'startedAt'.");
        step.Status = status;

        if (request.Note is not null) step.Note = Optional(request.Note, "note", 4000);
        if (request.Links is not null) step.LinksJson = JsonSerializer.Serialize(CleanLinks(request.Links, "links"), Json);
        if (request.Title is not null) step.Title = Required(request.Title, "title", 300);
        if (request.Description is not null) step.Description = Optional(request.Description, "description", 2000);
        if (request.Owner is not null) step.Owner = Optional(request.Owner, "owner", 100);
        step.UpdatedAt = now;

        // The first step that gets going starts the release, at the time the step says it did.
        if (row.StartedAt is null && step.Status != ReleaseStepStatuses.Pending && step.Status != ReleaseStepStatuses.Skipped)
            row.StartedAt = step.StartedAt ?? step.CompletedAt ?? now;
        Touch(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.step-updated", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, before, StepSnapshot(step), new { stepId = step.Id, step.Key });
        await PublishAsync(row, "updated");
        return await ToDtoAsync(row, ct);
    }

    public async Task<ReleaseDto?> AddStepAsync(Guid id, AddReleaseStepRequest request, CancellationToken ct)
    {
        var row = await Load(id, tracking: true, ct);
        if (row is null) return null;
        await EnsureCanManageAsync(row, ct);
        if (request is null) throw new ReleaseValidationException("request body is required");
        if (row.Steps.Count >= 50) throw new ReleaseValidationException("A release has at most 50 steps.");

        var title = Required(request.Title, "title", 300);
        var ordered = row.Steps.OrderBy(s => s.Order).ToList();
        int order;
        if (request.AfterStepId is { } afterId)
        {
            var index = ordered.FindIndex(s => s.Id == afterId);
            if (index < 0) throw new ReleaseValidationException("'afterStepId' is not a step of this release.");
            // Renumber so there is always room: steps are few and orders are only ever compared.
            for (var i = 0; i < ordered.Count; i++) ordered[i].Order = (i + 1) * 10;
            order = ordered[index].Order + 5;
        }
        else
        {
            order = (ordered.Count == 0 ? 0 : ordered[^1].Order) + 10;
        }

        var now = DateTimeOffset.UtcNow;
        var step = new ReleaseStep
        {
            Id = Guid.NewGuid(),
            ReleaseId = row.Id,
            Order = order,
            Key = $"custom-{Guid.NewGuid():N}"[..15],
            Title = title,
            Description = Optional(request.Description, "description", 2000),
            Owner = Optional(request.Owner, "owner", 100),
            Status = ReleaseStepStatuses.Pending,
            Custom = true,
            UpdatedAt = now,
        };
        // Added through the DbSet: a new child appended to a tracked collection whose key is already
        // set would be taken for an existing row and updated rather than inserted.
        _db.ReleaseSteps.Add(step);
        Touch(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.step-added", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, null, StepSnapshot(step), new { stepId = step.Id });
        await PublishAsync(row, "updated");
        return await ToDtoAsync(row, ct);
    }

    public async Task<ReleaseDto?> DeleteStepAsync(Guid id, Guid stepId, CancellationToken ct)
    {
        var row = await Load(id, tracking: true, ct);
        if (row is null) return null;
        var step = row.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null) return null;
        await EnsureCanManageAsync(row, ct);
        if (!step.Custom)
            throw new ReleaseValidationException("Only steps added to this release can be removed. Skip a standard step instead.");

        var snapshot = StepSnapshot(step);
        row.Steps.Remove(step);
        _db.ReleaseSteps.Remove(step);
        Touch(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "release.step-removed", _user.Id, _user.Name, "user",
            nameof(Release), row.Id, snapshot, null, new { stepId });
        await PublishAsync(row, "updated");
        return await ToDtoAsync(row, ct);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private sealed record Clean(
        string Version, string Title, List<string> Products, string StagingEnvironment, string ProductionEnvironment,
        DateOnly? TargetDate, string? ReleaseManager, string? ReleaseManagerName, string? Notes,
        List<string> IncludedServices, List<string> ExcludedServices, DateTimeOffset? StartedAt, List<ReleaseLink> Links);

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}$")]
    private static partial Regex VersionLine();

    /// <summary>"5.10" from "5.10", "v5.10" or " 5.10 ". Null when it is not a major.minor line.</summary>
    public static string? NormalizeVersion(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        return VersionLine().IsMatch(v) ? v : null;
    }

    private async Task<Clean> CleanAsync(
        string? version, List<string>? products, string? title, string? staging, string? production,
        DateOnly? targetDate, string? releaseManager, string? releaseManagerName, string? notes,
        List<string>? included, List<string>? excluded, DateTimeOffset? startedAt, List<ReleaseLink>? links,
        CancellationToken ct)
    {
        var line = NormalizeVersion(version)
            ?? throw new ReleaseValidationException("'version' must be a release line such as 5.10 or 6.0.");

        var productList = Names(products, "products", 20);
        if (productList.Count == 0) throw new ReleaseValidationException("'products' must name at least one product.");

        var stagingEnv = await _environments.ResolveAsync(Optional(staging, "stagingEnvironment", 100) ?? "staging", ct);
        var productionEnv = await _environments.ResolveAsync(Optional(production, "productionEnvironment", 100) ?? "prod", ct);
        if (string.Equals(stagingEnv, productionEnv, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseValidationException("The staging and production environments must differ.");

        if (startedAt is { } s) NotInFuture(s, "startedAt", DateTimeOffset.UtcNow);

        var includedList = Names(included, "includedServices", MaxServiceNames);
        var excludedList = Names(excluded, "excludedServices", MaxServiceNames);
        var both = includedList.Intersect(excludedList, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (both is not null)
            throw new ReleaseValidationException($"'{both}' cannot be both included in and excluded from the release.");

        return new Clean(
            line,
            Optional(title, "title", 200) ?? $"Release {line}",
            productList,
            stagingEnv,
            productionEnv,
            targetDate,
            Optional(releaseManager, "releaseManager", 300),
            Optional(releaseManagerName, "releaseManagerName", 300),
            Optional(notes, "notes", 8000),
            includedList,
            excludedList,
            startedAt,
            CleanLinks(links, "links"));
    }

    /// <summary>
    /// One release per version line and product. Two products may run their own trains that both
    /// reach 5.10; the same product releasing 5.10 twice is a mistake. Cancelled releases don't count.
    /// </summary>
    private async Task EnsureUniqueAsync(Clean clean, Guid? exceptId, CancellationToken ct)
    {
        var sameLine = await _db.Releases.AsNoTracking()
            .Where(r => r.Version == clean.Version && r.Status != ReleaseStatuses.Cancelled && r.Id != exceptId)
            .Select(r => new { r.Id, r.ProductsJson })
            .ToListAsync(ct);
        foreach (var other in sameLine)
        {
            var shared = ParseList(other.ProductsJson).Intersect(clean.Products, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (shared is not null)
                throw new ReleaseConflictException($"{shared} already has a release {clean.Version}.", other.Id);
        }
    }

    private static List<string> Names(List<string>? values, string field, int max)
    {
        var list = (values ?? [])
            .Select(v => (v ?? "").Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count > max) throw new ReleaseValidationException($"'{field}' has more than {max} entries.");
        var tooLong = list.FirstOrDefault(v => v.Length > 200);
        if (tooLong is not null) throw new ReleaseValidationException($"'{field}' has an entry longer than 200 characters.");
        return list;
    }

    private static string Required(string? value, string field, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new ReleaseValidationException($"'{field}' is required.");
        if (v.Length > max) throw new ReleaseValidationException($"'{field}' is longer than {max} characters.");
        return v;
    }

    private static string? Optional(string? value, string field, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length > max) throw new ReleaseValidationException($"'{field}' is longer than {max} characters.");
        return v;
    }

    private static string? OptionalUrl(string? value, string field)
    {
        var url = Optional(value, field, 2000);
        if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")))
            throw new ReleaseValidationException($"'{field}' must be an absolute http(s) link.");
        return url;
    }

    private const int MaxLinks = 20;

    /// <summary>Absolute http(s) links, each at most once, with an optional label.</summary>
    private static List<ReleaseLink> CleanLinks(List<ReleaseLink>? links, string field)
    {
        var clean = new List<ReleaseLink>();
        foreach (var link in links ?? [])
        {
            if (link is null) continue;
            var url = OptionalUrl(link.Url, $"{field}.url");
            if (url is null) continue;
            if (clean.Any(l => string.Equals(l.Url, url, StringComparison.Ordinal))) continue;
            clean.Add(new ReleaseLink(Optional(link.Label, $"{field}.label", 200), url));
        }
        if (clean.Count > MaxLinks) throw new ReleaseValidationException($"'{field}' has more than {MaxLinks} links.");
        return clean;
    }

    internal static List<ReleaseLink> ParseLinks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ReleaseLink>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static void NotInFuture(DateTimeOffset value, string field, DateTimeOffset now)
    {
        if (value > now + FutureSlack) throw new ReleaseValidationException($"'{field}' must not be in the future.");
    }

    private static void Apply(Release row, Clean c)
    {
        row.Version = c.Version;
        row.Title = c.Title;
        row.ProductsJson = JsonSerializer.Serialize(c.Products, Json);
        row.StagingEnvironment = c.StagingEnvironment;
        row.ProductionEnvironment = c.ProductionEnvironment;
        row.TargetDate = c.TargetDate;
        row.ReleaseManager = c.ReleaseManager;
        row.ReleaseManagerName = c.ReleaseManager is null ? null : c.ReleaseManagerName;
        row.Notes = c.Notes;
        row.IncludedServicesJson = JsonSerializer.Serialize(c.IncludedServices, Json);
        row.ExcludedServicesJson = JsonSerializer.Serialize(c.ExcludedServices, Json);
        row.LinksJson = JsonSerializer.Serialize(c.Links, Json);
        row.StartedAt = c.StartedAt ?? row.StartedAt;
    }

    /// <summary>Template steps name the version; keep them truthful when the version is corrected.</summary>
    private static void RetitleTemplateSteps(Release row, string oldVersion)
    {
        var byKey = ReleaseStepTemplate.Default(oldVersion).ToDictionary(d => d.Key);
        var fresh = ReleaseStepTemplate.Default(row.Version).ToDictionary(d => d.Key);
        foreach (var step in row.Steps.Where(s => !s.Custom))
        {
            if (!byKey.TryGetValue(step.Key, out var old) || !fresh.TryGetValue(step.Key, out var now)) continue;
            if (step.Title == old.Title) step.Title = now.Title;
            if (step.Description == old.Description) step.Description = now.Description;
        }
    }

    private async Task EnsureCanManageAsync(Release row, CancellationToken ct)
    {
        if (!await CanManageAsync(row, ct))
            throw new ReleaseForbiddenException("Only administrators and release managers can change this release.");
    }

    private void Touch(Release row)
    {
        var now = DateTimeOffset.UtcNow;
        row.UpdatedBy = Actor;
        row.UpdatedAt = now;
        row.Status = DeriveStatus(row, now);
    }

    /// <summary>
    /// Cancelled and completed are the release manager's calls; otherwise a release is in progress
    /// once its line has been cut or any step has got going, and planned until then.
    /// </summary>
    public static string DeriveStatus(Release r, DateTimeOffset now)
    {
        if (r.CancelledAt is not null) return ReleaseStatuses.Cancelled;
        if (r.CompletedAt is not null) return ReleaseStatuses.Completed;
        if ((r.StartedAt is not null && r.StartedAt <= now)
            || r.Steps.Any(s => s.Status is ReleaseStepStatuses.InProgress or ReleaseStepStatuses.Done))
            return ReleaseStatuses.InProgress;
        return ReleaseStatuses.Planned;
    }

    private string Actor => !string.IsNullOrEmpty(_user.Email) ? _user.Email : _user.Id;

    private Task PublishAsync(Release row, string action) =>
        _events.PublishEntityChanged(new EntityChangedEvent { Entity = "release", Action = action, Id = row.Id.ToString() });

    internal static List<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static object Snapshot(Release r) => new
    {
        r.Version, r.Title, Products = ParseList(r.ProductsJson), r.StagingEnvironment, r.ProductionEnvironment,
        r.TargetDate, r.ReleaseManager, r.Status, r.StartedAt, r.CompletedAt, r.CancelledAt, r.CancelReason,
        IncludedServices = ParseList(r.IncludedServicesJson), ExcludedServices = ParseList(r.ExcludedServicesJson),
        Links = ParseLinks(r.LinksJson),
    };

    private static object StepSnapshot(ReleaseStep s) => new
    {
        s.Key, s.Title, s.Status, s.StartedAt, s.CompletedAt, s.CompletedBy, s.Note, Links = ParseLinks(s.LinksJson),
    };

    private static List<ReleaseStep> Ordered(Release r) => r.Steps.OrderBy(s => s.Order).ThenBy(s => s.Title).ToList();

    public static ReleaseStepDto ToStepDto(ReleaseStep s) => new(
        s.Id, s.Order, s.Key, s.Title, s.Description, s.Owner, s.Check, s.Status,
        s.StartedAt, s.CompletedAt, s.CompletedBy, s.CompletedByName, s.Note, ParseLinks(s.LinksJson), s.Custom);

    public static ReleaseSummaryDto ToSummary(Release r)
    {
        var steps = Ordered(r);
        return new ReleaseSummaryDto(
            r.Id, r.Version, r.Title, ParseList(r.ProductsJson), r.Status, r.TargetDate,
            r.ReleaseManager, r.ReleaseManagerName, r.StartedAt, r.CompletedAt, r.CancelledAt,
            steps.Count(s => ReleaseStepStatuses.IsClosed(s.Status)), steps.Count,
            steps.FirstOrDefault(s => !ReleaseStepStatuses.IsClosed(s.Status))?.Title,
            r.CreatedAt, r.UpdatedAt);
    }

    public async Task<ReleaseDto> ToDtoAsync(Release r, CancellationToken ct) => new(
        r.Id, r.Version, r.Title, ParseList(r.ProductsJson), r.StagingEnvironment, r.ProductionEnvironment,
        r.TargetDate, r.ReleaseManager, r.ReleaseManagerName, r.Status, r.Notes, ParseLinks(r.LinksJson),
        ParseList(r.IncludedServicesJson), ParseList(r.ExcludedServicesJson),
        r.StartedAt, r.CompletedAt, r.CancelledAt, r.CancelReason,
        r.CreatedBy, r.CreatedByName, r.CreatedAt, r.UpdatedBy, r.UpdatedAt,
        Ordered(r).Select(ToStepDto).ToList(),
        await CanManageAsync(r, ct));
}
