using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Catalog;

public class GitHubIssueExecutorYamlTests
{
    private readonly CatalogYamlLoader _loader;

    public GitHubIssueExecutorYamlTests()
    {
        var config = Substitute.For<IConfiguration>();
        config["Catalog:Path"].Returns("nonexistent-path");
        _loader = new CatalogYamlLoader(config, Substitute.For<ILogger<CatalogYamlLoader>>());
    }

    [Fact]
    public void Deserializes_github_issue_executor_block()
    {
        const string yaml = """
            id: request-repository
            name: Request Repository
            category: ci-cd
            inputs: []
            executor:
              type: github-issue
              connection: default
              repository: acme/.github
              title: "[Repo Request] {{name}}"
              labels: [repository-request, portal]
              fields:
                - { label: "Repository name", value: "{{name}}" }
                - label: Jira key
                  value: "{{jira_key}}"
              comment: "Requested by {{request.requester_name}}"
              completion:
                closed_completed: Completed
                closed_not_planned: Failed
                failure_comment_marker: "validation failed"
                timeout_hours: 72
            """;

        var def = _loader.DeserializeDefinition(yaml);

        Assert.NotNull(def);
        var executor = def.Executor!;
        Assert.Equal("github-issue", executor.Type);
        Assert.Equal("acme/.github", executor.Repository);
        Assert.Equal("[Repo Request] {{name}}", executor.Title);
        Assert.Equal(["repository-request", "portal"], executor.Labels);
        Assert.Collection(executor.Fields,
            f => { Assert.Equal("Repository name", f.Label); Assert.Equal("{{name}}", f.Value); },
            f => { Assert.Equal("Jira key", f.Label); Assert.Equal("{{jira_key}}", f.Value); });
        Assert.Equal("Requested by {{request.requester_name}}", executor.Comment);
        Assert.Equal("validation failed", executor.Completion!.FailureCommentMarker);
        Assert.Equal(72, executor.Completion.TimeoutHours);
    }

    [Fact]
    public void Executor_block_survives_the_json_round_trip_into_a_catalog_item()
    {
        var item = new CatalogItem
        {
            Executor = new ExecutorConfig
            {
                Type = "github-issue",
                Repository = "acme/.github",
                Labels = ["repository-request"],
                Fields = [new IssueFormField { Label = "Repository name", Value = "{{name}}" }],
                Completion = new IssueCompletionConfig { FailureCommentMarker = "validation failed" },
            },
        };

        var roundTripped = item.Executor!;

        Assert.Equal("acme/.github", roundTripped.Repository);
        Assert.Equal(["repository-request"], roundTripped.Labels);
        Assert.Single(roundTripped.Fields);
        Assert.Equal("validation failed", roundTripped.Completion!.FailureCommentMarker);
    }

    [Fact]
    public void Shipped_request_repository_example_mirrors_the_issue_form()
    {
        var path = FindRepoFile(Path.Combine("catalog", "examples", "ci-cd", "request-repository.yaml"));
        var def = _loader.Load(path);

        Assert.NotNull(def);
        var executor = def.Executor!;
        Assert.Equal("github-issue", executor.Type);
        Assert.Equal(["repository-request"], executor.Labels);

        // Every section in the issue form, in template order
        Assert.Equal(
        [
            "Repository name", "Product (if applicable)", "Visibility", "Tier", "Application stack",
            "Starter template", "Owning team", "Contributor teams", "Register builds in InfraPortal?",
            "Publishes a package to a feed?", "Business justification", "Jira key",
        ], executor.Fields.Select(f => f.Label).ToArray());

        // Each field placeholder resolves to a declared input
        var inputIds = def.Inputs.Select(i => i.Id).ToHashSet();
        foreach (var field in executor.Fields)
        {
            var placeholder = field.Value.Trim('{', '}');
            Assert.Contains(placeholder, inputIds);
        }
    }

    [Fact]
    public void ValidateYaml_flags_unknown_request_variable_in_issue_templates()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new CatalogService(db, _loader, Substitute.For<ILogger<CatalogService>>());

        const string yaml = """
            id: request-repository
            name: Request Repository
            category: ci-cd
            inputs:
              - { id: name, component: TextInput, label: Repository name, required: true }
            executor:
              type: github-issue
              repository: acme/.github
              title: "[Repo Request] {{name}}"
              fields:
                - { label: "Repository name", value: "{{name}}" }
              comment: "Requested by {{request.requester}}"
            """;

        var result = service.ValidateYaml(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("{{request.requester}}") && e.Contains("{{request.requester_name}}"));

        var fixedResult = service.ValidateYaml(yaml.Replace("{{request.requester}}", "{{request.requester_name}}"));
        Assert.True(fixedResult.IsValid, string.Join("; ", fixedResult.Errors));
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
