namespace Platform.Api.Features.ReleaseNotes.Models;

/// <summary>
/// Persisted record of a generated release note. Contains both the raw structured
/// data (so the UI can re-render it) and the rendered template output (so downstream
/// consumers like Teams/Confluence don't need to call the template engine themselves).
/// </summary>
public class ReleaseNote
{
    public Guid Id { get; set; }
    public string Product { get; set; } = "";
    public string Environment { get; set; } = "";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public string RenderedContent { get; set; } = "";
    public string RawJson { get; set; } = "{}";
    public string Status { get; set; } = "published";
    public int ServicesCount { get; set; }
}
