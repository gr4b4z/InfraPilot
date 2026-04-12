using System.Text.Json.Serialization;

namespace Platform.Api.Infrastructure.AzureDevOps;

// --- Request models ---

public class QueueBuildRequest
{
    [JsonPropertyName("definition")]
    public BuildDefinitionRef Definition { get; set; } = new();

    [JsonPropertyName("sourceBranch")]
    public string? SourceBranch { get; set; }

    [JsonPropertyName("parameters")]
    public string? Parameters { get; set; }
}

public class BuildDefinitionRef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

// --- Pipelines Run API (supports YAML template parameters) ---

public class PipelineRunRequest
{
    [JsonPropertyName("templateParameters")]
    public Dictionary<string, string>? TemplateParameters { get; set; }

    [JsonPropertyName("resources")]
    public PipelineRunResources? Resources { get; set; }
}

public class PipelineRunResources
{
    [JsonPropertyName("repositories")]
    public Dictionary<string, PipelineRepositoryRef>? Repositories { get; set; }
}

public class PipelineRepositoryRef
{
    [JsonPropertyName("refName")]
    public string? RefName { get; set; }
}

public class PipelineRunResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("_links")]
    public BuildLinks? Links { get; set; }

    [JsonPropertyName("pipeline")]
    public PipelineRef? Pipeline { get; set; }
}

public class PipelineRef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

// --- Response models ---

public class BuildResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("buildNumber")]
    public string? BuildNumber { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("_links")]
    public BuildLinks? Links { get; set; }

    [JsonPropertyName("definition")]
    public BuildDefinitionInfo? Definition { get; set; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }

    [JsonPropertyName("finishTime")]
    public DateTimeOffset? FinishTime { get; set; }

    [JsonPropertyName("sourceBranch")]
    public string? SourceBranch { get; set; }

    /// <summary>
    /// Azure DevOps build statuses: notStarted, inProgress, completed, cancelling, postponed, notSet
    /// </summary>
    public bool IsCompleted => Status?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsInProgress => Status?.Equals("inProgress", StringComparison.OrdinalIgnoreCase) == true
                                || Status?.Equals("notStarted", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Azure DevOps build results: succeeded, partiallySucceeded, failed, canceled, none
    /// </summary>
    public bool IsSucceeded => Result?.Equals("succeeded", StringComparison.OrdinalIgnoreCase) == true
                               || Result?.Equals("partiallySucceeded", StringComparison.OrdinalIgnoreCase) == true;
}

public class BuildLinks
{
    [JsonPropertyName("web")]
    public BuildLink? Web { get; set; }

    [JsonPropertyName("timeline")]
    public BuildLink? Timeline { get; set; }
}

public class BuildLink
{
    [JsonPropertyName("href")]
    public string? Href { get; set; }
}

public class BuildDefinitionInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

// --- Timeline (for future log display) ---

public class BuildTimelineResponse
{
    [JsonPropertyName("records")]
    public List<TimelineRecord> Records { get; set; } = [];
}

public class TimelineRecord
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }

    [JsonPropertyName("finishTime")]
    public DateTimeOffset? FinishTime { get; set; }

    [JsonPropertyName("errorCount")]
    public int? ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int? WarningCount { get; set; }
}
