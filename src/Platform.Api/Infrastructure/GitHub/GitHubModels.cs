using System.Text.Json.Serialization;

namespace Platform.Api.Infrastructure.GitHub;

public class GitHubIssue
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>"open" or "closed".</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>"completed", "not_planned", "duplicate", "reopened" or null when closed without a reason.</summary>
    [JsonPropertyName("state_reason")]
    public string? StateReason { get; set; }

    [JsonPropertyName("closed_at")]
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);
    public bool IsClosed => string.Equals(State, "closed", StringComparison.OrdinalIgnoreCase);
}

public class GitHubIssueComment
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("user")]
    public GitHubUser? User { get; set; }
}

public class GitHubUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    /// <summary>"User" or "Bot".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public class GitHubApiException : Exception
{
    public int? StatusCode { get; }

    public GitHubApiException(string message) : base(message) { }
    public GitHubApiException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    public GitHubApiException(string message, Exception inner) : base(message, inner) { }
}
