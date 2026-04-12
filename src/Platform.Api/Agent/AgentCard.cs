using System.Text.Json.Serialization;

namespace Platform.Api.Agent;

/// <summary>
/// A structured data card returned alongside agent text replies.
/// The frontend renders a type-specific component for each card.
/// </summary>
public class AgentCard
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

// ─── DTOs shaped for card rendering ───

public class RequestCardItem
{
    public Guid Id { get; set; }
    public string ServiceName { get; set; } = "";
    public string Status { get; set; } = "";
    public string RequesterName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class RequestDetailCardData
{
    public Guid Id { get; set; }
    public string ServiceName { get; set; } = "";
    public string Status { get; set; } = "";
    public string RequesterName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Dictionary<string, object?>? Inputs { get; set; }
    public string? ExecutionStatus { get; set; }
    public string? ExecutionOutput { get; set; }
    public string? ApprovalStatus { get; set; }
    public List<ApprovalDecisionDto>? Decisions { get; set; }
}

public class ApprovalDecisionDto
{
    public string ApproverName { get; set; } = "";
    public string Decision { get; set; } = "";
    public string? Comment { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

public class TimelineEventDto
{
    public string Action { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string Module { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}

public class SummaryCardData
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int AwaitingApproval { get; set; }
    public int Executing { get; set; }
    public int Other { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public List<RequestCardItem> Items { get; set; } = [];
}

// ─── Deployment card DTOs ───

public class DeploymentCellDto
{
    public string Service { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public DateTimeOffset DeployedAt { get; set; }
}

public class DeploymentStateCardData
{
    public string Product { get; set; } = "";
    public List<string> Services { get; set; } = [];
    public List<string> Environments { get; set; } = [];
    public List<DeploymentCellDto> Cells { get; set; } = [];
}

public class DeploymentRefDto
{
    public string Type { get; set; } = "";
    public string? Key { get; set; }
    public string? Url { get; set; }
}

public class DeploymentParticipantDto
{
    public string Role { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
}

public class DeploymentActivityItemDto
{
    public string Service { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public DateTimeOffset DeployedAt { get; set; }
    public string Source { get; set; } = "";
    public string? WorkItemKey { get; set; }
    public string? WorkItemTitle { get; set; }
    public string? PrUrl { get; set; }
    public List<DeploymentParticipantDto> Participants { get; set; } = [];
}

public class DeploymentActivityCardData
{
    public string? Product { get; set; }
    public string? Environment { get; set; }
    public DateTimeOffset Since { get; set; }
    public List<DeploymentActivityItemDto> Items { get; set; } = [];
    /// <summary>
    /// URL path with query params to navigate the user to the full deployment view.
    /// </summary>
    public string? NavigationUrl { get; set; }
}
