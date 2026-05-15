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

        var services = events
            .GroupBy(e => e.Service)
            .Select(g =>
            {
                var latest = g.OrderByDescending(e => e.DeployedAt).First();
                return MapService(latest);
            })
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
