namespace Platform.Api.Infrastructure.GitHub;

public class GitHubOptions
{
    public const string SectionName = "GitHub";
    public Dictionary<string, GitHubConnection> Connections { get; set; } = [];
}

/// <summary>
/// One GitHub API identity. The token is used as a bearer token, so it can be a fine-grained PAT
/// (Issues: read and write on the target repositories) or a GitHub App installation token.
/// </summary>
public class GitHubConnection
{
    /// <summary>REST API root. Override only for GitHub Enterprise Server (e.g. https://ghe.example.com/api/v3).</summary>
    public string ApiUrl { get; set; } = "https://api.github.com";
    public string Token { get; set; } = "";
}
