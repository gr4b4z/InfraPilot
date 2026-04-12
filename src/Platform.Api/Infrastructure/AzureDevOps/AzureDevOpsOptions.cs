namespace Platform.Api.Infrastructure.AzureDevOps;

public class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";
    public Dictionary<string, AzureDevOpsConnection> Connections { get; set; } = [];
}

public class AzureDevOpsConnection
{
    public string OrganizationUrl { get; set; } = "";
    public string Project { get; set; } = "";
    public string Pat { get; set; } = "";
}
