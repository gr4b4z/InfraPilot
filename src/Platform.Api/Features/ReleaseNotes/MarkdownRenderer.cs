using Markdig;

namespace Platform.Api.Features.ReleaseNotes;

/// <summary>
/// Thin wrapper around Markdig used to convert release-note markdown to HTML
/// for the secondary <c>release_note.generated.html</c> webhook. Subscribers
/// that target Confluence storage format, an HTML-only mail template, or any
/// other "I can't parse markdown" sink consume the HTML payload instead of the
/// smaller markdown one.
///
/// Markdig is configured with the "advanced" pipeline (tables, autolinks, task
/// lists, etc.) so common GitHub-flavoured markdown round-trips faithfully. The
/// pipeline instance is reused per process — it's documented as thread-safe.
/// </summary>
public class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// For callers that are pure and static and so cannot take this as a dependency — notably the
    /// webhook request builder, which converts a rendered notification to HTML for the Teams HTML
    /// target. Shared rather than a second pipeline so both paths turn the same markdown into the
    /// same HTML; a release note that reaches a channel and one that reaches a Confluence page
    /// should not differ because they were rendered by different configurations.
    /// </summary>
    public static readonly MarkdownRenderer Shared = new();

    public string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        return Markdown.ToHtml(markdown, _pipeline);
    }
}
