using System.Text.Json.Serialization;

namespace Platform.Api.Features.Knowledge;

/// <summary>
/// A reference topic the assistant can answer from — what a webhook does, why a promotion policy is
/// shaped the way it is, which services run in which environment, how a delivery track works.
/// </summary>
/// <remarks>
/// Distinct from a guide on purpose. A guide is a sequence of actions in this UI and carries
/// anchors; a topic is prose that explains the system around it, much of which lives outside
/// InfraPilot entirely (the pipelines in <c>mpt-release</c>, the monorepo in <c>marketplace</c>,
/// the templates in <c>ops-build-templates-aks-releases</c>). Forcing that into steps would produce
/// confident nonsense.
///
/// Because the facts describe systems this repository does not own, every topic carries
/// <see cref="Source"/> and <see cref="AsOf"/>, and the assistant is instructed to quote them. A
/// reader can then tell a current answer from one that predates a pipeline change.
/// </remarks>
public class KnowledgeTopic
{
    /// <summary>Stable kebab-case identifier, e.g. "webhook-catalogue".</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>One sentence describing what the topic covers. Shown in search results.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Grouping for listing, e.g. "Webhooks", "Policies", "Topology".</summary>
    public string Group { get; set; } = "";

    /// <summary>Alternate phrasings a user might ask, so wording need not match the title.</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Free-form keywords — service names, event types, environment names.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>The answer itself, as Markdown. Tables are welcome; the chat renders them.</summary>
    public string Body { get; set; } = "";

    /// <summary>Where the facts came from, e.g. "mpt-release/docs/release-pipelines.md".</summary>
    public string? Source { get; set; }

    /// <summary>ISO date the facts were last verified against the live system.</summary>
    public string? AsOf { get; set; }

    /// <summary>Ids of related topics, offered as follow-ups.</summary>
    public List<string> Related { get; set; } = [];

    /// <summary>Ids of guides that let the user act on this topic.</summary>
    public List<string> RelatedGuides { get; set; } = [];

    [JsonIgnore]
    public string? SourcePath { get; set; }
}
