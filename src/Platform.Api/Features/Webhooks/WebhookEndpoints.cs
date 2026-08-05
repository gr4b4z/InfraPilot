using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Webhooks;

public static class WebhookEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", CreateSubscription);
        group.MapGet("/", ListSubscriptions);
        group.MapGet("/{id:guid}", GetSubscription);
        group.MapPut("/{id:guid}", UpdateSubscription);
        group.MapDelete("/{id:guid}", DeleteSubscription);
        group.MapGet("/{id:guid}/deliveries", GetDeliveries);
        group.MapPost("/deliveries/{id:guid}/retry", RetryDelivery);
        group.MapPost("/{id:guid}/test", TestSubscription);

        // ── Delivery maintenance (Settings → Maintenance) ────────────────────
        // Bulk counterparts to the per-delivery retry above, plus retention. The per-row button is
        // fine for one flaky call; after a receiver outage there are hundreds of exhausted rows and
        // nothing else re-queues them — and delivered/failed rows otherwise accumulate forever.
        group.MapGet("/maintenance/deliveries", async (
            PlatformDbContext db, int? olderThanDays, CancellationToken ct) =>
        {
            if (olderThanDays is < 1 or > 3650)
                return Results.BadRequest(new { error = "olderThanDays must be between 1 and 3650" });
            var stats = await GetDeliveryMaintenanceStatsAsync(db, olderThanDays ?? 30, ct);
            return Results.Ok(stats);
        });

        group.MapPost("/maintenance/deliveries/retry-failed", async (
            PlatformDbContext db, CancellationToken ct) =>
        {
            var retried = await RetryAllFailedDeliveriesAsync(db, ct);
            return Results.Ok(new { retried });
        });

        group.MapDelete("/maintenance/deliveries", async (
            PlatformDbContext db, int? olderThanDays, CancellationToken ct) =>
        {
            if (olderThanDays is null or < 1 or > 3650)
                return Results.BadRequest(new { error = "olderThanDays is required (1–3650)" });
            var removed = await PurgeSettledDeliveriesAsync(db, olderThanDays.Value, ct);
            return Results.Ok(new { removed });
        });

        return group;
    }

    // ── Delivery maintenance internals ──────────────────────────────────────
    // Static and endpoint-independent so the rules are unit-testable without HTTP plumbing.

    public record DeliveryMaintenanceStats(int Failed, int Purgeable, DateTimeOffset? OldestFailedAt);

    public static async Task<DeliveryMaintenanceStats> GetDeliveryMaintenanceStatsAsync(
        PlatformDbContext db, int olderThanDays, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var failed = await db.WebhookDeliveries.CountAsync(d => d.Status == "failed", ct);
        var purgeable = await db.WebhookDeliveries
            .CountAsync(d => d.Status != "pending" && d.CreatedAt < cutoff, ct);
        var oldestFailed = await db.WebhookDeliveries
            .Where(d => d.Status == "failed")
            .OrderBy(d => d.CreatedAt)
            .Select(d => (DateTimeOffset?)d.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return new DeliveryMaintenanceStats(failed, purgeable, oldestFailed);
    }

    /// <summary>
    /// Re-queues every failed delivery, the same reset the per-delivery retry performs: back to
    /// pending, attempts zeroed, eligible for the worker immediately. Payloads are kept verbatim —
    /// a retry re-sends what the receiver was owed, it does not rebuild it.
    /// </summary>
    public static async Task<int> RetryAllFailedDeliveriesAsync(
        PlatformDbContext db, CancellationToken ct = default)
    {
        var failed = await db.WebhookDeliveries.Where(d => d.Status == "failed").ToListAsync(ct);
        foreach (var delivery in failed)
        {
            delivery.Status = "pending";
            delivery.NextRetryAt = DateTimeOffset.UtcNow;
            delivery.Attempts = 0;
            delivery.ErrorMessage = null;
        }
        if (failed.Count > 0) await db.SaveChangesAsync(ct);
        return failed.Count;
    }

    /// <summary>
    /// Deletes settled (delivered or failed) delivery rows older than the cutoff. Pending rows are
    /// never touched, whatever their age — they are still owed to a receiver, and deleting one
    /// silently drops an event a subscriber asked for.
    /// </summary>
    public static async Task<int> PurgeSettledDeliveriesAsync(
        PlatformDbContext db, int olderThanDays, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var stale = await db.WebhookDeliveries
            .Where(d => d.Status != "pending" && d.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;
        db.WebhookDeliveries.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    // ── Target validation ───────────────────────────────────────────────────
    // Static and endpoint-independent so the per-target rules are unit-testable without HTTP.

    /// <summary>RFC 7230 token characters — the only thing legal in an HTTP header name.</summary>
    private static readonly Regex HeaderNamePattern =
        new(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Validates the target-specific half of a create/update request. Returns an error message, or
    /// null when the combination is deliverable. <paramref name="requireSecret"/> is false on update,
    /// where omitting the secret means "keep the stored one".
    /// </summary>
    public static string? ValidateTarget(
        string targetType,
        string url,
        string? secret,
        string? signatureHeader,
        string? gitHubEventType,
        bool requireSecret)
    {
        if (!WebhookTargetTypes.IsValid(targetType))
            return $"targetType must be one of: {string.Join(", ", WebhookTargetTypes.All)}";

        var hasSignatureHeader = !string.IsNullOrWhiteSpace(signatureHeader);
        var hasGitHubEventType = !string.IsNullOrWhiteSpace(gitHubEventType);

        // A secret reaching an Authorization header must not be able to smuggle in a second header.
        if (secret is not null && secret.Any(char.IsControl))
            return "secret must not contain control characters";

        switch (targetType)
        {
            case WebhookTargetTypes.Generic:
                if (hasSignatureHeader) return "signatureHeader applies only to azure_devops targets";
                if (hasGitHubEventType) return "githubEventType applies only to github targets";
                break;

            case WebhookTargetTypes.AzureDevOps:
                if (requireSecret && string.IsNullOrWhiteSpace(secret))
                    return "secret is required for azure_devops targets — use the same value as the Incoming WebHook service connection";
                if (hasGitHubEventType) return "githubEventType applies only to github targets";
                if (hasSignatureHeader)
                {
                    var header = signatureHeader!.Trim();
                    if (header.Length > 100) return "signatureHeader must be 100 characters or fewer";
                    if (!HeaderNamePattern.IsMatch(header))
                        return "signatureHeader is not a valid HTTP header name";
                }
                break;

            case WebhookTargetTypes.GitHub:
                if (requireSecret && string.IsNullOrWhiteSpace(secret))
                    return "secret is required for github targets — supply a token with permission to dispatch repository events";
                if (hasSignatureHeader) return "signatureHeader applies only to azure_devops targets";
                if (hasGitHubEventType && gitHubEventType!.Trim().Length > 100)
                    return "githubEventType must be 100 characters or fewer";
                // The token travels in the clear over http, and repository_dispatch is the only
                // endpoint this target knows how to talk to.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    return "github targets require an absolute https URL";
                if (!uri.AbsolutePath.EndsWith("/dispatches", StringComparison.Ordinal))
                    return "github target URL must be a repository_dispatch endpoint, e.g. https://api.github.com/repos/{owner}/{repo}/dispatches";
                break;
        }

        return null;
    }

    /// <summary>
    /// Resolves the stored signature header: Azure DevOps targets persist the effective value (so
    /// the API and UI show what will actually be sent), everything else stores null.
    /// </summary>
    private static string? NormalizeSignatureHeader(string targetType, string? signatureHeader)
        => targetType != WebhookTargetTypes.AzureDevOps
            ? null
            : string.IsNullOrWhiteSpace(signatureHeader)
                ? WebhookRequestBuilder.DefaultAzureDevOpsSignatureHeader
                : signatureHeader.Trim();

    private static string? NormalizeGitHubEventType(string targetType, string? gitHubEventType)
        => targetType != WebhookTargetTypes.GitHub || string.IsNullOrWhiteSpace(gitHubEventType)
            ? null
            : gitHubEventType.Trim();

    private static async Task<IResult> CreateSubscription(
        PlatformDbContext db,
        IDataProtectionProvider dataProtection,
        CreateWebhookRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Url))
            return Results.BadRequest(new { error = "Name and URL are required" });
        if (request.Events is null || request.Events.Length == 0)
            return Results.BadRequest(new { error = "At least one event type is required" });

        var targetType = string.IsNullOrWhiteSpace(request.TargetType)
            ? WebhookTargetTypes.Generic
            : request.TargetType.Trim();

        var error = ValidateTarget(
            targetType, request.Url, request.Secret, request.SignatureHeader, request.GitHubEventType,
            requireSecret: true);
        if (error is not null) return Results.BadRequest(new { error });

        // Generic keeps generating its own secret, as it always has. The other targets must reuse
        // the credential the receiving system already holds, so it comes from the caller.
        var isGeneric = targetType == WebhookTargetTypes.Generic;
        var rawSecret = isGeneric ? GenerateSecret() : request.Secret!.Trim();
        var protector = dataProtection.CreateProtector("WebhookSecrets");

        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Url = request.Url,
            EncryptedSecret = protector.Protect(rawSecret),
            EventsJson = JsonSerializer.Serialize(request.Events),
            FilterProduct = request.Filters?.Product,
            FilterEnvironment = request.Filters?.Environment,
            TargetType = targetType,
            SignatureHeader = NormalizeSignatureHeader(targetType, request.SignatureHeader),
            GitHubEventType = NormalizeGitHubEventType(targetType, request.GitHubEventType),
            Active = true,
        };

        db.WebhookSubscriptions.Add(sub);
        await db.SaveChangesAsync();

        return Results.Created($"/api/webhooks/{sub.Id}", new
        {
            sub.Id,
            sub.Name,
            sub.Url,
            // Shown only once, and only when we minted it — the caller already has the others.
            secret = isGeneric ? rawSecret : null,
            events = request.Events,
            filters = new { product = sub.FilterProduct, environment = sub.FilterEnvironment },
            targetType = sub.TargetType,
            signatureHeader = sub.SignatureHeader,
            githubEventType = sub.GitHubEventType,
            sub.Active,
            sub.CreatedAt,
        });
    }

    private static async Task<IResult> ListSubscriptions(PlatformDbContext db)
    {
        var subs = await db.WebhookSubscriptions
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Url,
                events = s.EventsJson,
                filters = new { product = s.FilterProduct, environment = s.FilterEnvironment },
                targetType = s.TargetType,
                signatureHeader = s.SignatureHeader,
                githubEventType = s.GitHubEventType,
                s.Active,
                s.CreatedAt,
                s.UpdatedAt,
                deliveryStats = new
                {
                    total = s.Deliveries.Count,
                    delivered = s.Deliveries.Count(d => d.Status == "delivered"),
                    failed = s.Deliveries.Count(d => d.Status == "failed"),
                    pending = s.Deliveries.Count(d => d.Status == "pending"),
                    lastDeliveryAt = s.Deliveries
                        .Where(d => d.DeliveredAt != null)
                        .OrderByDescending(d => d.DeliveredAt)
                        .Select(d => (DateTimeOffset?)d.DeliveredAt)
                        .FirstOrDefault(),
                    lastStatus = s.Deliveries
                        .OrderByDescending(d => d.CreatedAt)
                        .Select(d => d.Status)
                        .FirstOrDefault(),
                },
            })
            .ToListAsync();

        // Parse events JSON for cleaner output
        var result = subs.Select(s => new
        {
            s.Id,
            s.Name,
            s.Url,
            events = JsonSerializer.Deserialize<string[]>(s.events) ?? [],
            s.filters,
            s.targetType,
            s.signatureHeader,
            s.githubEventType,
            s.Active,
            s.CreatedAt,
            s.UpdatedAt,
            s.deliveryStats,
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> GetSubscription(PlatformDbContext db, Guid id)
    {
        var sub = await db.WebhookSubscriptions
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sub is null) return Results.NotFound();

        var recentDeliveries = await db.WebhookDeliveries
            .Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Take(50)
            .Select(d => new
            {
                d.Id,
                d.EventType,
                d.Status,
                d.Attempts,
                d.HttpStatus,
                d.ResponseBody,
                d.ErrorMessage,
                d.CreatedAt,
                d.DeliveredAt,
                d.NextRetryAt,
            })
            .ToListAsync();

        return Results.Ok(new
        {
            sub.Id,
            sub.Name,
            sub.Url,
            events = JsonSerializer.Deserialize<string[]>(sub.EventsJson) ?? [],
            filters = new { product = sub.FilterProduct, environment = sub.FilterEnvironment },
            targetType = sub.TargetType,
            signatureHeader = sub.SignatureHeader,
            githubEventType = sub.GitHubEventType,
            sub.Active,
            sub.CreatedAt,
            sub.UpdatedAt,
            recentDeliveries,
        });
    }

    private static async Task<IResult> UpdateSubscription(
        PlatformDbContext db, IDataProtectionProvider dataProtection, Guid id, UpdateWebhookRequest request)
    {
        var sub = await db.WebhookSubscriptions.FindAsync(id);
        if (sub is null) return Results.NotFound();

        // Switching target type would silently invalidate the stored credential — an auto-generated
        // whsec_ is not a GitHub token — and nothing downstream could detect that. Recreate instead.
        if (request.TargetType is not null && request.TargetType.Trim() != sub.TargetType)
            return Results.BadRequest(new
            {
                error = "targetType cannot be changed after creation — delete the subscription and create a new one",
            });

        // Validate the merged state, not just what was sent: a URL change alone can invalidate a
        // GitHub target. An omitted secret means "keep the stored one", so it is not required here.
        var error = ValidateTarget(
            sub.TargetType,
            request.Url ?? sub.Url,
            request.Secret,
            request.SignatureHeader ?? sub.SignatureHeader,
            request.GitHubEventType ?? sub.GitHubEventType,
            requireSecret: false);
        if (error is not null) return Results.BadRequest(new { error });

        if (request.Name is not null) sub.Name = request.Name;
        if (request.Url is not null) sub.Url = request.Url;
        if (request.Events is not null) sub.EventsJson = JsonSerializer.Serialize(request.Events);
        if (request.Filters is not null)
        {
            sub.FilterProduct = request.Filters.Product;
            sub.FilterEnvironment = request.Filters.Environment;
        }
        if (request.SignatureHeader is not null)
            sub.SignatureHeader = NormalizeSignatureHeader(sub.TargetType, request.SignatureHeader);
        if (request.GitHubEventType is not null)
            sub.GitHubEventType = NormalizeGitHubEventType(sub.TargetType, request.GitHubEventType);
        // Rotation: GitHub tokens expire and an Azure DevOps connection secret can be re-rolled.
        // A blank value is "leave it alone", never "wipe the credential".
        if (!string.IsNullOrWhiteSpace(request.Secret))
            sub.EncryptedSecret = dataProtection.CreateProtector("WebhookSecrets").Protect(request.Secret.Trim());
        if (request.Active.HasValue) sub.Active = request.Active.Value;
        sub.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            sub.Id,
            sub.Name,
            sub.Url,
            events = JsonSerializer.Deserialize<string[]>(sub.EventsJson) ?? [],
            filters = new { product = sub.FilterProduct, environment = sub.FilterEnvironment },
            targetType = sub.TargetType,
            signatureHeader = sub.SignatureHeader,
            githubEventType = sub.GitHubEventType,
            sub.Active,
            sub.UpdatedAt,
        });
    }

    private static async Task<IResult> DeleteSubscription(PlatformDbContext db, Guid id)
    {
        var sub = await db.WebhookSubscriptions
            .Include(s => s.Deliveries)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return Results.NotFound();

        db.WebhookDeliveries.RemoveRange(sub.Deliveries);
        db.WebhookSubscriptions.Remove(sub);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    private static async Task<IResult> GetDeliveries(
        PlatformDbContext db, Guid id, int? limit, int? offset)
    {
        var exists = await db.WebhookSubscriptions.AnyAsync(s => s.Id == id);
        if (!exists) return Results.NotFound();

        var query = db.WebhookDeliveries
            .Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip(offset ?? 0)
            .Take(limit ?? 50)
            .Select(d => new
            {
                d.Id,
                d.EventType,
                d.Status,
                d.Attempts,
                d.HttpStatus,
                d.ResponseBody,
                d.ErrorMessage,
                d.PayloadJson,
                d.CreatedAt,
                d.DeliveredAt,
                d.NextRetryAt,
            })
            .ToListAsync();

        return Results.Ok(new { items, total });
    }

    private static async Task<IResult> RetryDelivery(PlatformDbContext db, Guid id)
    {
        var delivery = await db.WebhookDeliveries.FindAsync(id);
        if (delivery is null) return Results.NotFound();
        if (delivery.Status != "failed")
            return Results.BadRequest(new { error = "Only failed deliveries can be retried" });

        delivery.Status = "pending";
        delivery.NextRetryAt = DateTimeOffset.UtcNow;
        delivery.Attempts = 0;
        delivery.ErrorMessage = null;
        await db.SaveChangesAsync();

        return Results.Ok(new { message = "Delivery queued for retry" });
    }

    private static async Task<IResult> TestSubscription(
        PlatformDbContext db, IWebhookDispatcher dispatcher, Guid id)
    {
        var sub = await db.WebhookSubscriptions.FindAsync(id);
        if (sub is null) return Results.NotFound();

        // Create a ping delivery directly for this subscription
        var deliveryId = Guid.NewGuid();
        var envelope = new
        {
            id = deliveryId,
            eventType = "ping",
            timestamp = DateTimeOffset.UtcNow,
            data = new { message = "Test webhook delivery", subscriptionId = id },
        };

        var delivery = new WebhookDelivery
        {
            Id = deliveryId,
            SubscriptionId = sub.Id,
            EventType = "ping",
            PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
            Status = "pending",
            NextRetryAt = DateTimeOffset.UtcNow,
        };

        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        return Results.Ok(new { message = "Test delivery queued", deliveryId });
    }

    private static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"whsec_{Convert.ToBase64String(bytes).TrimEnd('=')}";
    }
}

// ── Request DTOs ──

public record CreateWebhookRequest(
    string Name,
    string Url,
    string[] Events,
    WebhookFilterDto? Filters = null,
    // generic (default) | azure_devops | github
    string? TargetType = null,
    // Required for azure_devops and github; ignored for generic, which mints its own.
    string? Secret = null,
    // azure_devops only — defaults to X-Hub-Signature.
    string? SignatureHeader = null,
    // github only — defaults to the InfraPilot event type.
    string? GitHubEventType = null);

public record UpdateWebhookRequest(
    string? Name = null,
    string? Url = null,
    string[]? Events = null,
    WebhookFilterDto? Filters = null,
    bool? Active = null,
    // Rejected when it differs from the stored value — target type is immutable.
    string? TargetType = null,
    // Replaces the stored secret/token when non-blank; omit to keep the current one.
    string? Secret = null,
    string? SignatureHeader = null,
    string? GitHubEventType = null);

public record WebhookFilterDto(string? Product = null, string? Environment = null);
