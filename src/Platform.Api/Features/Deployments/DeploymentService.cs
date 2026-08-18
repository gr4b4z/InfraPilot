using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Microsoft.Extensions.Options;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Users;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

public class DeploymentService
{
    private readonly PlatformDbContext _db;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly IPromotionIngestHook _promotionHook;
    private readonly IOptionsMonitor<NormalizationOptions> _normalization;
    private readonly UserPreferencesService _userPrefs;
    private readonly ServiceDeletionService _serviceDeletions;
    private readonly ServiceProductOverrideService _productOverrides;
    private readonly ILogger<DeploymentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Per-block cap on captured pipeline output. Beyond this the <b>tail</b> is kept: a failing
    /// deploy prints its diagnostics last, so the end of the log is the part worth having. 512 KiB
    /// comfortably holds a full Helm release printout plus its pod/event dumps.
    /// </summary>
    public const int LogContentLimitBytes = 512 * 1024;

    /// <summary>Cap on how many blocks one event may carry, so a misbehaving producer can't flood the table.</summary>
    public const int MaxLogsPerEvent = 20;

    public DeploymentService(
        PlatformDbContext db,
        IWebhookDispatcher webhookDispatcher,
        IPromotionIngestHook promotionHook,
        IOptionsMonitor<NormalizationOptions> normalization,
        UserPreferencesService userPrefs,
        ServiceDeletionService serviceDeletions,
        ServiceProductOverrideService productOverrides,
        ILogger<DeploymentService> logger)
    {
        _db = db;
        _webhookDispatcher = webhookDispatcher;
        _promotionHook = promotionHook;
        _normalization = normalization;
        _userPrefs = userPrefs;
        _serviceDeletions = serviceDeletions;
        _productOverrides = productOverrides;
        _logger = logger;
    }

    /// <summary>
    /// Creates a NEW deploy event as a manual, human/agent-authored entry based on the most recent
    /// event for <c>(Product, Service, Environment)</c>. Only <c>Version</c> and <c>Status</c> come
    /// from the request; references/participants are carried over from the latest event. Attribution
    /// is stamped by the server: <c>Source="manual"</c> and a <c>triggered-by</c> participant set to
    /// <paramref name="actor"/> (any inherited <c>triggered-by</c> is dropped first) — the caller can't
    /// pass it off as a CI event. Runs through <see cref="IngestEvent"/> so PreviousVersion derivation,
    /// the deployment webhook, and promotion-candidate generation all behave exactly as for CI.
    /// <para>Throws <see cref="KeyNotFoundException"/> when the target has no prior deployment to base on.</para>
    /// </summary>
    public async Task<DeployEvent> CreateManualEventAsync(
        CreateManualDeployRequest req, ManualDeployActor actor, CancellationToken ct = default)
    {
        var environment = _normalization.CurrentValue.ApplyEnvironment(req.Environment);

        // Same override as CI ingest, applied before the lookup: the event this entry is based on lives
        // under the resolved product, so asking for the requested one would report "no prior deployment"
        // for a service that has plenty — just filed where the admin said it belongs.
        var product = await _productOverrides.ResolveProductAsync(req.Product, req.Service, ct);

        var latest = await _db.DeployEvents
            .Where(e => e.Product == product && e.Service == req.Service && e.Environment == environment)
            .OrderByDescending(e => e.DeployedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException(
                $"No existing deployment for {product}/{req.Service} in {req.Environment} to base a manual entry on.");

        // Carry references/participants from the latest event verbatim (same JSON shape as the DTOs),
        // then force the attribution: drop any inherited triggered-by and set it to the actual caller.
        var references = JsonSerializer.Deserialize<List<ReferenceDto>>(latest.ReferencesJson, JsonOptions) ?? [];
        var participants = (JsonSerializer.Deserialize<List<ParticipantDto>>(latest.ParticipantsJson, JsonOptions) ?? [])
            .Where(p => !string.Equals(p.Role, "triggered-by", StringComparison.OrdinalIgnoreCase))
            .ToList();
        participants.Insert(0, new ParticipantDto("triggered-by", actor.DisplayName, actor.Email));

        var metadata = latest.Metadata ?? new Dictionary<string, object>();
        metadata["manualEntry"] = true;
        metadata["basedOnEventId"] = latest.Id.ToString();
        metadata["note"] = req.Note;

        var dto = new CreateDeployEventDto(
            Product: product,
            Service: req.Service,
            Environment: environment,
            Version: req.Version,
            Source: "manual",
            DeployedAt: DateTimeOffset.UtcNow,
            References: references,
            Participants: participants,
            Metadata: metadata,
            Status: req.Status ?? latest.Status,
            IsRollback: false,
            PreviousVersion: null); // let IngestEvent derive it from the current latest

        return await IngestEvent(dto, ct);
    }

    /// <summary>Outcome of an ingest: the stored event plus whether it was a replay of an existing row.</summary>
    public record IngestResult(DeployEvent Event, bool Replayed);

    public async Task<DeployEvent> IngestEvent(CreateDeployEventDto dto, CancellationToken ct = default)
        => (await IngestEventWithResult(dto, ct)).Event;

    /// <summary>
    /// Ingests a deploy event idempotently. A row whose natural key
    /// <c>(Product, Service, Environment, Version, DeployedAt, Source)</c> — the same key
    /// <see cref="RemoveDuplicates"/> uses — already exists is treated as a replay: the existing
    /// row is returned with <c>Replayed=true</c> and no webhook / promotion hook fires again.
    /// This makes pipeline retries safe as long as the sender keeps <c>deployedAt</c> stable
    /// across attempts. The guard is check-then-insert (no unique index across providers), so a
    /// truly concurrent duplicate can still slip through; <see cref="RemoveDuplicates"/> remains
    /// the backstop for that case.
    /// <para>Either path un-retires the service if an admin had soft-deleted it — see
    /// <see cref="ServiceDeletionService.ReviveOnDeployAsync"/>. A replay counts: the retirement was
    /// wrong either way, and the sender should not have to post a second, distinct deploy to undo it.</para>
    /// </summary>
    public async Task<IngestResult> IngestEventWithResult(CreateDeployEventDto dto, CancellationToken ct = default)
    {
        var norm = _normalization.CurrentValue;

        // Optional canonicalisation — controlled by appsettings `Normalization:*`. Off by
        // default, so senders' original casing is preserved unless an admin opts in.
        var environment = norm.ApplyEnvironment(dto.Environment);

        // Product is the one field on this payload that a pipeline mid-migration reliably gets wrong,
        // so an admin override for the service wins over what was sent (ServiceProductOverride).
        // Resolved once, before anything reads it: the revive probe, the replay key, the
        // PreviousVersion derivation and the stored row all have to agree on which product this
        // deploy belongs to, and resolving per-use is how they would drift apart.
        var product = await _productOverrides.ResolveProductAsync(dto.Product, dto.Service, ct);

        // Staged, not saved: it rides along with whichever SaveChanges below commits the event.
        await _serviceDeletions.ReviveOnDeployAsync(product, dto.Service, dto.DeployedAt, ct);

        var replayed = await _db.DeployEvents
            .Where(e => e.Product == product && e.Service == dto.Service
                     && e.Environment == environment && e.Version == dto.Version
                     && e.Source == dto.Source && e.DeployedAt == dto.DeployedAt)
            .OrderBy(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (replayed is not null)
        {
            // A replay still refreshes run details and logs. The first attempt of a pipeline can
            // post before it has resolved its job URL or finished capturing output; the retry
            // carries the fuller picture, and dropping it would leave the detail page thinner than
            // the sender intended.
            if (dto.Run is not null)
            {
                replayed.Run = dto.Run;
                _db.DeployEvents.Update(replayed);
            }
            await SyncLogsAsync(replayed.Id, dto.Logs, ct);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Replayed deploy event {Id}: {Product}/{Service} → {Environment} v{Version} already ingested; returning existing row",
                replayed.Id, replayed.Product, replayed.Service, replayed.Environment, replayed.Version);

            // A replay still runs the promotion hook. The original POST could only match promotions
            // that existed at the time; one created since — or one stranded by a hook failure the first
            // time round — is closed by the retry rather than left open forever. The hook is idempotent
            // (it skips candidates already Deployed/Superseded), so replaying costs nothing when there
            // is nothing to close.
            await _promotionHook.OnIngestedAsync(replayed, ct);

            return new IngestResult(replayed, true);
        }

        // Use caller-supplied previousVersion when present (lets integrators assert the
        // predecessor they observed and detect drift vs. the server's history). Otherwise
        // derive it from the most recent event for the same product+service+environment.
        string? previousVersion = dto.PreviousVersion;
        if (previousVersion is null)
        {
            var previousEvent = await _db.DeployEvents
                .Where(e => e.Product == product && e.Service == dto.Service && e.Environment == environment)
                .OrderByDescending(e => e.DeployedAt)
                .Select(e => new { e.Version })
                .FirstOrDefaultAsync(ct);
            previousVersion = previousEvent?.Version;
        }

        var status = dto.Status ?? "succeeded";
        var deployEvent = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = dto.Service,
            Environment = environment,
            Version = dto.Version,
            PreviousVersion = previousVersion,
            IsRollback = dto.IsRollback,
            Status = status,
            Source = dto.Source,
            DeployedAt = dto.DeployedAt,
            ReferencesJson = JsonSerializer.Serialize(
                (dto.References ?? []).Select(r => new ReferenceDto(
                    Type: r.Type,
                    Url: r.Url,
                    Provider: r.Provider,
                    Key: r.Key,
                    Revision: r.Revision,
                    Title: r.Title,
                    // Apply the same role canonicalisation to nested participants so
                    // reference-level roles are stored in the same shape as event-level.
                    Participants: r.Participants is null
                        ? null
                        : r.Participants.Select(p => new ParticipantDto(
                            Role: norm.ApplyRole(p.Role),
                            DisplayName: p.DisplayName,
                            Email: p.Email)).ToList(),
                    // Stored verbatim — normalisation applies to roles and environment names,
                    // not to a ticket body.
                    Content: r.Content,
                    // Carried through untouched: Commits links a work item to its commit/PR
                    // references, OccurredAt is the lead-time clock start — both are consumed
                    // by WorkItemCommitTime when the work-item projection is synced.
                    Commits: r.Commits,
                    OccurredAt: r.OccurredAt)).ToList(),
                JsonOptions),
            ParticipantsJson = JsonSerializer.Serialize(
                (dto.Participants ?? []).Select(p => new ParticipantDto(
                    Role: norm.ApplyRole(p.Role),
                    DisplayName: p.DisplayName,
                    Email: p.Email)).ToList(),
                JsonOptions),
            MetadataJson = JsonSerializer.Serialize(dto.Metadata ?? new Dictionary<string, object>(), JsonOptions),
            RunJson = dto.Run is null ? null : JsonSerializer.Serialize(dto.Run, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.DeployEvents.Add(deployEvent);
        await SyncLogsAsync(deployEvent.Id, dto.Logs, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Ingested deploy event {Id}: {Product}/{Service} → {Environment} v{Version} (prev: {PreviousVersion})",
            deployEvent.Id, deployEvent.Product, deployEvent.Service, deployEvent.Environment,
            deployEvent.Version, deployEvent.PreviousVersion ?? "none");

        await _webhookDispatcher.DispatchAsync("deployment.created", new
        {
            deployEvent.Id,
            deployEvent.Product,
            deployEvent.Service,
            deployEvent.Environment,
            deployEvent.Version,
            deployEvent.PreviousVersion,
            deployEvent.IsRollback,
            deployEvent.Status,
            deployEvent.Source,
            deployEvent.DeployedAt,
            references = deployEvent.References,
            participants = deployEvent.Participants,
            // Subscribers alerting on failures need the cause and somewhere to click, not just
            // status="failed".
            runUrl = dto.Run?.JobUrl ?? dto.Run?.RunUrl,
            failureReason = dto.Run?.FailureReason,
        }, new WebhookEventFilters(deployEvent.Product, deployEvent.Environment));

        // Fire-and-observe: generate promotion candidates / close in-flight ones. The hook is
        // feature-flag gated internally and swallows its own failures so ingestion stays
        // resilient even when the promotion machinery misbehaves.
        await _promotionHook.OnIngestedAsync(deployEvent, ct);

        return new IngestResult(deployEvent, false);
    }

    // --- Captured pipeline output ---

    /// <summary>
    /// Stages the event's log blocks to match <paramref name="logs"/>, keyed by name: a block the
    /// sender repeats is updated in place, one it stops sending is left alone. Deliberately not a
    /// full reconcile — a producer may report the Helm output and its diagnostics from two different
    /// calls, and deleting whatever the current call didn't mention would make the second call erase
    /// the first. Blocks past <see cref="MaxLogsPerEvent"/> are dropped with a warning.
    /// <para>Caller owns the surrounding <c>SaveChangesAsync</c>.</para>
    /// </summary>
    private async Task SyncLogsAsync(Guid eventId, List<CreateDeployLogDto>? logs, CancellationToken ct)
    {
        if (logs is null || logs.Count == 0) return;

        // Named blocks only — the name is the identity, so an unnamed block can neither be replaced
        // nor labelled in the UI.
        var incoming = logs
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .GroupBy(l => l.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last()) // last wins: a sender repeating a name within one payload meant the later one
            .ToList();

        if (incoming.Count > MaxLogsPerEvent)
        {
            _logger.LogWarning(
                "Deploy event {EventId} sent {Count} log blocks; keeping the first {Limit}",
                eventId, incoming.Count, MaxLogsPerEvent);
            incoming = incoming.Take(MaxLogsPerEvent).ToList();
        }

        var existing = await _db.DeployEventLogs
            .Where(l => l.DeployEventId == eventId)
            .ToListAsync(ct);
        var byName = existing.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);

        // Continue numbering after what's already stored so blocks arriving in a later call sort
        // after the earlier ones rather than colliding at 0.
        var nextSequence = existing.Count == 0 ? 0 : existing.Max(l => l.Sequence) + 1;

        foreach (var dto in incoming)
        {
            var name = dto.Name.Trim();
            var (content, truncated, originalBytes) = CapLogContent(dto.Content ?? "");
            truncated = truncated || dto.Truncated;

            if (byName.TryGetValue(name, out var row))
            {
                row.Source = dto.Source;
                row.Content = content;
                row.Truncated = truncated;
                row.ByteCount = System.Text.Encoding.UTF8.GetByteCount(content);
                row.LineCount = CountLines(content);
                row.OriginalByteCount = originalBytes;
                _db.DeployEventLogs.Update(row);
                continue;
            }

            _db.DeployEventLogs.Add(new DeployEventLog
            {
                Id = Guid.NewGuid(),
                DeployEventId = eventId,
                Name = name,
                Source = dto.Source,
                Sequence = nextSequence++,
                Content = content,
                Truncated = truncated,
                ByteCount = System.Text.Encoding.UTF8.GetByteCount(content),
                LineCount = CountLines(content),
                OriginalByteCount = originalBytes,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    /// <summary>
    /// Trims content to <see cref="LogContentLimitBytes"/>, keeping the tail and prefixing a marker
    /// so a reader is never left wondering whether the log simply started mid-sentence. Measured in
    /// UTF-8 bytes (what the column costs), cut on a character boundary.
    /// </summary>
    public static (string Content, bool Truncated, int OriginalByteCount) CapLogContent(string content)
    {
        var originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (originalBytes <= LogContentLimitBytes) return (content, false, originalBytes);

        // Character count is an upper bound on byte count for the tail, so slicing by characters
        // then re-checking converges without a per-character scan.
        var kept = content;
        while (System.Text.Encoding.UTF8.GetByteCount(kept) > LogContentLimitBytes)
        {
            var overBy = System.Text.Encoding.UTF8.GetByteCount(kept) - LogContentLimitBytes;
            kept = kept[Math.Min(kept.Length, overBy)..];
        }
        // Drop the partial first line — a log that opens mid-token reads like corruption.
        var firstNewline = kept.IndexOf('\n');
        if (firstNewline >= 0 && firstNewline < kept.Length - 1) kept = kept[(firstNewline + 1)..];

        return ($"[… {originalBytes - System.Text.Encoding.UTF8.GetByteCount(kept)} bytes trimmed from the start of this log …]\n{kept}",
            true, originalBytes);
    }

    /// <summary>
    /// Returns one log block's content, or null when the block doesn't exist or belongs to a
    /// different event (the id pair is checked, so a guessed log id can't leak another deployment's
    /// output).
    /// </summary>
    public async Task<DeployLogContentDto?> GetLogContent(Guid eventId, Guid logId, CancellationToken ct = default)
    {
        var log = await _db.DeployEventLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == logId && l.DeployEventId == eventId, ct);
        return log is null
            ? null
            : new DeployLogContentDto(log.Id, log.Name, log.Source, log.Content, log.Truncated, log.OriginalByteCount);
    }

    // --- Detail view ---

    /// <summary>
    /// Assembles the deployment detail page's payload for one event: the event, its captured output
    /// (summaries only — content is a separate call), the same service's neighbouring deployments,
    /// and the promotions and work items that tie this deployment into the release process.
    /// Returns null when no such event exists.
    /// </summary>
    public async Task<DeployEventDetailDto?> GetEventDetail(
        Guid id, int historyLimit = 10, CancellationToken ct = default)
    {
        var ev = await _db.DeployEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null) return null;

        var overrides = await LoadOverridesByEventAsync([ev.Id], ct);
        var eventDto = MapToResponseDto(ev, overrides.GetValueOrDefault(ev.Id));

        // Projected without Content: the summary list exists precisely so opening the page doesn't
        // drag every log block along with it. Sizes were materialised at ingest for this reason.
        var logSummaries = await _db.DeployEventLogs.AsNoTracking()
            .Where(l => l.DeployEventId == id)
            .OrderBy(l => l.Sequence)
            .Select(l => new DeployLogSummaryDto(
                l.Id, l.Name, l.Source, l.Sequence, l.ByteCount, l.LineCount, l.Truncated, l.CreatedAt))
            .ToListAsync(ct);

        // Same service in the same environment: the question a reader has on this page is "what did
        // this service do before/after here", not "what happened elsewhere".
        var history = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == ev.Product && e.Service == ev.Service && e.Environment == ev.Environment)
            .OrderByDescending(e => e.DeployedAt)
            .Take(Math.Max(1, historyLimit))
            .Select(e => new
            {
                e.Id, e.Environment, e.Version, e.PreviousVersion, e.IsRollback,
                e.Status, e.Source, e.DeployedAt, e.RunJson,
            })
            .ToListAsync(ct);

        var historyDtos = history
            .Select(e => new DeployEventHistoryEntryDto(
                e.Id, e.Environment, e.Version, e.PreviousVersion, e.IsRollback,
                e.Status, e.Source, e.DeployedAt,
                FailureReason: Deserialize<DeployRun>(e.RunJson)?.FailureReason))
            .ToList();

        // Promotions on the same (product, service, version). Outbound: this environment is the
        // promotion's source, so this deploy is what may move forward. Inbound: it is the target, so
        // this deploy is what the promotion delivered.
        var promotions = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Product == ev.Product && c.Service == ev.Service && c.Version == ev.Version
                     && (c.SourceEnv == ev.Environment || c.TargetEnv == ev.Environment))
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id, c.SourceEnv, c.TargetEnv, c.Version, c.Status, c.CreatedAt, c.ApprovedAt, c.DeployedAt,
            })
            .ToListAsync(ct);

        var promotionDtos = promotions
            .Select(c => new RelatedPromotionDto(
                c.Id, c.SourceEnv, c.TargetEnv, c.Version, c.Status.ToString(),
                Direction: c.SourceEnv == ev.Environment ? "outbound" : "inbound",
                c.CreatedAt, c.ApprovedAt, c.DeployedAt))
            .ToList();

        // Work items come from the relational projection rather than the event's JSON so the titles
        // reflect any later Jira enrichment.
        var workItemRows = await _db.DeployEventWorkItems.AsNoTracking()
            .Where(w => w.DeployEventId == id)
            .OrderBy(w => w.WorkItemKey)
            .Select(w => new { w.WorkItemKey, w.Provider, w.Url, w.Title })
            .ToListAsync(ct);

        // Sign-off is keyed on (key, product, targetEnv), so a link needs a target env. Take them
        // from the promotions carrying this version — those are the gates the ticket is actually in.
        var signOffEnvs = promotionDtos
            .Select(p => p.TargetEnv)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workItemDtos = workItemRows
            .Select(w => new RelatedWorkItemDto(w.WorkItemKey, w.Provider, w.Url, w.Title, signOffEnvs))
            .ToList();

        return new DeployEventDetailDto(eventDto, logSummaries, historyDtos, promotionDtos, workItemDtos);
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0) return 0;
        var lines = 1;
        foreach (var c in content) if (c == '\n') lines++;
        return lines;
    }

    /// <summary>
    /// Returns the distinct versions that have been deployed to the given (product, service, environment),
    /// most-recent-first. Intended as the backing data source for a rollback picker in the UI:
    /// each item carries the deploy id, version, deployer, and timestamp so the UI can show a
    /// meaningful label ("v1.2.3 — deployed 2 days ago by alice").
    ///
    /// <para><c>product</c> and <c>environment</c> are required; <c>service</c> is optional and
    /// when omitted returns versions across all services for the product/environment. Results
    /// are capped by <paramref name="limit"/> (default 50).</para>
    /// </summary>
    public async Task<List<DeploymentVersionDto>> GetVersions(
        string product, string environment, string? serviceName,
        int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(environment))
            return new List<DeploymentVersionDto>();

        var query = _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Environment == environment)
            // A retired service is not a rollback target — offering its versions in the picker would
            // invite somebody to redeploy the thing that was just migrated away.
            .ExcludingDeletedServices(_db);
        if (!string.IsNullOrWhiteSpace(serviceName))
            query = query.Where(e => e.Service == serviceName);

        // Only successful deploys are rollback candidates; failed events don't represent a
        // real deployed version to go back to.
        query = query.Where(e => e.Status == "succeeded");

        // DeployedAt-desc with a DeployEventId tiebreak (LINQ `.First()` inside GroupBy would
        // be the natural shape but the in-memory provider doesn't translate it cleanly, so we
        // project, order, and then distinct-by version client-side.)
        var raw = await query
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => new
            {
                e.Id,
                e.Service,
                e.Version,
                e.DeployedAt,
                e.IsRollback,
                e.ParticipantsJson,
            })
            .Take(Math.Max(1, limit) * 4) // oversample so distinct-by-version still hits the limit
            .ToListAsync(ct);

        var versions = new List<DeploymentVersionDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in raw)
        {
            // Unique key includes service — same version number across two services is not a duplicate.
            var key = $"{e.Service}\0{e.Version}";
            if (!seen.Add(key)) continue;

            string? deployer = null;
            if (!string.IsNullOrWhiteSpace(e.ParticipantsJson))
            {
                try
                {
                    var parts = JsonSerializer.Deserialize<List<ParticipantDto>>(e.ParticipantsJson, JsonOptions);
                    // Match after normalization so this works whether or not ingest-time
                    // canonicalisation is enabled.
                    deployer = parts?.FirstOrDefault(p =>
                        RoleNormalizer.Normalize(p.Role) == "triggered-by")?.Email;
                }
                catch { /* best-effort */ }
            }

            versions.Add(new DeploymentVersionDto(
                Id: e.Id,
                Service: e.Service,
                Version: e.Version,
                DeployedAt: e.DeployedAt,
                DeployerEmail: deployer,
                IsRollback: e.IsRollback));

            if (versions.Count >= limit) break;
        }

        return versions;
    }

    /// <summary>
    /// The service × environment matrix. Services an admin has retired are dropped here — this is the
    /// query the product page derives its service list from, so filtering it is what actually takes
    /// an obsolete service off the page.
    /// </summary>
    public async Task<List<DeploymentStateDto>> GetState(string? product, string? environment, string? serviceName, CancellationToken ct = default)
    {
        var query = _db.DeployEvents.ExcludingDeletedServices(_db);
        if (!string.IsNullOrEmpty(product)) query = query.Where(e => e.Product == product);
        if (!string.IsNullOrEmpty(environment)) query = query.Where(e => e.Environment == environment);
        if (!string.IsNullOrEmpty(serviceName)) query = query.Where(e => e.Service == serviceName);

        // Latest event per (product, service, environment) using a window-function approach
        var latest = await query
            .GroupBy(e => new { e.Product, e.Service, e.Environment })
            .Select(g => g.OrderByDescending(e => e.DeployedAt).First())
            .ToListAsync(ct);

        var overrides = await LoadOverridesByEventAsync(latest.Select(e => e.Id), ct);
        return latest.Select(e => MapToStateDto(e, overrides.GetValueOrDefault(e.Id))).ToList();
    }

    /// <summary>
    /// The product × environment matrix. Products the caller has hidden are dropped here rather
    /// than in the UI — this is the canonical product list, so filtering it also empties the
    /// release-notes index and every product dropdown built from it.
    /// </summary>
    public async Task<List<ProductSummaryDto>> GetProductSummaries(CancellationToken ct = default)
    {
        var hidden = await _userPrefs.GetHiddenProductsAsync(ct);

        var latest = await _db.DeployEvents
            .Where(e => !hidden.Contains(e.Product))
            // Retired services stop counting towards the per-environment service totals too, or the
            // product card would keep advertising components the product page no longer lists.
            .ExcludingDeletedServices(_db)
            .GroupBy(e => new { e.Product, e.Service, e.Environment })
            .Select(g => g.OrderByDescending(e => e.DeployedAt).First())
            .ToListAsync(ct);

        var grouped = latest
            .GroupBy(e => e.Product)
            .Select(pg =>
            {
                var environments = pg
                    .GroupBy(e => e.Environment)
                    .ToDictionary(
                        eg => eg.Key,
                        eg => new EnvironmentSummaryDto(
                            TotalServices: eg.Count(),
                            DeployedServices: eg.Count(),
                            LastDeployedAt: eg.Max(e => e.DeployedAt)));

                return new ProductSummaryDto(pg.Key, environments);
            })
            .ToList();

        return grouped;
    }

    /// <summary>
    /// Cross-product service search: case-insensitive substring match on the service name. This is
    /// the query behind "find a service without knowing its product", so unlike the other read
    /// paths it takes no product argument at all. Hidden products are dropped for the same reason
    /// they are in <see cref="GetProductSummaries"/> — hiding a product hides it everywhere — and
    /// retired services stay out like on every other read path. Results are most-recently-deployed
    /// first, capped by <paramref name="limit"/>.
    /// </summary>
    public async Task<List<ServiceSearchResultDto>> SearchServices(
        string query, int limit = 20, CancellationToken ct = default)
    {
        var needle = query.Trim().ToLower();
        if (needle.Length == 0) return new List<ServiceSearchResultDto>();

        var hidden = await _userPrefs.GetHiddenProductsAsync(ct);

        // One row per (product, service, environment) — grouped into per-service hits in memory,
        // which keeps the query translatable (a nested Distinct inside a GroupBy projection isn't).
        var rows = await _db.DeployEvents.AsNoTracking()
            .ExcludingDeletedServices(_db)
            .Where(e => !hidden.Contains(e.Product))
            .Where(e => e.Service.ToLower().Contains(needle))
            .GroupBy(e => new { e.Product, e.Service, e.Environment })
            .Select(g => new
            {
                g.Key.Product,
                g.Key.Service,
                g.Key.Environment,
                LastDeployedAt = g.Max(e => e.DeployedAt),
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => new { r.Product, r.Service })
            .Select(g => new ServiceSearchResultDto(
                g.Key.Product,
                g.Key.Service,
                g.OrderByDescending(r => r.LastDeployedAt)
                    .Select(r => new ServiceSearchEnvironmentDto(r.Environment, r.LastDeployedAt))
                    .ToList(),
                g.Max(r => r.LastDeployedAt)))
            .OrderByDescending(s => s.LastDeployedAt)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    /// <summary>
    /// Assembles the service detail page's payload for one (product, service): the latest event per
    /// environment, the most recent distinct versions with the environments each reached, and the
    /// service's promotions. Returns null when the pair has no visible deployments — unknown and
    /// retired look the same here, exactly as they do on every other read path.
    /// </summary>
    public async Task<ServiceDetailDto?> GetServiceDetail(
        string product, string service, int versionsLimit = 10, CancellationToken ct = default)
    {
        var environments = await GetState(product, null, service, ct);
        if (environments.Count == 0) return null;

        // Recent deploys, newest first, folded into distinct versions in memory (same reasoning as
        // GetVersions: GroupBy + First doesn't translate cleanly). Oversampled so a version that was
        // redeployed many times doesn't starve the list of older versions.
        var recent = await _db.DeployEvents.AsNoTracking()
            .ExcludingDeletedServices(_db)
            .Where(e => e.Product == product && e.Service == service)
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => new { e.Id, e.Environment, e.Version, e.Status, e.IsRollback, e.DeployedAt })
            .Take(Math.Max(1, versionsLimit) * 20)
            .ToListAsync(ct);

        var versions = new List<ServiceVersionDto>();
        var byVersion = new Dictionary<string, List<ServiceVersionEnvironmentDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in recent)
        {
            if (!byVersion.TryGetValue(e.Version, out var envs))
            {
                if (versions.Count >= versionsLimit) continue;
                envs = new List<ServiceVersionEnvironmentDto>();
                byVersion[e.Version] = envs;
                // Rows arrive newest-first, so first sight of a version fixes its LastDeployedAt.
                versions.Add(new ServiceVersionDto(e.Version, e.DeployedAt, envs));
            }
            // Latest event per (version, environment): rows are newest-first, so keep the first one.
            if (envs.Any(x => string.Equals(x.Environment, e.Environment, StringComparison.OrdinalIgnoreCase)))
                continue;
            envs.Add(new ServiceVersionEnvironmentDto(e.Id, e.Environment, e.Status, e.IsRollback, e.DeployedAt));
        }

        var promotions = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Product == product && c.Service == service)
            .OrderByDescending(c => c.CreatedAt)
            .Take(25)
            .Select(c => new
            {
                c.Id, c.SourceEnv, c.TargetEnv, c.Version, c.Status, c.CreatedAt, c.ApprovedAt, c.DeployedAt,
            })
            .ToListAsync(ct);

        var promotionDtos = promotions
            .Select(c => new ServicePromotionDto(
                c.Id, c.SourceEnv, c.TargetEnv, c.Version, c.Status.ToString(),
                c.CreatedAt, c.ApprovedAt, c.DeployedAt))
            .ToList();

        return new ServiceDetailDto(product, service, environments, versions, promotionDtos);
    }

    public async Task<List<DeployEventResponseDto>> GetHistory(
        string product, string service, string? environment, int limit = 50, CancellationToken ct = default)
    {
        var query = _db.DeployEvents
            .ExcludingDeletedServices(_db)
            .Where(e => e.Product == product && e.Service == service);

        if (!string.IsNullOrEmpty(environment))
            query = query.Where(e => e.Environment == environment);

        var events = await query
            .OrderByDescending(e => e.DeployedAt)
            .Take(limit)
            .ToListAsync(ct);

        var overrides = await LoadOverridesByEventAsync(events.Select(e => e.Id), ct);
        return events.Select(e => MapToResponseDto(e, overrides.GetValueOrDefault(e.Id))).ToList();
    }

    public async Task<List<DeployEventResponseDto>> GetRecentByEnvironment(
        string product, string environment, DateTimeOffset since, CancellationToken ct = default)
    {
        var events = await _db.DeployEvents
            .ExcludingDeletedServices(_db)
            .Where(e => e.Product == product && e.Environment == environment && e.DeployedAt >= since)
            .OrderByDescending(e => e.DeployedAt)
            .ToListAsync(ct);

        var overrides = await LoadOverridesByEventAsync(events.Select(e => e.Id), ct);
        return events.Select(e => MapToResponseDto(e, overrides.GetValueOrDefault(e.Id))).ToList();
    }

    public async Task<List<DeployEventResponseDto>> GetRecentByProduct(
        string product, DateTimeOffset since, int limit = 200, CancellationToken ct = default)
    {
        var events = await _db.DeployEvents
            .ExcludingDeletedServices(_db)
            .Where(e => e.Product == product && e.DeployedAt >= since)
            .OrderByDescending(e => e.DeployedAt)
            .Take(limit)
            .ToListAsync(ct);

        var overrides = await LoadOverridesByEventAsync(events.Select(e => e.Id), ct);
        return events.Select(e => MapToResponseDto(e, overrides.GetValueOrDefault(e.Id))).ToList();
    }

    /// <summary>
    /// Batch-load override rows keyed by deploy event id. Returns an empty dictionary when
    /// the input is empty so callers can still call <c>GetValueOrDefault</c> without nulls.
    /// </summary>
    private async Task<Dictionary<Guid, List<ReferenceParticipantOverride>>> LoadOverridesByEventAsync(
        IEnumerable<Guid> eventIds, CancellationToken ct)
    {
        var ids = eventIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        var rows = await _db.ReferenceParticipantOverrides.AsNoTracking()
            .Where(o => ids.Contains(o.DeployEventId))
            .ToListAsync(ct);
        return rows.GroupBy(o => o.DeployEventId).ToDictionary(g => g.Key, g => g.ToList());
    }

    // --- Admin: duplicate cleanup ---

    /// <summary>
    /// Natural key used to detect a DeployEvent that was ingested twice.
    /// Rows matching on every field here are duplicates; the earliest-created one is kept.
    /// </summary>
    private readonly record struct DuplicateKey(
        string Product, string Service, string Environment, string Version, DateTimeOffset DeployedAt, string Source);

    /// <summary>Count of duplicate groups and total rows that would be removed by <see cref="RemoveDuplicates"/>.</summary>
    public async Task<(int Groups, int Rows)> CountDuplicates(CancellationToken ct = default)
    {
        // Pull only the natural-key fields to keep the query light.
        var keys = await _db.DeployEvents
            .Select(e => new { e.Product, e.Service, e.Environment, e.Version, e.DeployedAt, e.Source })
            .ToListAsync(ct);

        var grouped = keys
            .GroupBy(k => new DuplicateKey(k.Product, k.Service, k.Environment, k.Version, k.DeployedAt, k.Source))
            .Where(g => g.Count() > 1)
            .ToList();

        var groups = grouped.Count;
        var rows = grouped.Sum(g => g.Count() - 1);
        return (groups, rows);
    }

    /// <summary>
    /// Deletes duplicate DeployEvent rows, keeping the one with the earliest <c>CreatedAt</c> per natural-key group.
    /// Returns the number of distinct groups touched and total rows removed.
    /// </summary>
    public async Task<(int Groups, int Rows)> RemoveDuplicates(CancellationToken ct = default)
    {
        // Fetch just what we need to partition client-side. We can't delete directly in SQL
        // because the "keep earliest" rule is easier to express in memory and keeps this
        // provider-agnostic across Postgres + SqlServer.
        var rows = await _db.DeployEvents
            .Select(e => new { e.Id, e.Product, e.Service, e.Environment, e.Version, e.DeployedAt, e.Source, e.CreatedAt })
            .ToListAsync(ct);

        var toDelete = rows
            .GroupBy(r => new DuplicateKey(r.Product, r.Service, r.Environment, r.Version, r.DeployedAt, r.Source))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.OrderBy(r => r.CreatedAt).Skip(1)) // keep earliest, drop the rest
            .Select(r => r.Id)
            .ToList();

        if (toDelete.Count == 0) return (0, 0);

        var groupCount = rows
            .GroupBy(r => new DuplicateKey(r.Product, r.Service, r.Environment, r.Version, r.DeployedAt, r.Source))
            .Count(g => g.Count() > 1);

        // Single SaveChanges is atomic at the EF level (one DB transaction under the hood).
        var idSet = toDelete.ToHashSet();
        var stale = await _db.DeployEvents.Where(e => idSet.Contains(e.Id)).ToListAsync(ct);
        _db.DeployEvents.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deploy-event dedup removed {Rows} rows across {Groups} groups",
            stale.Count, groupCount);

        return (groupCount, stale.Count);
    }

    // --- Log retention ---

    /*
     * Captured pipeline output is by far the largest thing stored per deploy — a Helm printout plus
     * failure diagnostics per event — and it ages fast: the log matters while somebody is debugging
     * that deploy, not months later. This pair purges log rows for deploy events older than a cutoff;
     * the events themselves, and everything else on them, stay. Age is the EVENT's DeployedAt, not the
     * log row's CreatedAt, so all of one deploy's blocks age together.
     */

    /// <summary>Logs (row count and stored bytes) that <see cref="RemoveOldLogs"/> would delete.</summary>
    public async Task<(int Logs, long Bytes)> CountOldLogs(int olderThanDays, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var rows = await OldLogsQuery(cutoff).Select(l => (long)l.ByteCount).ToListAsync(ct);
        return (rows.Count, rows.Sum());
    }

    /// <summary>Deletes log rows for deploy events older than the cutoff. Returns what was removed.</summary>
    public async Task<(int Logs, long Bytes)> RemoveOldLogs(int olderThanDays, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var stale = await OldLogsQuery(cutoff).ToListAsync(ct);
        if (stale.Count == 0) return (0, 0);

        var bytes = stale.Sum(l => (long)l.ByteCount);
        _db.DeployEventLogs.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Log retention removed {Logs} log block(s), {Bytes} bytes, for deploys older than {Days} days",
            stale.Count, bytes, olderThanDays);

        return (stale.Count, bytes);
    }

    private IQueryable<DeployEventLog> OldLogsQuery(DateTimeOffset cutoff) =>
        _db.DeployEventLogs
            .Where(l => _db.DeployEvents.Any(e => e.Id == l.DeployEventId && e.DeployedAt < cutoff));

    // --- Mapping helpers ---

    private static DeploymentStateDto MapToStateDto(DeployEvent e, IReadOnlyList<ReferenceParticipantOverride>? overrides)
    {
        var refs = ApplyOverridesToReferences(e.ReferencesJson, overrides);
        var parts = Deserialize<List<ParticipantDto>>(e.ParticipantsJson) ?? [];
        var enrichment = string.IsNullOrEmpty(e.EnrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(e.EnrichmentJson);

        return new DeploymentStateDto(
            e.Id, e.Product, e.Service, e.Environment, e.Version, e.PreviousVersion,
            e.IsRollback, e.Status, e.Source, e.DeployedAt, refs, parts, enrichment,
            Deserialize<DeployRun>(e.RunJson));
    }

    private static DeployEventResponseDto MapToResponseDto(DeployEvent e, IReadOnlyList<ReferenceParticipantOverride>? overrides)
    {
        var refs = ApplyOverridesToReferences(e.ReferencesJson, overrides);
        var parts = Deserialize<List<ParticipantDto>>(e.ParticipantsJson) ?? [];
        var enrichment = string.IsNullOrEmpty(e.EnrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(e.EnrichmentJson);
        var metadata = Deserialize<Dictionary<string, object>>(e.MetadataJson);

        return new DeployEventResponseDto(
            e.Id, e.Product, e.Service, e.Environment, e.Version, e.PreviousVersion,
            e.IsRollback, e.Status, e.Source, e.DeployedAt, refs, parts, enrichment, metadata,
            Deserialize<DeployRun>(e.RunJson));
    }

    /// <summary>
    /// Reads ReferencesJson and merges override rows into each reference's participants[]
    /// using <see cref="ReferenceParticipantOverrideService.MergeForReference"/>. References
    /// without overrides pass through untouched. Tombstones are filtered out so the UI sees
    /// an empty slot rather than a stale Jira person.
    /// </summary>
    private static List<ReferenceDto> ApplyOverridesToReferences(
        string? referencesJson, IReadOnlyList<ReferenceParticipantOverride>? overrides)
    {
        var refs = Deserialize<List<ReferenceDto>>(referencesJson) ?? new();
        if (overrides is null || overrides.Count == 0) return refs;

        var byKey = overrides
            .Where(o => !string.IsNullOrEmpty(o.ReferenceKey))
            .GroupBy(o => o.ReferenceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceParticipantOverride>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<ReferenceDto>(refs.Count);
        foreach (var r in refs)
        {
            if (string.IsNullOrEmpty(r.Key) || !byKey.TryGetValue(r.Key, out var matches))
            {
                result.Add(r);
                continue;
            }
            var merged = ReferenceParticipantOverrideService.MergeForReference(r, matches);
            result.Add(r with { Participants = merged });
        }
        return result;
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
