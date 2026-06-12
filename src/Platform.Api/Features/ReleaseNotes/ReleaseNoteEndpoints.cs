using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.ReleaseNotes.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ReleaseNotes;

public static class ReleaseNoteEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static RouteGroupBuilder MapReleaseNoteEndpoints(this RouteGroupBuilder group)
    {
        // --- Phase 1: raw preview ---
        group.MapGet("/preview/raw", async (
            ReleaseNoteService service,
            string? product, string? environment,
            DateTimeOffset? from, DateTimeOffset? to,
            CancellationToken ct) =>
        {
            var validation = ValidatePreviewQuery(product, environment, from, to);
            if (validation is not null) return validation;

            var raw = await service.GetRawPreview(product!, environment!, from!.Value, to!.Value, ct);
            return Results.Ok(raw);
        });

        // --- Phase 2: rendered preview + template CRUD ---
        group.MapGet("/preview", async (
            ReleaseNoteService service,
            ReleaseNoteTemplateService templates,
            TemplateEngine engine,
            string? product, string? environment,
            DateTimeOffset? from, DateTimeOffset? to,
            CancellationToken ct) =>
        {
            var validation = ValidatePreviewQuery(product, environment, from, to);
            if (validation is not null) return validation;

            var raw = await service.GetRawPreview(product!, environment!, from!.Value, to!.Value, ct);
            var template = await templates.GetTemplate(product, environment, ct: ct);
            try
            {
                var rendered = engine.Render(template, raw);
                return Results.Ok(new { rendered, raw });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Template render failed: {ex.Message}" });
            }
        });

        group.MapGet("/template", async (
            ReleaseNoteTemplateService templates,
            string? product, string? environment, bool? exact,
            CancellationToken ct) =>
        {
            var template = await templates.GetTemplate(product, environment, exact ?? false, ct);
            return Results.Ok(new
            {
                product = product ?? "",
                environment = environment ?? "",
                template,
            });
        });

        group.MapPut("/template", async (
            ReleaseNoteTemplateService templates,
            SaveTemplateRequest body,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrEmpty(body.Template))
                return Results.BadRequest(new { error = "'template' is required" });
            await templates.SaveTemplate(body.Product, body.Environment, body.Template, ct);
            return Results.NoContent();
        });

        // --- Phase 3: persist + dispatch ---
        group.MapPost("/generate", async (
            PlatformDbContext db,
            ReleaseNoteService service,
            ReleaseNoteTemplateService templates,
            TemplateEngine engine,
            MarkdownRenderer markdown,
            IWebhookDispatcher webhooks,
            GenerateReleaseNoteRequest body,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Product) || string.IsNullOrWhiteSpace(body.Environment))
                return Results.BadRequest(new { error = "'product' and 'environment' are required" });

            // Auto-derive window from last published note when not provided.
            var to = body.To ?? DateTimeOffset.UtcNow;
            DateTimeOffset from;
            if (body.From.HasValue)
            {
                from = body.From.Value;
            }
            else
            {
                var last = await db.ReleaseNotes
                    .Where(r => r.Product == body.Product && r.Environment == body.Environment)
                    .OrderByDescending(r => r.GeneratedAt)
                    .Select(r => (DateTimeOffset?)r.GeneratedAt)
                    .FirstOrDefaultAsync(ct);
                from = last ?? to.AddDays(-1);
            }

            if (from > to) return Results.BadRequest(new { error = "'from' must be <= 'to'" });

            var raw = await service.GetRawPreview(body.Product, body.Environment, from, to, ct);

            // Reject empty windows up front so pipelines don't spam the webhook with
            // header-only notes. Callers that genuinely want an empty note can opt in
            // by supplying their own `renderedContent` (the edit-in-UI path).
            if (raw.Services.Count == 0 && string.IsNullOrEmpty(body.RenderedContent))
            {
                return Results.BadRequest(new
                {
                    error = "No services deployed in the given window — nothing to release.",
                    code = "no_services",
                    product = body.Product,
                    environment = body.Environment,
                    from,
                    to,
                });
            }

            string rendered;
            // Edited-in-UI path: caller supplied final markdown after previewing.
            // Skip the template render entirely so user tweaks are preserved verbatim.
            if (!string.IsNullOrEmpty(body.RenderedContent))
            {
                rendered = body.RenderedContent;
            }
            else
            {
                var template = await templates.GetTemplate(body.Product, body.Environment, ct: ct);
                try { rendered = engine.Render(template, raw); }
                catch (Exception ex) { return Results.BadRequest(new { error = $"Template render failed: {ex.Message}" }); }
            }

            var note = new ReleaseNote
            {
                Id = Guid.NewGuid(),
                Product = body.Product,
                Environment = body.Environment,
                From = from,
                To = to,
                GeneratedAt = DateTimeOffset.UtcNow,
                RenderedContent = rendered,
                RawJson = JsonSerializer.Serialize(raw, JsonOptions),
                Status = "published",
                ServicesCount = raw.Services.Count,
            };
            db.ReleaseNotes.Add(note);
            await db.SaveChangesAsync(ct);

            var filters = new WebhookEventFilters(note.Product, note.Environment);

            // Primary event — markdown only. Small payload, what most consumers want.
            await webhooks.DispatchAsync("release_note.generated", new
            {
                note.Id,
                note.Product,
                note.Environment,
                note.From,
                note.To,
                note.GeneratedAt,
                renderedContent = note.RenderedContent,
                services = raw.Services,
            }, filters);

            // Secondary event — adds `renderedHtml` for consumers that can't parse
            // markdown (Confluence storage format, HTML-only mail templates, etc.).
            // Dispatched as a separate event so markdown subscribers don't pay the
            // payload-size cost they don't need. The matching subscription opts in
            // by subscribing to `release_note.generated.html` explicitly.
            //
            // Rendered server-side once and reused; the webhook delivery worker
            // serialises this payload per subscription, so doing the markdown→HTML
            // conversion up here keeps it O(1) instead of O(subscriptions).
            var html = markdown.ToHtml(note.RenderedContent);
            await webhooks.DispatchAsync("release_note.generated.html", new
            {
                note.Id,
                note.Product,
                note.Environment,
                note.From,
                note.To,
                note.GeneratedAt,
                renderedContent = note.RenderedContent,
                renderedHtml = html,
                services = raw.Services,
            }, filters);

            return Results.Created($"/api/release-notes/{note.Id}", new
            {
                note.Id,
                note.Product,
                note.Environment,
                note.From,
                note.To,
                note.GeneratedAt,
                note.ServicesCount,
                note.Status,
                note.RenderedContent,
            });
        });

        group.MapGet("/", async (
            PlatformDbContext db, string? product, string? environment,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            var query = db.ReleaseNotes.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(product)) query = query.Where(r => r.Product == product);
            if (!string.IsNullOrEmpty(environment)) query = query.Where(r => r.Environment == environment);

            var resolvedPage = Math.Max(page ?? 1, 1);
            var resolvedPageSize = Math.Clamp(pageSize ?? 10, 1, 50);
            var total = await query.CountAsync(ct);

            var rows = await query
                .OrderByDescending(r => r.GeneratedAt)
                .Skip((resolvedPage - 1) * resolvedPageSize)
                .Take(resolvedPageSize)
                .Select(r => new ReleaseNoteFeedItemDto(
                    r.Id, r.Product, r.Environment, r.From, r.To, r.GeneratedAt,
                    r.ServicesCount, r.Status, r.RenderedContent))
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<ReleaseNoteFeedItemDto>(rows, total, resolvedPage, resolvedPageSize));
        });

        group.MapGet("/{id:guid}", async (
            PlatformDbContext db, Guid id, CancellationToken ct) =>
        {
            var note = await db.ReleaseNotes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (note is null) return Results.NotFound();

            RawPreviewDto? raw = null;
            try { raw = JsonSerializer.Deserialize<RawPreviewDto>(note.RawJson, JsonOptions); }
            catch { /* tolerate bad JSON */ }

            return Results.Ok(new ReleaseNoteDetailDto(
                note.Id, note.Product, note.Environment, note.From, note.To, note.GeneratedAt,
                note.RenderedContent, note.Status,
                raw ?? new RawPreviewDto(note.Product, note.Environment, note.From, note.To, note.GeneratedAt, [])));
        });

        return group;
    }

    private static IResult? ValidatePreviewQuery(string? product, string? environment, DateTimeOffset? from, DateTimeOffset? to)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(product)) errors.Add("'product' is required");
        if (string.IsNullOrWhiteSpace(environment)) errors.Add("'environment' is required");
        if (!from.HasValue) errors.Add("'from' is required");
        if (!to.HasValue) errors.Add("'to' is required");
        if (from.HasValue && to.HasValue && from.Value > to.Value)
            errors.Add("'from' must be <= 'to'");
        return errors.Count > 0 ? Results.BadRequest(new { errors }) : null;
    }
}
