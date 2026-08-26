using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.ReleaseNotes.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ReleaseNotes;

public class ReleaseNoteService
{
    private readonly PlatformDbContext _db;

    public ReleaseNoteService(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Aggregates DeployEvents for the given product/environment in the [from, to] window
    /// into a structured release-notes payload, one entry per service (latest event wins).
    /// <para>
    /// A service whose latest in-window version is the one that was already live when the window
    /// opened is left out: nothing shipped, so there is nothing to announce. This makes the note a
    /// diff of deployed state rather than a log of deploy events, which is the distinction that
    /// keeps a re-run from being reported as a release — see <see cref="GetVersionsLiveBeforeAsync"/>.
    /// </para>
    /// </summary>
    public async Task<RawPreviewDto> GetRawPreview(
        string product, string environment,
        DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        var events = await _db.DeployEvents.AsNoTracking()
            .Where(e =>
                e.Product == product &&
                e.Environment == environment &&
                e.DeployedAt >= from &&
                e.DeployedAt <= to &&
                e.Status == "succeeded")
            .OrderByDescending(e => e.DeployedAt)
            .ToListAsync(ct);

        var latestPerService = events
            .GroupBy(e => e.Service)
            .Select(g => g.OrderByDescending(e => e.DeployedAt).First())
            .ToList();

        var versionsLiveBefore = await GetVersionsLiveBeforeAsync(
            product, environment, latestPerService.Select(e => e.Service).ToList(), from, ct);

        var services = latestPerService
            .Where(e => !IsUnchanged(e, versionsLiveBefore))
            .Select(MapService)
            .OrderBy(s => s.Service)
            .ToList();

        return new RawPreviewDto(
            Product: product,
            Environment: environment,
            From: from,
            To: to,
            GeneratedAt: DateTimeOffset.UtcNow,
            Services: services);
    }

    /// <summary>
    /// True when the service ended the window on the version it started it on. A deploy that
    /// re-ships the version already running is a real event worth recording — the ingest keeps it,
    /// and analytics counts it as a redeploy — but it is not a release, and announcing it puts a
    /// notification in a channel about work that was already announced when it first shipped.
    /// <para>
    /// Compared against the deployed history rather than the event's own
    /// <see cref="DeployEvent.PreviousVersion"/>, because that field is whatever the sender asserted:
    /// a pipeline that reports a fixed predecessor on every run leaves it permanently unequal to
    /// <see cref="DeployEvent.Version"/>, and a check against it would never fire.
    /// </para>
    /// </summary>
    private static bool IsUnchanged(DeployEvent latest, IReadOnlyDictionary<string, string> versionsLiveBefore)
        => versionsLiveBefore.TryGetValue(latest.Service, out var before)
           && string.Equals(before, latest.Version, StringComparison.Ordinal);

    /// <summary>
    /// The version each of <paramref name="services"/> was running immediately before
    /// <paramref name="from"/> — the "old manifest" side of the diff. A service with no earlier
    /// succeeded deploy is absent from the result, which is what makes a first-ever deploy report.
    /// </summary>
    private async Task<Dictionary<string, string>> GetVersionsLiveBeforeAsync(
        string product, string environment, IReadOnlyList<string> services,
        DateTimeOffset from, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (services.Count == 0) return result;

        // Two round trips rather than one so the shape stays inside what every provider translates:
        // the newest timestamp per service, then the rows carrying it.
        var newestBefore = await _db.DeployEvents.AsNoTracking()
            .Where(e =>
                e.Product == product &&
                e.Environment == environment &&
                e.Status == "succeeded" &&
                e.DeployedAt < from &&
                services.Contains(e.Service))
            .GroupBy(e => e.Service)
            .Select(g => new { Service = g.Key, DeployedAt = g.Max(e => e.DeployedAt) })
            .ToListAsync(ct);
        if (newestBefore.Count == 0) return result;

        var stamps = newestBefore.Select(x => x.DeployedAt).Distinct().ToList();
        var rows = await _db.DeployEvents.AsNoTracking()
            .Where(e =>
                e.Product == product &&
                e.Environment == environment &&
                e.Status == "succeeded" &&
                services.Contains(e.Service) &&
                stamps.Contains(e.DeployedAt))
            .Select(e => new { e.Service, e.DeployedAt, e.Version })
            .ToListAsync(ct);

        var newestByService = newestBefore.ToDictionary(x => x.Service, x => x.DeployedAt, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (newestByService.TryGetValue(row.Service, out var stamp) && stamp == row.DeployedAt)
                result[row.Service] = row.Version;
        }
        return result;
    }

    private static ServiceReleaseDto MapService(DeployEvent e)
    {
        var refs = e.References;
        var workItems = refs
            .Where(r => string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase))
            .Select(r => new WorkItemSummaryDto(
                Key: r.Key ?? "",
                Title: r.Title,
                Type: r.Provider,
                Url: r.Url))
            .Where(w => !string.IsNullOrEmpty(w.Key))
            .ToList();

        var pullRequests = refs
            .Where(r => string.Equals(r.Type, "pull-request", StringComparison.OrdinalIgnoreCase))
            .Select(r => new PullRequestSummaryDto(
                Key: r.Key ?? r.Revision,
                Title: r.Title,
                Url: r.Url))
            .ToList();

        var pipelines = refs
            .Where(r => string.Equals(r.Type, "pipeline", StringComparison.OrdinalIgnoreCase))
            .Select(r => new PipelineSummaryDto(
                Key: r.Key ?? r.Revision,
                Title: r.Title,
                Url: r.Url))
            .ToList();

        // Participants: combine event-level and reference-level, dedupe by (role, email|displayName).
        var allParticipants = new List<ParticipantSummaryDto>();
        foreach (var p in e.Participants)
            allParticipants.Add(new ParticipantSummaryDto(p.Role, p.DisplayName, p.Email));
        foreach (var r in refs)
        {
            if (r.Participants is null) continue;
            foreach (var p in r.Participants)
                allParticipants.Add(new ParticipantSummaryDto(p.Role, p.DisplayName, p.Email));
        }
        var participants = allParticipants
            .GroupBy(p => $"{p.Role}\0{p.Email ?? p.DisplayName ?? ""}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Single-best-match shortcuts for the common roles so templates can write
        // `{{author.displayName}}` / `{{author.email}}` without a `{{#each participants}}` loop.
        ParticipantSummaryDto? Pick(params string[] roles) =>
            participants.FirstOrDefault(p => roles.Any(r =>
                string.Equals(p.Role, r, StringComparison.OrdinalIgnoreCase)));

        return new ServiceReleaseDto(
            Service: e.Service,
            PreviousVersion: e.PreviousVersion,
            CurrentVersion: e.Version,
            IsRollback: e.IsRollback,
            DeployedAt: e.DeployedAt,
            WorkItems: workItems,
            PullRequests: pullRequests,
            Pipelines: pipelines,
            Participants: participants,
            Author: Pick("author"),
            Qa: Pick("qa"),
            TriggeredBy: Pick("triggered-by", "triggeredBy"));
    }
}
