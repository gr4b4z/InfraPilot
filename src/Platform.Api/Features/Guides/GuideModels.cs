using System.Text.Json.Serialization;

namespace Platform.Api.Features.Guides;

/// <summary>
/// A task-oriented walkthrough of one action in the portal — "how do I roll a service back",
/// "how do I approve a promotion". Authored as YAML under the guides directory, loaded once at
/// startup, and surfaced to the agent through the search_guides / start_guide tools.
/// </summary>
/// <remarks>
/// Guides are deliberately separate from the docs site content (docs/src/content/siteContent.ts),
/// which documents installing and configuring InfraPilot. A guide describes operating the running
/// UI, so it carries route and anchor information that prose cannot.
/// </remarks>
public class GuideDefinition
{
    /// <summary>Stable kebab-case identifier, e.g. "request-rollback". Referenced by start_guide.</summary>
    public string Id { get; set; } = "";

    /// <summary>Short imperative title, e.g. "Request a rollback".</summary>
    public string Title { get; set; } = "";

    /// <summary>One sentence describing what the guide accomplishes. Shown in search results.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Grouping for listing, e.g. "Rollbacks", "Promotions".</summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// Alternate phrasings a user might ask. Matched by the keyword search so that "revert a
    /// deploy" finds the rollback guide even though it shares no words with the title.
    /// </summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Route the walkthrough starts on, e.g. "/rollbacks". May contain :params.</summary>
    public string Route { get; set; } = "";

    /// <summary>Gating — used to tailor the answer, never to hide a guide outright.</summary>
    public GuideRequirements Requires { get; set; } = new();

    /// <summary>Ordered steps. At least one.</summary>
    public List<GuideStep> Steps { get; set; } = [];

    /// <summary>Ids of related guides, offered as follow-ups.</summary>
    public List<string> Related { get; set; } = [];

    /// <summary>Absolute path of the YAML file this came from. Diagnostic only.</summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }
}

/// <summary>
/// What a user needs in order to complete a guide. Both are advisory: a user who lacks the role
/// still gets the steps, prefixed with who they need to ask. Hiding the guide would leave the
/// agent unable to explain why the button is missing, which is the actual question being asked.
/// </summary>
public class GuideRequirements
{
    /// <summary>Feature flag key that must be enabled, e.g. "Rollbacks". Null means always available.</summary>
    public string? FeatureFlag { get; set; }

    /// <summary>
    /// Roles that can complete the action, e.g. ["InfraPortal.Admin"]. Empty means any signed-in
    /// user. Matched case-insensitively against the caller's role claims.
    /// </summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>One step of a walkthrough.</summary>
public class GuideStep
{
    /// <summary>Instruction text shown in the chat and in the spotlight tooltip.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// Value of the `data-guide-anchor` attribute on the element this step refers to. When set and
    /// the element is on screen, the frontend spotlights it; when absent or not found, the step
    /// still renders as text. Anchors are asserted to exist by GuideAnchorTests.
    /// </summary>
    public string? Anchor { get; set; }

    /// <summary>
    /// Route to navigate to before showing this step, when it differs from the previous one.
    /// Lets a guide cross pages, e.g. from /rollbacks into /settings/rollbacks.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>Optional note — a caveat or "what you should see" confirmation.</summary>
    public string? Note { get; set; }
}
