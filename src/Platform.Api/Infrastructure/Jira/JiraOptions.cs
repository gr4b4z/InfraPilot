namespace Platform.Api.Infrastructure.Jira;

public class JiraOptions
{
    public const string SectionName = "Jira";
    public Dictionary<string, JiraConnection> Connections { get; set; } = [];
}

public class JiraConnection
{
    public string BaseUrl { get; set; } = "";
    public string Email { get; set; } = "";
    public string ApiToken { get; set; } = "";
}
