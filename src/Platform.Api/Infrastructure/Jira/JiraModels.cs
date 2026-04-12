using System.Text.Json.Serialization;

namespace Platform.Api.Infrastructure.Jira;

public class JiraIssueResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("self")]
    public string Self { get; set; } = "";
}

public class JiraApiException : Exception
{
    public int? StatusCode { get; }

    public JiraApiException(string message) : base(message) { }
    public JiraApiException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    public JiraApiException(string message, Exception inner) : base(message, inner) { }
}
