using System.Text.Json;

namespace Platform.Api.Features.Deployments.Models;

public class DeployEvent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public Guid Id { get; set; }
    public string Product { get; set; } = "";
    public string Service { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public bool IsRollback { get; set; }
    /// <summary>succeeded, failed, in_progress, or <see cref="DeployEventStatuses.Removed"/>.</summary>
    public string Status { get; set; } = "succeeded";
    public string Source { get; set; } = "";
    public DateTimeOffset DeployedAt { get; set; }
    public string ReferencesJson { get; set; } = "[]";
    public string ParticipantsJson { get; set; } = "[]";
    public string? EnrichmentJson { get; set; }
    public string MetadataJson { get; set; } = "{}";
    /// <summary>Serialized <see cref="Models.DeployRun"/>. Null for events with no CI run behind them (manual entries).</summary>
    public string? RunJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Convenience accessors (not mapped to DB)
    public List<Reference> References
    {
        get => JsonSerializer.Deserialize<List<Reference>>(ReferencesJson, JsonOptions) ?? [];
        set => ReferencesJson = JsonSerializer.Serialize(value, JsonOptions);
    }

    public List<Participant> Participants
    {
        get => JsonSerializer.Deserialize<List<Participant>>(ParticipantsJson, JsonOptions) ?? [];
        set => ParticipantsJson = JsonSerializer.Serialize(value, JsonOptions);
    }

    public Enrichment? Enrichment
    {
        get => string.IsNullOrEmpty(EnrichmentJson) ? null : JsonSerializer.Deserialize<Enrichment>(EnrichmentJson, JsonOptions);
        set => EnrichmentJson = value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    }

    public Dictionary<string, object>? Metadata
    {
        get => JsonSerializer.Deserialize<Dictionary<string, object>>(MetadataJson, JsonOptions);
        set => MetadataJson = JsonSerializer.Serialize(value ?? new Dictionary<string, object>(), JsonOptions);
    }

    public DeployRun? Run
    {
        get => string.IsNullOrEmpty(RunJson) ? null : JsonSerializer.Deserialize<DeployRun>(RunJson, JsonOptions);
        set => RunJson = value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    }
}

/// <summary>
/// Deploy event statuses that carry meaning beyond "it worked" or "it did not". The others are plain
/// strings where they are used.
/// </summary>
public static class DeployEventStatuses
{
    /// <summary>
    /// The service was taken out of the environment: uninstalled, and no longer declared by the
    /// release pipeline. As a cell's newest event it means the environment runs nothing of the
    /// service, so current-state views drop the cell. It is not a deploy: analytics leave it out,
    /// and it does not bring back a service an admin retired. <c>Version</c> is the version that
    /// was removed.
    /// </summary>
    public const string Removed = "removed";
}
