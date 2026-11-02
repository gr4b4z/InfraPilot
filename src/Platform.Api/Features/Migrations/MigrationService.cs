using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Migrations.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Migrations;

public sealed class MigrationValidationException(string message) : Exception(message);

/// <summary>
/// Database migrations of deployed versions — read-only in the portal. Two ways in:
/// <list type="bullet">
///   <item><b>Reported</b> by a pipeline through <c>POST /api/migrations/runs</c>
///   (<see cref="ReportAsync"/>): how data migrations arrive, Pending when a deploy queues one and
///   again as it runs and finishes.</item>
///   <item><b>Derived</b> from a deploy event's own captured output (<see cref="DeriveFromDeployAsync"/>):
///   a schema migration runs as a Helm hook inside the deployment, and the release pipelines already
///   send that hook's pod log with the event, so the portal reads the migration out of it rather than
///   asking every pipeline to report it twice.</item>
/// </list>
/// Either way the individual migrations — which ran, which failed — are read from the migration
/// library's log lines unless the producer listed them (<see cref="MigrationLogParser"/>).
/// </summary>
public class MigrationService
{
    /// <summary>Same cap as deploy logs, for the same reason: a failing run prints its cause last.</summary>
    public const int MaxLogsPerRun = DeploymentService.MaxLogsPerEvent;

    /// <summary>The <see cref="MigrationRun.RunKey"/> prefix of a schema migration read out of a deploy event's logs.</summary>
    public const string DerivedRunKeyPrefix = "deploy:";

    /// <summary>Name of the log block a derived schema migration stores the extracted hook output under.</summary>
    public const string DerivedLogName = "schema migration output";

    /// <summary>
    /// How far after a migration's start the deploy it belongs to may be stamped. Producers stamp
    /// <c>deployedAt</c> at the start of the deploy, and a schema migration runs inside it, so the
    /// deploy is normally a little <i>earlier</i>; the slack covers producers that stamp at the end.
    /// </summary>
    private static readonly TimeSpan LinkSlack = TimeSpan.FromHours(1);

    private readonly PlatformDbContext _db;
    private readonly IOptionsMonitor<NormalizationOptions> _normalization;
    private readonly EnvironmentAliasResolver _environments;
    private readonly ServiceProductOverrideService _productOverrides;
    private readonly IPlatformEventPublisher _events;
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(
        PlatformDbContext db,
        IOptionsMonitor<NormalizationOptions> normalization,
        EnvironmentAliasResolver environments,
        ServiceProductOverrideService productOverrides,
        IPlatformEventPublisher events,
        ILogger<MigrationService> logger)
    {
        _db = db;
        _normalization = normalization;
        _environments = environments;
        _productOverrides = productOverrides;
        _events = events;
        _logger = logger;
    }

    // --- Reporting ---

    public record ReportResult(MigrationRun Run, bool Created);

    /// <summary>
    /// Records what a pipeline reports about one migration, upserting on
    /// <c>(kind, product, service, environment, version, runKey)</c>. Statuses only move forward: a
    /// replayed Pending never reopens a migration that has since run, and a Superseded one stays
    /// superseded unless it is actually run again. A data migration then supersedes every older
    /// Pending or Failed one of the same service in the same environment.
    /// </summary>
    public async Task<ReportResult> ReportAsync(ReportMigrationRunDto dto, CancellationToken ct = default)
    {
        Validate(dto);

        var kind = dto.Kind.Trim().ToLowerInvariant();
        var status = dto.Status.Trim().ToLowerInvariant();
        var environment = await _environments.ResolveAsync(
            _normalization.CurrentValue.ApplyEnvironment(dto.Environment), ct);
        // The same admin override deploy ingest applies, so a migration files under the product its
        // deployment does.
        var product = await _productOverrides.ResolveProductAsync(dto.Product, dto.Service, ct);
        var runKey = dto.RunKey?.Trim() ?? "";
        var now = DateTimeOffset.UtcNow;

        var run = await _db.MigrationRuns.FirstOrDefaultAsync(r =>
            r.Kind == kind && r.Product == product && r.Service == dto.Service
            && r.Environment == environment && r.Version == dto.Version && r.RunKey == runKey, ct);
        var created = run is null;
        if (run is null)
        {
            run = new MigrationRun
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Product = product,
                Service = dto.Service,
                Environment = environment,
                Version = dto.Version,
                RunKey = runKey,
                Status = status,
                Source = dto.Source,
                QueuedAt = dto.QueuedAt ?? dto.StartedAt ?? now,
                CreatedAt = now,
            };
            _db.MigrationRuns.Add(run);
        }
        else
        {
            run.Status = NextStatus(run.Status, status);
            // The first report decides when it became due; later ones may only say it was earlier.
            if (dto.QueuedAt is { } queuedAt && queuedAt < run.QueuedAt) run.QueuedAt = queuedAt;
        }

        run.Source = dto.Source;
        if (dto.SkipReason is not null) run.SkipReason = dto.SkipReason;
        if (dto.Run is not null) run.Run = dto.Run;
        ApplyTiming(run, status, dto.StartedAt, dto.CompletedAt, now);

        // Listed by the producer wins; otherwise read off the log, and a log with no migration lines
        // in it (a pod that died before the library started) keeps what an earlier report found.
        var parsed = MigrationLogParser.Parse(string.Join("\n", (dto.Logs ?? []).Select(l => l.Content ?? "")));
        List<MigrationItem>? items = dto.Migrations is { Count: > 0 }
            ? dto.Migrations.Select(CleanItem).ToList()
            : parsed.Items.Count > 0 ? parsed.Items : null;
        if (items is not null) await ReplaceItemsAsync(run, items, ct);

        var stillFailing = run.Status is MigrationRunStatus.Failed
            || (run.Status == MigrationRunStatus.Superseded && status == MigrationRunStatus.Failed);
        run.FailureReason = stillFailing
            ? Truncate(dto.Run?.FailureReason?.Trim() is { Length: > 0 } stated ? stated
                : parsed.FailureReason ?? items?.FirstOrDefault(i => i.Status == MigrationItemStatus.Failed)?.Error
                ?? run.FailureReason, 2000)
            : run.Status == MigrationRunStatus.Superseded ? run.FailureReason : null;

        run.DeployEventId = dto.DeployEventId is { } given && await _db.DeployEvents.AnyAsync(e => e.Id == given, ct)
            ? given
            : run.DeployEventId ?? await FindDeployEventAsync(run, ct);
        run.UpdatedAt = now;

        await SyncLogsAsync(run.Id, dto.Logs, ct);
        if (kind == MigrationKinds.Data) await ApplySupersedeAsync(run, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Migration {Kind} {Product}/{Service} → {Environment} v{Version}: {Status} ({Applied}/{Total} applied, {Failed} failed)",
            run.Kind, LogSanitizer.Clean(run.Product), LogSanitizer.Clean(run.Service),
            LogSanitizer.Clean(run.Environment), LogSanitizer.Clean(run.Version), run.Status,
            run.AppliedCount, run.TotalCount, run.FailedCount);

        await PublishAsync(run, created ? "created" : "updated");
        return new ReportResult(run, created);
    }

    private static readonly HashSet<string> Kinds = new(MigrationKinds.All, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReportableStatuses = new(MigrationRunStatus.Reportable, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ItemStatuses = new(MigrationItemStatus.All, StringComparer.OrdinalIgnoreCase);

    public static List<string> ValidationErrors(ReportMigrationRunDto? dto)
    {
        var errors = new List<string>();
        if (dto is null) return ["request body is required"];
        if (string.IsNullOrWhiteSpace(dto.Kind) || !Kinds.Contains(dto.Kind.Trim()))
            errors.Add($"'kind' must be one of: {string.Join(", ", MigrationKinds.All)}");
        if (string.IsNullOrWhiteSpace(dto.Product)) errors.Add("'product' is required");
        if (string.IsNullOrWhiteSpace(dto.Service)) errors.Add("'service' is required");
        if (string.IsNullOrWhiteSpace(dto.Environment)) errors.Add("'environment' is required");
        if (string.IsNullOrWhiteSpace(dto.Version)) errors.Add("'version' is required");
        if (string.IsNullOrWhiteSpace(dto.Source)) errors.Add("'source' is required");
        if (string.IsNullOrWhiteSpace(dto.Status) || !ReportableStatuses.Contains(dto.Status.Trim()))
            errors.Add($"'status' must be one of: {string.Join(", ", MigrationRunStatus.Reportable)}");
        if (dto.RunKey is { Length: > 100 }) errors.Add("'runKey' must be at most 100 characters");
        if (dto.RunKey?.StartsWith(DerivedRunKeyPrefix, StringComparison.OrdinalIgnoreCase) == true)
            errors.Add($"'runKey' must not start with '{DerivedRunKeyPrefix}' — that prefix is reserved for migrations read from deploy logs");
        for (var i = 0; i < (dto.Logs?.Count ?? 0); i++)
        {
            if (string.IsNullOrWhiteSpace(dto.Logs![i].Name)) errors.Add($"'logs[{i}].name' is required");
        }
        for (var i = 0; i < (dto.Migrations?.Count ?? 0); i++)
        {
            var item = dto.Migrations![i];
            if (string.IsNullOrWhiteSpace(item.Name)) errors.Add($"'migrations[{i}].name' is required");
            if (string.IsNullOrWhiteSpace(item.Status) || !ItemStatuses.Contains(item.Status.Trim()))
                errors.Add($"'migrations[{i}].status' must be one of: {string.Join(", ", MigrationItemStatus.All)}");
        }
        return errors;
    }

    private static void Validate(ReportMigrationRunDto dto)
    {
        var errors = ValidationErrors(dto);
        if (errors.Count > 0) throw new MigrationValidationException(string.Join("; ", errors));
    }

    /// <summary>
    /// Where a stored status goes when a producer reports <paramref name="incoming"/>. Pending only
    /// ever opens a migration — a re-sent "queued" must not undo a run that already happened. A
    /// superseded migration stays superseded unless someone actually runs it (Running / Succeeded):
    /// a late "failed" from an old leg does not bring it back.
    /// </summary>
    internal static string NextStatus(string stored, string incoming)
    {
        if (incoming == MigrationRunStatus.Pending)
            return stored == MigrationRunStatus.Superseded ? stored : stored is MigrationRunStatus.Pending ? incoming : stored;
        if (stored == MigrationRunStatus.Superseded && incoming is not (MigrationRunStatus.Running or MigrationRunStatus.Succeeded))
            return stored;
        return incoming;
    }

    /// <summary>
    /// Start/finish times and the attempt count. A new start time (or a first start) is a new attempt;
    /// a finished status with no start reported counts as one attempt so a producer that only reports
    /// outcomes still shows how often the migration ran.
    /// </summary>
    private static void ApplyTiming(MigrationRun run, string status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, DateTimeOffset now)
    {
        var ran = status is MigrationRunStatus.Running or MigrationRunStatus.Succeeded or MigrationRunStatus.Failed;
        if (startedAt is { } started && started != run.StartedAt)
        {
            if (run.StartedAt is null || started > run.StartedAt) run.Attempts++;
            run.StartedAt = started;
            run.CompletedAt = null;
        }
        else if (ran && run.Attempts == 0)
        {
            run.Attempts = 1;
            run.StartedAt ??= status == MigrationRunStatus.Running ? now : completedAt;
        }

        if (status is MigrationRunStatus.Succeeded or MigrationRunStatus.Failed or MigrationRunStatus.Skipped)
            run.CompletedAt = completedAt ?? run.CompletedAt ?? now;
        else if (status == MigrationRunStatus.Running)
            run.CompletedAt = null;
    }

    private static MigrationItem CleanItem(MigrationItem i) => new(
        Truncate(i.Name.Trim(), 300)!,
        i.Status.Trim().ToLowerInvariant(),
        i.DurationMs is >= 0 ? i.DurationMs : null,
        Truncate(i.Error?.Trim(), 2000),
        i.Order,
        i.AppliedAt);

    /// <summary>
    /// Replaces a run's migrations with <paramref name="items"/> and refreshes its counts. The list is
    /// always a whole picture — the library re-prints its full table — so there is nothing to merge.
    /// Returns false when the stored list was already the same. Caller owns <c>SaveChangesAsync</c>.
    /// </summary>
    private async Task<bool> ReplaceItemsAsync(MigrationRun run, List<MigrationItem> items, CancellationToken ct)
    {
        var existing = await _db.MigrationRunItems.Where(i => i.MigrationRunId == run.Id).OrderBy(i => i.Sequence).ToListAsync(ct);
        if (existing.Select(e => e.ToItem()).SequenceEqual(items)) return false;

        _db.MigrationRunItems.RemoveRange(existing);
        _db.MigrationRunItems.AddRange(items.Select((item, index) => new MigrationRunItem
        {
            Id = Guid.NewGuid(),
            MigrationRunId = run.Id,
            Sequence = index,
            Name = Truncate(item.Name, 300)!,
            Status = item.Status,
            Order = item.Order,
            DurationMs = item.DurationMs,
            Error = Truncate(item.Error, 2000),
            AppliedAt = item.AppliedAt,
        }));
        run.AppliedCount = items.Count(i => i.Status == MigrationItemStatus.Applied);
        run.FailedCount = items.Count(i => i.Status == MigrationItemStatus.Failed);
        run.TotalCount = items.Count;
        return true;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    /// <summary>
    /// The deploy event a migration belongs to: same service, environment and version, the latest one
    /// that started no later than the migration did (plus <see cref="LinkSlack"/>). A data migration
    /// only follows a successful deploy, so it links to the latest succeeded one. Falls back to the
    /// latest deploy of the version at all when the timing doesn't line up (backfills, clock skew).
    /// </summary>
    private async Task<Guid?> FindDeployEventAsync(MigrationRun run, CancellationToken ct)
    {
        var sameVersion = _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == run.Product && e.Service == run.Service
                     && e.Environment == run.Environment && e.Version == run.Version
                     && e.Status != DeployEventStatuses.Removed);
        if (run.Kind == MigrationKinds.Data) sameVersion = sameVersion.Where(e => e.Status == "succeeded");

        var anchor = (run.StartedAt ?? run.QueuedAt) + LinkSlack;
        return await sameVersion.Where(e => e.DeployedAt <= anchor)
                   .OrderByDescending(e => e.DeployedAt).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct)
               ?? await sameVersion.OrderByDescending(e => e.DeployedAt).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Only the newest data migration of a service in an environment ever runs, as with promotions.
    /// Marks every older Pending/Failed one superseded by <paramref name="run"/>; and when a newer one
    /// already exists (reports arriving out of order, a backfill), supersedes <paramref name="run"/>
    /// itself instead. "Older" is by <see cref="MigrationRun.QueuedAt"/> — when each became due — not
    /// by version string, which has no reliable order across producers.
    /// <para>Caller owns the surrounding <c>SaveChangesAsync</c>.</para>
    /// </summary>
    private async Task ApplySupersedeAsync(MigrationRun run, CancellationToken ct)
    {
        var siblings = await _db.MigrationRuns
            .Where(r => r.Kind == MigrationKinds.Data && r.Id != run.Id
                     && r.Product == run.Product && r.Service == run.Service && r.Environment == run.Environment
                     && r.Status != MigrationRunStatus.Superseded)
            .ToListAsync(ct);

        var newer = siblings.Where(r => r.QueuedAt > run.QueuedAt).OrderByDescending(r => r.QueuedAt).FirstOrDefault();
        if (newer is not null)
        {
            if (MigrationRunStatus.Supersedable.Contains(run.Status))
            {
                run.Status = MigrationRunStatus.Superseded;
                run.SupersededById = newer.Id;
            }
            return;
        }

        foreach (var older in siblings.Where(r => r.QueuedAt <= run.QueuedAt && MigrationRunStatus.Supersedable.Contains(r.Status)))
        {
            older.Status = MigrationRunStatus.Superseded;
            older.SupersededById = run.Id;
            older.UpdatedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "Data migration {OldId} ({Service} v{OldVersion} in {Environment}) superseded by {NewId} (v{NewVersion})",
                older.Id, LogSanitizer.Clean(older.Service), LogSanitizer.Clean(older.Version),
                LogSanitizer.Clean(older.Environment), run.Id, LogSanitizer.Clean(run.Version));
        }
    }

    private async Task SyncLogsAsync(Guid runId, List<CreateDeployLogDto>? logs, CancellationToken ct)
    {
        if (logs is null || logs.Count == 0) return;

        var incoming = logs
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .GroupBy(l => l.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .Take(MaxLogsPerRun)
            .ToList();

        var existing = await _db.MigrationRunLogs.Where(l => l.MigrationRunId == runId).ToListAsync(ct);
        var byName = existing.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var nextSequence = existing.Count == 0 ? 0 : existing.Max(l => l.Sequence) + 1;

        foreach (var dto in incoming)
        {
            var (content, truncated, originalBytes) = DeploymentService.CapLogContent(dto.Content ?? "");
            truncated = truncated || dto.Truncated;
            if (!byName.TryGetValue(dto.Name.Trim(), out var row))
            {
                row = new MigrationRunLog
                {
                    Id = Guid.NewGuid(),
                    MigrationRunId = runId,
                    Name = dto.Name.Trim(),
                    Sequence = nextSequence++,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _db.MigrationRunLogs.Add(row);
            }
            row.Source = dto.Source;
            row.Content = content;
            row.Truncated = truncated;
            row.ByteCount = System.Text.Encoding.UTF8.GetByteCount(content);
            row.LineCount = DeploymentService.CountLines(content);
            row.OriginalByteCount = originalBytes;
        }
    }

    // --- Derived from deploy events ---

    /// <summary>
    /// Called by deploy ingest once an event and its logs are stored (new or replayed): reads the
    /// schema migration out of the captured hook output, and attaches migrations reported before
    /// their deployment arrived. Never throws — ingest must not fail because a log was unreadable.
    /// </summary>
    public async Task OnDeployIngestedAsync(DeployEvent ev, CancellationToken ct = default)
    {
        try
        {
            await DeriveFromDeployAsync(ev, ct);
            await AdoptOrphansAsync(ev, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record migrations for deploy event {EventId}", ev.Id);
            // Drop only what this hook staged: the context is the ingest request's, and the deploy
            // event itself is already saved and still in use by the caller.
            foreach (var entry in _db.ChangeTracker.Entries()
                         .Where(e => e.Entity is MigrationRun or MigrationRunItem or MigrationRunLog).ToList())
                entry.State = EntityState.Detached;
        }
    }

    public enum DeriveOutcome { None, Created, Updated, Unchanged }

    /// <summary>
    /// Reads a deploy event's schema migration out of the output it carries — the Helm hook pod logs
    /// (<c>helm hook logs</c>) or, when the hook failed, the failure diagnostics' dump of the
    /// migration job's pod. Upserts one schema migration per deploy event (run key
    /// <c>deploy:{eventId}</c>), with only the migration's own lines as its log: the diagnostics also
    /// hold pod descriptions, which can print a container's environment, and none of that belongs on
    /// the migrations page.
    /// </summary>
    public async Task<DeriveOutcome> DeriveFromDeployAsync(DeployEvent ev, CancellationToken ct = default)
    {
        if (ev.Status == DeployEventStatuses.Removed) return DeriveOutcome.None;

        var blocks = await _db.DeployEventLogs.AsNoTracking()
            .Where(l => l.DeployEventId == ev.Id)
            .OrderBy(l => l.Sequence)
            .Select(l => new { l.Name, l.Content })
            .ToListAsync(ct);
        var extracted = MigrationLogParser.ExtractSchemaMigration(blocks.Select(b => (b.Name, b.Content)), ev.Service);
        if (extracted is null) return DeriveOutcome.None;

        var parsed = MigrationLogParser.Parse(extracted.Content);
        var run = ev.Run;
        // Helm runs the deployment only once its pre-hooks succeed, so a deploy that went through had a
        // schema migration that did too, whatever an ERR line along the way said. A failed deploy is a
        // failed migration when the job is reported failed, the output shows it, or Helm's reason was
        // a hook.
        var failed = ev.Status != "succeeded"
                     && (extracted.JobFailed || (ev.Status == "failed"
                         && (parsed.Failed || MigrationLogParser.IsHookFailure(run?.FailureReason))));
        var status = failed ? MigrationRunStatus.Failed
            : ev.Status == "in_progress" ? MigrationRunStatus.Running
            : MigrationRunStatus.Succeeded;

        var runKey = DerivedRunKeyPrefix + ev.Id;
        var row = await _db.MigrationRuns.FirstOrDefaultAsync(r => r.Kind == MigrationKinds.Schema && r.RunKey == runKey, ct);
        var created = row is null;
        if (row is null)
        {
            row = new MigrationRun
            {
                Id = Guid.NewGuid(),
                Kind = MigrationKinds.Schema,
                RunKey = runKey,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.MigrationRuns.Add(row);
        }
        var statusChanged = row.Status != status;
        var itemsChanged = await ReplaceItemsAsync(row, parsed.Items, ct);
        var outcome = created ? DeriveOutcome.Created
            : statusChanged || itemsChanged ? DeriveOutcome.Updated
            : DeriveOutcome.Unchanged;

        row.Product = ev.Product;
        row.Service = ev.Service;
        row.Environment = ev.Environment;
        row.Version = ev.Version;
        row.Source = ev.Source;
        row.DeployEventId = ev.Id;
        row.Status = status;
        row.QueuedAt = ev.DeployedAt;
        row.StartedAt = run?.StartedAt ?? ev.DeployedAt;
        row.CompletedAt = status == MigrationRunStatus.Running ? null : run?.CompletedAt ?? ev.DeployedAt;
        row.Attempts = 1;
        row.RunJson = ev.RunJson;
        row.FailureReason = failed
            ? Truncate(parsed.FailureReason ?? run?.FailureReason, 2000)
            : null;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        if (outcome != DeriveOutcome.Unchanged)
        {
            await SyncLogsAsync(row.Id,
                [new CreateDeployLogDto(DerivedLogName, extracted.Source, extracted.Content)], ct);
        }
        await _db.SaveChangesAsync(ct);
        if (outcome != DeriveOutcome.Unchanged) await PublishAsync(row, outcome == DeriveOutcome.Created ? "created" : "updated");
        return outcome;
    }

    /// <summary>
    /// Links migrations that were reported before any deploy of their version existed — a pipeline
    /// that posts the migration before the deploy event, or a backfill — to the deploy that just
    /// arrived.
    /// </summary>
    private async Task AdoptOrphansAsync(DeployEvent ev, CancellationToken ct)
    {
        var orphans = await _db.MigrationRuns
            .Where(r => r.DeployEventId == null && r.Product == ev.Product && r.Service == ev.Service
                     && r.Environment == ev.Environment && r.Version == ev.Version)
            .ToListAsync(ct);
        if (orphans.Count == 0) return;
        foreach (var r in orphans)
        {
            if (r.Kind == MigrationKinds.Data && ev.Status != "succeeded") continue;
            r.DeployEventId = ev.Id;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Re-reads stored deploy logs for schema migrations — what an admin runs once after this feature
    /// ships, so the deployments recorded before it show their migrations too. Idempotent.
    /// </summary>
    public async Task<MigrationDeriveResultDto> DeriveSinceAsync(
        DateTimeOffset since, string? product, CancellationToken ct = default)
    {
        // Only events that carry output at all; the rest cannot hold a migration.
        var query = _db.DeployEvents.AsNoTracking()
            .Where(e => e.DeployedAt >= since && _db.DeployEventLogs.Any(l => l.DeployEventId == e.Id));
        if (!string.IsNullOrWhiteSpace(product)) query = query.Where(e => e.Product == product);
        var ids = await query.OrderBy(e => e.DeployedAt).Select(e => e.Id).ToListAsync(ct);

        int created = 0, updated = 0, unchanged = 0;
        foreach (var id in ids)
        {
            var ev = await _db.DeployEvents.AsNoTracking().FirstAsync(e => e.Id == id, ct);
            switch (await DeriveFromDeployAsync(ev, ct))
            {
                case DeriveOutcome.Created: created++; break;
                case DeriveOutcome.Updated: updated++; break;
                case DeriveOutcome.Unchanged: unchanged++; break;
            }
            _db.ChangeTracker.Clear();
        }
        return new MigrationDeriveResultDto(ids.Count, created, updated, unchanged);
    }

    private Task PublishAsync(MigrationRun run, string action) => Task.WhenAll(
        _events.PublishEntityChanged(new EntityChangedEvent
        {
            Entity = "migration", Action = action, Id = run.Id.ToString(),
            Product = run.Product, Service = run.Service, Environment = run.Environment,
        }),
        // The deployment page shows its migrations, and the matrix its flare.
        _events.PublishEntityChanged(new EntityChangedEvent
        {
            Entity = "deployment", Action = "updated", Id = run.DeployEventId?.ToString(),
            Product = run.Product, Environment = run.Environment,
        }));

    // --- Reads ---

    public async Task<MigrationRunListDto> ListAsync(
        string? product, string? service, string? environment, string? kind, string? status,
        string? version, Guid? deployEventId, DateTimeOffset? from, DateTimeOffset? to,
        int limit, int offset, CancellationToken ct = default)
    {
        var query = _db.MigrationRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(product)) query = query.Where(r => r.Product == product);
        if (!string.IsNullOrWhiteSpace(service)) query = query.Where(r => r.Service == service);
        var env = await _environments.ResolveFilterAsync(environment, ct);
        if (env is not null) query = query.Where(r => r.Environment == env);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(r => r.Kind == kind.ToLower());
        if (!string.IsNullOrWhiteSpace(version)) query = query.Where(r => r.Version == version);
        if (deployEventId is not null) query = query.Where(r => r.DeployEventId == deployEventId);
        if (from is not null) query = query.Where(r => r.QueuedAt >= from);
        if (to is not null) query = query.Where(r => r.QueuedAt < to);

        // "attention" is the page's default view: what is waiting or broken, superseded left out.
        var statuses = status?.ToLowerInvariant() switch
        {
            null or "" or "all" => null,
            "attention" => MigrationRunStatus.Attention,
            var s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        if (statuses is not null) query = query.Where(r => statuses.Contains(r.Status));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(r => r.QueuedAt).ThenBy(r => r.Kind)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);
        return new MigrationRunListDto(rows.Select(ToSummary).ToList(), total);
    }

    public async Task<MigrationRunDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var run = await _db.MigrationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return null;

        var logs = await _db.MigrationRunLogs.AsNoTracking()
            .Where(l => l.MigrationRunId == id)
            .OrderBy(l => l.Sequence)
            .Select(l => new DeployLogSummaryDto(l.Id, l.Name, l.Source, l.Sequence, l.ByteCount, l.LineCount, l.Truncated, l.CreatedAt))
            .ToListAsync(ct);

        MigrationDeploymentDto? deployment = null;
        if (run.DeployEventId is { } eventId)
        {
            deployment = await _db.DeployEvents.AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => new MigrationDeploymentDto(e.Id, e.Version, e.Status, e.Source, e.DeployedAt))
                .FirstOrDefaultAsync(ct);
        }

        var supersededBy = run.SupersededById is { } byId
            ? await _db.MigrationRuns.AsNoTracking().Where(r => r.Id == byId).FirstOrDefaultAsync(ct)
            : null;
        var supersedes = await _db.MigrationRuns.AsNoTracking()
            .Where(r => r.SupersededById == run.Id)
            .OrderByDescending(r => r.QueuedAt)
            .ToListAsync(ct);
        // The same kind of migration of the same service in the same environment, newest first — "has
        // this ever worked here".
        var history = await _db.MigrationRuns.AsNoTracking()
            .Where(r => r.Kind == run.Kind && r.Product == run.Product && r.Service == run.Service
                     && r.Environment == run.Environment)
            .OrderByDescending(r => r.QueuedAt)
            .Take(15)
            .ToListAsync(ct);

        var items = await _db.MigrationRunItems.AsNoTracking()
            .Where(i => i.MigrationRunId == id)
            .OrderBy(i => i.Sequence)
            .ToListAsync(ct);

        return new MigrationRunDetailDto(
            ToSummary(run), run.Run, items.Select(i => i.ToItem()).ToList(), logs, deployment,
            supersededBy is null ? null : ToSummary(supersededBy),
            supersedes.Select(ToSummary).ToList(),
            history.Select(ToSummary).ToList());
    }

    public async Task<DeployLogContentDto?> GetLogContentAsync(Guid runId, Guid logId, CancellationToken ct = default)
    {
        var log = await _db.MigrationRunLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == logId && l.MigrationRunId == runId, ct);
        return log is null
            ? null
            : new DeployLogContentDto(log.Id, log.Name, log.Source, log.Content, log.Truncated, log.OriginalByteCount);
    }

    /// <summary>
    /// Every migration that needs attention — Pending, Running or Failed — for the deployment views
    /// to flag. Bounded by product (and optionally service/environment); superseded and finished ones
    /// never appear, so the set stays small however long the history.
    /// </summary>
    public async Task<List<MigrationFlagDto>> GetFlagsAsync(
        string? product, string? service, string? environment, CancellationToken ct = default)
    {
        var query = _db.MigrationRuns.AsNoTracking().Where(r => MigrationRunStatus.Attention.Contains(r.Status));
        if (!string.IsNullOrWhiteSpace(product)) query = query.Where(r => r.Product == product);
        if (!string.IsNullOrWhiteSpace(service)) query = query.Where(r => r.Service == service);
        var env = await _environments.ResolveFilterAsync(environment, ct);
        if (env is not null) query = query.Where(r => r.Environment == env);

        return await query
            .OrderByDescending(r => r.QueuedAt)
            .Take(1000)
            .Select(r => new MigrationFlagDto(
                r.Id, r.Kind, r.Product, r.Service, r.Environment, r.Version, r.Status,
                r.DeployEventId, r.QueuedAt, r.FailedCount, r.FailureReason))
            .ToListAsync(ct);
    }

    /// <summary>
    /// "Has this migration run on staging?" Matches <paramref name="query"/> against every migration
    /// name on record — partial and fuzzy (<see cref="MigrationNameMatcher"/>), or the order number
    /// when the query is one — and reports, per matching name, each (service, environment) it was
    /// seen in and how it went there. Matching happens in memory over the distinct names, which stay
    /// in the thousands however many runs there are; only the matched names' rows are then read.
    /// </summary>
    public async Task<MigrationSearchResultDto> SearchAsync(
        string query, string? product, string? environment, string? kind, int maxNames, CancellationToken ct = default)
    {
        query = query.Trim();
        var runs = _db.MigrationRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(product)) runs = runs.Where(r => r.Product == product);
        var env = await _environments.ResolveFilterAsync(environment, ct);
        if (env is not null) runs = runs.Where(r => r.Environment == env);
        if (!string.IsNullOrWhiteSpace(kind)) runs = runs.Where(r => r.Kind == kind.ToLower());

        var scoped = from item in _db.MigrationRunItems.AsNoTracking()
                     join run in runs on item.MigrationRunId equals run.Id
                     select new { item, run };

        List<(string Name, int Score)> names;
        if (query.Length > 0 && query.All(char.IsDigit) && int.TryParse(query, out var orderNo))
        {
            // The order prefix of the script's file name — what someone reading the repository sees.
            names = (await scoped.Where(x => x.item.Order == orderNo).Select(x => x.item.Name).Distinct().ToListAsync(ct))
                .Select(n => (n, 100)).ToList();
        }
        else
        {
            var candidates = await scoped.Select(x => x.item.Name).Distinct().ToListAsync(ct);
            names = candidates
                .Select(n => (Name: n, Score: MigrationNameMatcher.Score(query, n)))
                .Where(x => x.Score >= MigrationNameMatcher.Threshold)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var truncated = names.Count > maxNames;
        var picked = names.Take(maxNames).ToList();
        var pickedNames = picked.Select(p => p.Name).ToList();

        var rows = await scoped
            .Where(x => pickedNames.Contains(x.item.Name))
            .Select(x => new
            {
                x.item.Name, x.item.Status, x.item.AppliedAt, x.item.Error,
                RunId = x.run.Id, x.run.Kind, x.run.Product, x.run.Service, x.run.Environment, x.run.Version,
                x.run.DeployEventId, x.run.QueuedAt, x.run.CompletedAt,
            })
            .ToListAsync(ct);

        var matches = picked.Select(p =>
        {
            var hits = rows
                .Where(r => r.Name == p.Name)
                .GroupBy(r => (r.Product, r.Service, r.Environment))
                .Select(g =>
                {
                    var latest = g.OrderByDescending(r => r.QueuedAt).First();
                    var applied = g.Where(r => r.Status == MigrationItemStatus.Applied)
                        .OrderBy(r => r.AppliedAt ?? r.CompletedAt ?? r.QueuedAt).FirstOrDefault();
                    var shown = applied ?? latest;
                    return new MigrationSearchHitDto(
                        g.Key.Product, g.Key.Service, g.Key.Environment, shown.Kind,
                        applied is not null ? MigrationItemStatus.Applied : latest.Status,
                        applied is null ? null : applied.AppliedAt ?? applied.CompletedAt,
                        shown.Version, shown.RunId, shown.DeployEventId,
                        latest.QueuedAt, g.Select(r => r.RunId).Distinct().Count(),
                        applied is null ? latest.Error : null);
                })
                .OrderBy(h => h.Product).ThenBy(h => h.Service).ThenBy(h => h.Environment)
                .ToList();
            return new MigrationSearchMatchDto(p.Name, p.Score, hits);
        }).ToList();

        return new MigrationSearchResultDto(query, matches, truncated);
    }

    /// <summary>The migrations a deploy event carries, for its detail page: by link, or by version for a data migration not linked yet.</summary>
    public async Task<List<MigrationRunSummaryDto>> ForDeployEventAsync(DeployEvent ev, CancellationToken ct = default)
    {
        var rows = await _db.MigrationRuns.AsNoTracking()
            .Where(r => r.DeployEventId == ev.Id
                     || (r.DeployEventId == null && r.Product == ev.Product && r.Service == ev.Service
                         && r.Environment == ev.Environment && r.Version == ev.Version))
            .OrderBy(r => r.Kind == MigrationKinds.Schema ? 0 : 1).ThenByDescending(r => r.QueuedAt)
            .ToListAsync(ct);
        return rows.Select(ToSummary).ToList();
    }

    public static MigrationRunSummaryDto ToSummary(MigrationRun r)
    {
        string? runUrl = null;
        if (r.RunJson is not null)
        {
            var run = r.Run;
            runUrl = run?.JobUrl ?? run?.RunUrl;
        }
        return new MigrationRunSummaryDto(
            r.Id, r.Kind, r.Product, r.Service, r.Environment, r.Version, r.Status, r.Source,
            r.DeployEventId, r.QueuedAt, r.StartedAt, r.CompletedAt, r.Attempts,
            r.AppliedCount, r.FailedCount, r.TotalCount, r.FailureReason, r.SkipReason,
            r.SupersededById, runUrl, r.UpdatedAt);
    }
}
