namespace Platform.Api.Features.Deployments.Models;

public record Enrichment(
    Dictionary<string, string> Labels,
    List<Participant> Participants,
    DateTimeOffset EnrichedAt);
