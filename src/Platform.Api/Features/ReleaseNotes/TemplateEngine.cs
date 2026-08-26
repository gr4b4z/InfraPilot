using HandlebarsDotNet;
using Platform.Api.Features.ReleaseNotes.Models;

namespace Platform.Api.Features.ReleaseNotes;

/// <summary>
/// Thin wrapper around Handlebars.Net. Renders a release-note template against a
/// <see cref="RawPreviewDto"/>. Templates use camelCase field names matching the DTO.
/// </summary>
public class TemplateEngine
{
    private readonly IHandlebars _hb;

    public TemplateEngine()
    {
        _hb = Handlebars.Create();
        _hb.Configuration.ThrowOnUnresolvedBindingExpression = false;
    }

    public string Render(string template, RawPreviewDto data)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var compiled = _hb.Compile(template);
        var ctx = ToContext(data);
        return compiled(ctx);
    }

    /// <summary>
    /// Convert the DTO to a plain dictionary tree so Handlebars resolves keys
    /// case-insensitively against the camelCase names operators write in templates.
    /// </summary>
    private static object ToContext(RawPreviewDto d) => new
    {
        product = d.Product,
        environment = d.Environment,
        from = d.From.ToString("u"),
        to = d.To.ToString("u"),
        date = d.GeneratedAt.ToString("yyyy-MM-dd"),
        generatedAt = d.GeneratedAt.ToString("u"),
        services = d.Services.Select(s => new
        {
            service = s.Service,
            previousVersion = s.PreviousVersion ?? "—",
            currentVersion = s.CurrentVersion,
            isRollback = s.IsRollback,
            deployedAt = s.DeployedAt.ToString("u"),
            // Convenience: first pull-request / pipeline so templates can write
            // {{pullRequest.url}} without an {{#each}} for the common single-PR case.
            pullRequest = s.PullRequests.FirstOrDefault() is { } pr
                ? new { key = pr.Key ?? "", title = pr.Title ?? "", url = pr.Url ?? "" }
                : null,
            pipeline = s.Pipelines.FirstOrDefault() is { } pl
                ? new { key = pl.Key ?? "", title = pl.Title ?? "", url = pl.Url ?? "" }
                : null,
            author = s.Author is { } a
                ? new { displayName = a.DisplayName ?? a.Email ?? "", email = a.Email ?? "" }
                : null,
            qa = s.Qa is { } q
                ? new { displayName = q.DisplayName ?? q.Email ?? "", email = q.Email ?? "" }
                : null,
            triggeredBy = s.TriggeredBy is { } tb
                ? new { displayName = tb.DisplayName ?? tb.Email ?? "", email = tb.Email ?? "" }
                : null,
            workItems = s.WorkItems.Select(w => new
            {
                key = w.Key,
                title = w.Title ?? "",
                type = w.Type ?? "",
                url = w.Url ?? "",
            }).ToList(),
            pullRequests = s.PullRequests.Select(p => new
            {
                key = p.Key ?? "",
                title = p.Title ?? "",
                url = p.Url ?? "",
            }).ToList(),
            pipelines = s.Pipelines.Select(p => new
            {
                key = p.Key ?? "",
                title = p.Title ?? "",
                url = p.Url ?? "",
            }).ToList(),
            participants = s.Participants.Select(p => new
            {
                role = p.Role,
                displayName = p.DisplayName ?? "",
                email = p.Email ?? "",
            }).ToList(),
        }).ToList(),
    };
}
