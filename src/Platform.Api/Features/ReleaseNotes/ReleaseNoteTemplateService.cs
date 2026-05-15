using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ReleaseNotes;

/// <summary>
/// Reads/writes release-note templates from the <c>platform_settings</c> table.
/// Per-product key: <c>release-notes.template.{product}</c>
/// Default fallback key: <c>release-notes.template.default</c>
/// </summary>
public class ReleaseNoteTemplateService
{
    public const string DefaultKey = "release-notes.template.default";
    public const string KeyPrefix = "release-notes.template.";

    // Default "product-level" template. Each service gets one line plus a nested
    // bullet per work item carrying: ticket link, title, PR link, pipeline link,
    // author, QA. Keep this short and dense — operators copy it into Teams/Confluence.
    public const string DefaultTemplate = """
# 🛠️ Release: {{product}} — {{environment}}

**Date:** {{date}} | **Window:** {{from}} → {{to}}

{{#each services}}
* **{{service}}** (`{{{previousVersion}}} → {{currentVersion}}`){{#if isRollback}} ⚠️ rollback{{/if}}
{{#each workItems}}
  * [{{key}}]({{url}}) — {{{title}}}{{#if ../pullRequest}} · PR [#{{../pullRequest.key}}]({{../pullRequest.url}}){{/if}}{{#if ../pipeline}} · Build [{{../pipeline.key}}]({{../pipeline.url}}){{/if}}{{#if ../author}} · author: [{{{../author.displayName}}}](mailto:{{../author.email}}){{/if}}{{#if ../qa}} · qa: [{{{../qa.displayName}}}](mailto:{{../qa.email}}){{/if}}
{{/each}}
{{#unless workItems}}
  * _no work items_{{#if pullRequest}} · PR [#{{pullRequest.key}}]({{pullRequest.url}}){{/if}}{{#if pipeline}} · Build [{{pipeline.key}}]({{pipeline.url}}){{/if}}{{#if author}} · author: [{{{author.displayName}}}](mailto:{{author.email}}){{/if}}{{#if qa}} · qa: [{{{qa.displayName}}}](mailto:{{qa.email}}){{/if}}
{{/unless}}
{{/each}}
""";

    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _user;

    public ReleaseNoteTemplateService(PlatformDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    /// <summary>
    /// Storage key for a template scope. Templates are stored under three possible scopes:
    ///   • <c>release-notes.template.default</c>              — global fallback
    ///   • <c>release-notes.template.{product}</c>            — product default (all envs)
    ///   • <c>release-notes.template.{product}.{environment}</c> — per (product, env)
    /// Per-env wins over per-product wins over default.
    /// </summary>
    public static string KeyFor(string? product, string? environment = null)
    {
        if (string.IsNullOrWhiteSpace(product)) return DefaultKey;
        if (string.IsNullOrWhiteSpace(environment)) return $"{KeyPrefix}{product}";
        return $"{KeyPrefix}{product}.{environment}";
    }

    /// <summary>
    /// Get the template for a (product, environment) pair. Resolution order:
    /// (product+env) → (product) → default key → built-in default constant.
    /// Pass <paramref name="exactScope"/>=true to load just the row for the given key
    /// without walking the fallback chain — used by the settings editor so operators
    /// see exactly what's saved at the scope they selected.
    /// </summary>
    public async Task<string> GetTemplate(string? product, string? environment = null,
        bool exactScope = false, CancellationToken ct = default)
    {
        if (exactScope)
        {
            var row = await _db.PlatformSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == KeyFor(product, environment), ct);
            return row?.Value ?? "";
        }

        // Fallback chain: per-env → per-product → default → built-in.
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(product))
        {
            if (!string.IsNullOrWhiteSpace(environment))
                keys.Add(KeyFor(product, environment));
            keys.Add(KeyFor(product));
        }
        keys.Add(DefaultKey);

        foreach (var key in keys)
        {
            var row = await _db.PlatformSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row is not null) return row.Value;
        }
        return DefaultTemplate;
    }

    public async Task SaveTemplate(string? product, string? environment, string template,
        CancellationToken ct = default)
    {
        var key = KeyFor(product, environment);
        var existing = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var actor = !string.IsNullOrEmpty(_user.Email) ? _user.Email : _user.Name;
        if (existing is null)
        {
            _db.PlatformSettings.Add(new PlatformSetting
            {
                Key = key,
                Value = template,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = actor,
            });
        }
        else
        {
            existing.Value = template;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = actor;
        }
        await _db.SaveChangesAsync(ct);
    }
}
