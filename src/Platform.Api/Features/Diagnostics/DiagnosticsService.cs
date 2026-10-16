using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Diagnostics;

/// <summary>
/// Gathers the live evidence behind a stalled promotion or a failed deployment, reduces it to
/// observation flags, and pairs those with the authored causes that fit.
/// </summary>
/// <remarks>
/// The division of labour matters. This class only reports what is true — it never names a cause.
/// The causes live in the playbook YAML, and are selected by whether their declared flags hold. So
/// a wrong diagnosis is either a wrong observation (a bug here, testable) or a wrong playbook entry
/// (an authoring fix), never the model free-associating over a log.
/// </remarks>
public class DiagnosticsService
{
    private readonly PlatformDbContext _db;
    private readonly PlaybookRegistry _playbooks;

    /// <summary>
    /// How long an approved promotion may sit before it is worth explaining. Approval fires a
    /// webhook that has to reach GitHub or ADO, start a workflow, commit a file, and trigger a
    /// reconcile that takes the deployment lock — minutes is normal, so a shorter window would
    /// diagnose healthy promotions.
    /// </summary>
    public static readonly TimeSpan ApprovedGracePeriod = TimeSpan.FromMinutes(20);

    private const string ApprovedEventType = "promotion.approved";

    public DiagnosticsService(PlatformDbContext db, PlaybookRegistry playbooks)
    {
        _db = db;
        _playbooks = playbooks;
    }

    /// <summary>
    /// Diagnoses one promotion candidate. Returns the state, the evidence, and the causes that fit.
    /// </summary>
    public async Task<PromotionDiagnosis?> DiagnosePromotionAsync(Guid candidateId, CancellationToken ct = default)
    {
        var candidate = await _db.PromotionCandidates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        if (candidate is null) return null;

        var now = DateTimeOffset.UtcNow;
        var observations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<string>();

        var age = now - (candidate.ApprovedAt ?? candidate.CreatedAt);
        var symptom = candidate.Status switch
        {
            PromotionStatus.Approved => Symptoms.PromotionStuckApproved,
            PromotionStatus.Pending => Symptoms.PromotionStuckPending,
            _ => null,
        };

        // ── Terminal and in-flight states explain themselves; say so and stop. ──
        if (symptom is null)
        {
            return new PromotionDiagnosis(
                candidate.Id, candidate.Product, candidate.Service, candidate.SourceEnv,
                candidate.TargetEnv, candidate.Version, candidate.Status.ToString(),
                candidate.ApprovedAt, age, null,
                [$"Status is {candidate.Status} — this promotion is not stalled."],
                [], []);
        }

        if (age < ApprovedGracePeriod)
            observations.Add(Observations.WithinGracePeriod);

        evidence.Add($"Status {candidate.Status} for {Describe(age)}.");

        // ── Has something newer overtaken it? ──
        var newer = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Product == candidate.Product
                     && c.Service == candidate.Service
                     && c.TargetEnv == candidate.TargetEnv
                     && c.Id != candidate.Id
                     && c.CreatedAt > candidate.CreatedAt)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (newer is not null)
        {
            observations.Add(Observations.NewerCandidateExists);
            evidence.Add($"A newer candidate exists for the same service and target: {newer.Version} ({newer.Status}).");
        }

        if (symptom == Symptoms.PromotionStuckPending)
        {
            await AddPendingObservations(candidate, observations, evidence, ct);
            return Build(candidate, age, symptom, evidence, observations);
        }

        // ── Approved: follow the announcement out of the building. ──
        var cancelKey = $"promotion-approved:{candidate.Id}";
        var deliveries = await _db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.EventType == ApprovedEventType && d.CancelKey == cancelKey)
            .OrderByDescending(d => d.CreatedAt)
            .Take(5)
            .ToListAsync(ct);

        var subscriptions = await _db.WebhookSubscriptions.AsNoTracking().ToListAsync(ct);
        var listening = subscriptions.Where(s => s.Active && Subscribes(s, ApprovedEventType, candidate)).ToList();

        if (listening.Count == 0)
        {
            observations.Add(Observations.NoActiveSubscription);
            evidence.Add($"No active webhook subscription matches {ApprovedEventType} for product '{candidate.Product}' / environment '{candidate.TargetEnv}'.");
        }
        else
        {
            evidence.Add($"{listening.Count} active subscription(s) listen for {ApprovedEventType}: {string.Join(", ", listening.Select(s => s.Name))}.");
        }

        if (deliveries.Count == 0)
        {
            observations.Add(Observations.NoDeliveryRecorded);
            evidence.Add("No promotion.approved delivery is recorded for this candidate.");
        }
        else
        {
            var latest = deliveries[0];
            evidence.Add($"Latest promotion.approved delivery: status '{latest.Status}', {latest.Attempts} attempt(s)"
                + (latest.HttpStatus is not null ? $", HTTP {latest.HttpStatus}" : "")
                + (latest.DeliveredAt is not null ? $", delivered {Describe(now - latest.DeliveredAt.Value)} ago" : "")
                + ".");

            switch (latest.Status)
            {
                case "pending":
                    observations.Add(Observations.DeliveryPending);
                    evidence.Add($"It is still queued; next retry at {latest.NextRetryAt:u}.");
                    break;
                case "failed":
                    observations.Add(Observations.DeliveryFailed);
                    if (!string.IsNullOrWhiteSpace(latest.ErrorMessage))
                        evidence.Add($"Delivery error: {Truncate(latest.ErrorMessage, 300)}");
                    if (!string.IsNullOrWhiteSpace(latest.ResponseBody))
                        evidence.Add($"Response body: {Truncate(latest.ResponseBody, 300)}");
                    break;
                default:
                    observations.Add(Observations.DeliverySucceeded);
                    break;
            }
        }

        // ── Did anything land on the cluster afterwards? ──
        var since = candidate.ApprovedAt ?? candidate.CreatedAt;
        var deployEvents = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == candidate.Product
                     && e.Service == candidate.Service
                     && e.Environment == candidate.TargetEnv
                     && e.DeployedAt >= since)
            .OrderByDescending(e => e.DeployedAt)
            .Take(5)
            .ToListAsync(ct);

        if (deployEvents.Count == 0)
        {
            observations.Add(Observations.NoDeployEventSinceApproval);
            evidence.Add($"No deploy event for {candidate.Service} in {candidate.TargetEnv} since approval.");
        }
        else
        {
            var matching = deployEvents.FirstOrDefault(e => e.Version == candidate.Version);
            if (matching is null)
            {
                observations.Add(Observations.DeployEventOtherVersion);
                evidence.Add($"Deploy events exist since approval, but for other versions ({string.Join(", ", deployEvents.Select(e => e.Version).Distinct())}), not {candidate.Version}.");
            }
            else if (!string.Equals(matching.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                observations.Add(Observations.DeployEventFailed);
                evidence.Add($"A deploy event for {candidate.Version} exists but its status is '{matching.Status}'.");
            }
            else
            {
                observations.Add(Observations.DeployEventSucceeded);
                evidence.Add($"A successful deploy event for {candidate.Version} exists at {matching.DeployedAt:u} — the candidate should have been closed; this looks like a completion-reconcile gap.");
            }
        }

        return Build(candidate, age, symptom, evidence, observations);
    }

    /// <summary>
    /// Diagnoses a failed deployment. Log excerpts are returned verbatim but capped — the model is
    /// told to quote them rather than paraphrase, because an operator needs the actual Helm or
    /// kubectl error, not a summary of it.
    /// </summary>
    public async Task<DeploymentDiagnosis?> DiagnoseDeploymentAsync(Guid eventId, CancellationToken ct = default)
    {
        var ev = await _db.DeployEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return null;

        var observations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<string>();
        var excerpts = new List<string>();

        var succeeded = string.Equals(ev.Status, "succeeded", StringComparison.OrdinalIgnoreCase);
        evidence.Add($"Deploy event status '{ev.Status}', source '{ev.Source}', at {ev.DeployedAt:u}.");

        if (succeeded)
        {
            observations.Add(Observations.DeploySucceeded);
            evidence.Add("This deployment did not fail — nothing to diagnose.");
            return new DeploymentDiagnosis(
                ev.Id, ev.Product, ev.Service, ev.Environment, ev.Version, ev.Status, ev.Source,
                ev.DeployedAt, null, evidence, [.. observations], [], []);
        }

        observations.Add(Observations.DeployFailed);

        if (ev.IsRollback) observations.Add(Observations.WasRollback);
        if (!string.IsNullOrWhiteSpace(ev.PreviousVersion))
            evidence.Add($"Previous version in {ev.Environment} was {ev.PreviousVersion}.");

        // A synthetic source cannot have failed on a cluster — it never touched one.
        if (ev.Source is "release-track" or "mpt-manifest" or "manual")
        {
            observations.Add(Observations.SyntheticSource);
            evidence.Add($"Source '{ev.Source}' is a ledger marker, not a real deployment.");
        }

        var logs = await _db.DeployEventLogs.AsNoTracking()
            .Where(l => l.DeployEventId == ev.Id)
            .OrderBy(l => l.Sequence)
            .ToListAsync(ct);

        if (logs.Count == 0)
        {
            observations.Add(Observations.NoLogsCaptured);
            evidence.Add("No logs were captured with this event.");
        }
        else
        {
            evidence.Add($"{logs.Count} log section(s) captured: {string.Join(", ", logs.Select(l => l.Name))}.");
            if (logs.Any(l => l.Truncated)) observations.Add(Observations.LogsTruncated);

            foreach (var log in logs.Take(4))
            {
                // The tail is where the failure is — a Helm or kubectl error surfaces at the end of
                // the section, and leading output is setup noise.
                excerpts.Add($"── {log.Name} ──\n{Tail(log.Content, 1500)}");
            }

            var haystack = string.Join('\n', logs.Select(l => l.Content));
            foreach (var (needle, flag) in LogSignals)
                if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    observations.Add(flag);
        }

        var playbook = _playbooks.ForSymptom(Symptoms.DeploymentFailed);
        var matched = _playbooks.Match(Symptoms.DeploymentFailed, observations);

        return new DeploymentDiagnosis(
            ev.Id, ev.Product, ev.Service, ev.Environment, ev.Version, ev.Status, ev.Source,
            ev.DeployedAt, playbook?.Summary, evidence, [.. observations], excerpts, matched);
    }

    /// <summary>
    /// Substrings that identify a well-understood failure shape in deployment logs, mapped to the
    /// observation flag a playbook cause can key on. Kept deliberately small and literal: each entry
    /// must correspond to a cause somebody actually wrote up.
    /// </summary>
    private static readonly (string Needle, string Flag)[] LogSignals =
    [
        ("ImagePullBackOff", "log_image_pull_failure"),
        ("ErrImagePull", "log_image_pull_failure"),
        ("manifest unknown", "log_image_pull_failure"),
        ("CrashLoopBackOff", "log_crash_loop"),
        ("readiness probe failed", "log_probe_failure"),
        ("liveness probe failed", "log_probe_failure"),
        ("timed out waiting for the condition", "log_timeout"),
        ("context deadline exceeded", "log_timeout"),
        ("UPGRADE FAILED", "log_helm_upgrade_failed"),
        ("another operation (install/upgrade/rollback) is in progress", "log_helm_lock"),
        ("has no deployed releases", "log_helm_no_deployed_release"),
        ("Insufficient cpu", "log_insufficient_resources"),
        ("Insufficient memory", "log_insufficient_resources"),
        ("forbidden", "log_permission_denied"),
        ("unauthorized", "log_permission_denied"),
        ("connection refused", "log_connection_refused"),
    ];

    /// <summary>Last <paramref name="max"/> characters, cut at a line boundary where possible.</summary>
    private static string Tail(string value, int max)
    {
        if (value.Length <= max) return value;
        var tail = value[^max..];
        var newline = tail.IndexOf('\n');
        return "…\n" + (newline >= 0 && newline < 200 ? tail[(newline + 1)..] : tail);
    }

    /// <summary>Why a Pending candidate has not been approved — gates, not plumbing.</summary>
    private async Task AddPendingObservations(
        PromotionCandidate candidate, HashSet<string> observations, List<string> evidence, CancellationToken ct)
    {
        var approvals = await _db.PromotionApprovals.AsNoTracking()
            .Where(a => a.CandidateId == candidate.Id)
            .ToListAsync(ct);

        if (approvals.Count == 0)
        {
            observations.Add(Observations.NoApprovalsRecorded);
            evidence.Add("No approval decision has been recorded yet.");
        }
        else
        {
            evidence.Add($"{approvals.Count} approval decision(s) recorded.");
        }

        var workItemKeys = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => w.CandidateId == candidate.Id)
            .Select(w => w.WorkItemKey)
            .ToListAsync(ct);

        if (workItemKeys.Count == 0) return;

        // Sign-off is not held on the bundle row — it is scoped to (key, product, service, target),
        // so the same item approved for one environment does not count as approved for the next.
        var approvedKeys = await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => a.Product == candidate.Product
                     && a.Service == candidate.Service
                     && a.TargetEnv == candidate.TargetEnv
                     && a.Decision == WorkItemDecision.Approved
                     && workItemKeys.Contains(a.WorkItemKey))
            .Select(a => a.WorkItemKey)
            .Distinct()
            .ToListAsync(ct);

        var outstanding = workItemKeys.Distinct().Count() - approvedKeys.Count;
        if (outstanding > 0)
        {
            observations.Add(Observations.WorkItemsOutstanding);
            evidence.Add($"{outstanding} of {workItemKeys.Distinct().Count()} work item(s) are not signed off for {candidate.TargetEnv}.");
        }
        else
        {
            evidence.Add($"All {workItemKeys.Distinct().Count()} work item(s) are signed off.");
        }
    }

    private PromotionDiagnosis Build(
        PromotionCandidate candidate, TimeSpan age, string symptom,
        List<string> evidence, HashSet<string> observations)
    {
        var matched = _playbooks.Match(symptom, observations);
        var playbook = _playbooks.ForSymptom(symptom);

        return new PromotionDiagnosis(
            candidate.Id, candidate.Product, candidate.Service, candidate.SourceEnv,
            candidate.TargetEnv, candidate.Version, candidate.Status.ToString(),
            candidate.ApprovedAt, age, playbook?.Summary,
            evidence, [.. observations], matched);
    }

    /// <summary>
    /// Whether a subscription would receive this event for this candidate. Mirrors the dispatcher's
    /// filter semantics: an empty filter list means "any", so an unfiltered subscription matches.
    /// </summary>
    private static bool Subscribes(WebhookSubscription sub, string eventType, PromotionCandidate candidate)
    {
        if (!ParseList(sub.EventsJson).Contains(eventType, StringComparer.OrdinalIgnoreCase))
            return false;

        return MatchesFilter(sub.FilterProductsJson, candidate.Product)
            && MatchesFilter(sub.FilterServicesJson, candidate.Service)
            && MatchesFilter(sub.FilterEnvironmentsJson, candidate.TargetEnv);
    }

    private static bool MatchesFilter(string json, string value)
    {
        var list = ParseList(json);
        return list.Count == 0 || list.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ParseList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Describe(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "less than a minute",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} minute(s)",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} hour(s)",
        _ => $"{(int)span.TotalDays} day(s)",
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>The result of diagnosing one failed deployment.</summary>
public record DeploymentDiagnosis(
    Guid EventId,
    string Product,
    string Service,
    string Environment,
    string Version,
    string Status,
    string Source,
    DateTimeOffset DeployedAt,
    string? SymptomSummary,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Observations,
    IReadOnlyList<string> LogExcerpts,
    IReadOnlyList<MatchedCause> ProbableCauses);

/// <summary>The result of diagnosing one promotion.</summary>
public record PromotionDiagnosis(
    Guid CandidateId,
    string Product,
    string Service,
    string SourceEnv,
    string TargetEnv,
    string Version,
    string Status,
    DateTimeOffset? ApprovedAt,
    TimeSpan Age,
    string? SymptomSummary,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Observations,
    IReadOnlyList<MatchedCause> ProbableCauses);
