using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Settings;
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
        group.MapPost("/preview-message", PreviewMessage);

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

    /// <summary>Maximum message template length, matching the column width.</summary>
    private const int MessageTemplateMaxLength = 8000;

    private const int MessageTitleMaxLength = 200;

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
        bool requireSecret,
        string? messageTemplate = null,
        string? messageTitle = null)
    {
        if (!WebhookTargetTypes.IsValid(targetType))
            return $"targetType must be one of: {string.Join(", ", WebhookTargetTypes.All)}";

        var hasSignatureHeader = !string.IsNullOrWhiteSpace(signatureHeader);
        var hasGitHubEventType = !string.IsNullOrWhiteSpace(gitHubEventType);
        var isMessaging = WebhookTargetTypes.IsMessaging(targetType);

        // A secret reaching an Authorization header must not be able to smuggle in a second header.
        if (secret is not null && secret.Any(char.IsControl))
            return "secret must not contain control characters";

        // Message templates belong to the chat targets. Accepting one elsewhere would store a
        // setting that silently never renders.
        if (!isMessaging)
        {
            if (!string.IsNullOrWhiteSpace(messageTemplate))
                return $"messageTemplate applies only to messaging targets: {string.Join(", ", WebhookTargetTypes.Messaging)}";
            if (!string.IsNullOrWhiteSpace(messageTitle))
                return $"messageTitle applies only to messaging targets: {string.Join(", ", WebhookTargetTypes.Messaging)}";
        }
        else
        {
            if (messageTemplate is { Length: > MessageTemplateMaxLength })
                return $"messageTemplate must be {MessageTemplateMaxLength} characters or fewer";
            if (messageTitle is { Length: > MessageTitleMaxLength })
                return $"messageTitle must be {MessageTitleMaxLength} characters or fewer";
            // Compiled now so a typo is a rejected form field rather than a run of failed deliveries.
            if (MessageTemplateRenderer.Validate(messageTemplate) is { } bodyError)
                return $"messageTemplate is not a valid template: {bodyError}";
            if (MessageTemplateRenderer.Validate(messageTitle) is { } titleError)
                return $"messageTitle is not a valid template: {titleError}";
        }

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

            case WebhookTargetTypes.MicrosoftTeams:
            case WebhookTargetTypes.MicrosoftTeamsHtml:
            case WebhookTargetTypes.Discord:
                if (hasSignatureHeader) return "signatureHeader applies only to azure_devops targets";
                if (hasGitHubEventType) return "githubEventType applies only to github targets";
                // Anyone holding the URL can post to the channel, so there is nothing to authenticate
                // with and nothing to rotate — accepting a secret here would only imply otherwise.
                if (!string.IsNullOrWhiteSpace(secret))
                    return $"secret does not apply to {targetType} targets — the webhook URL is itself the credential";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var chatUri) || chatUri.Scheme != Uri.UriSchemeHttps)
                    return $"{targetType} targets require an absolute https URL";
                if (targetType == WebhookTargetTypes.Discord && !LooksLikeDiscordWebhook(chatUri))
                    return "discord target URL must be a channel webhook, e.g. https://discord.com/api/webhooks/{id}/{token}";
                break;
        }

        return null;
    }

    /// <summary>
    /// Catches the common paste error — a Discord channel or invite link instead of a webhook URL —
    /// without locking the target to Discord's own hostnames, since a gateway or proxy in front of it
    /// still speaks the same body shape.
    /// </summary>
    private static bool LooksLikeDiscordWebhook(Uri uri)
        => uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// A blank body template stores as null, which means "use the per-event default" — an empty chat
    /// message is not something either platform accepts, so blank cannot mean empty here.
    /// </summary>
    private static string? NormalizeMessageTemplate(string targetType, string? messageTemplate)
        => !WebhookTargetTypes.IsMessaging(targetType) || string.IsNullOrWhiteSpace(messageTemplate)
            ? null
            : messageTemplate.Trim();

    /// <summary>
    /// Unlike the body, a blank title is preserved as an empty string: a heading is genuinely
    /// optional, so clearing the field has to mean "post without one" rather than silently
    /// reinstating the default. Only an omitted field (null) falls back to the default.
    /// </summary>
    private static string? NormalizeMessageTitle(string targetType, string? messageTitle)
        => !WebhookTargetTypes.IsMessaging(targetType) || messageTitle is null
            ? null
            : messageTitle.Trim();

    private static async Task<IResult> CreateSubscription(
        PlatformDbContext db,
        IDataProtectionProvider dataProtection,
        EnvironmentAliasResolver environments,
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
            requireSecret: true, request.MessageTemplate, request.MessageTitle);
        if (error is not null) return Results.BadRequest(new { error });

        // Generic keeps generating its own secret, as it always has. The signature/token targets must
        // reuse the credential the receiving system already holds, so it comes from the caller.
        // Messaging targets store nothing — the URL they post to is the whole credential.
        var isGeneric = targetType == WebhookTargetTypes.Generic;
        var isMessaging = WebhookTargetTypes.IsMessaging(targetType);
        var rawSecret = isGeneric ? GenerateSecret() : request.Secret?.Trim() ?? "";
        var protector = dataProtection.CreateProtector("WebhookSecrets");

        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Url = request.Url,
            EncryptedSecret = isMessaging ? "" : protector.Protect(rawSecret),
            EventsJson = JsonSerializer.Serialize(request.Events),
            FilterProduct = request.Filters?.Product,
            // Alias-resolved: the filter is matched against the environment stored on the event, so a
            // subscription written for "prod" has to hold the canonical key or it silently matches
            // nothing â€” a webhook that never fires is the hardest kind of misconfiguration to spot.
            FilterEnvironment = await environments.ResolveFilterAsync(request.Filters?.Environment),
            TargetType = targetType,
            SignatureHeader = NormalizeSignatureHeader(targetType, request.SignatureHeader),
            GitHubEventType = NormalizeGitHubEventType(targetType, request.GitHubEventType),
            MessageTemplate = NormalizeMessageTemplate(targetType, request.MessageTemplate),
            MessageTitle = NormalizeMessageTitle(targetType, request.MessageTitle),
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
            messageTemplate = sub.MessageTemplate,
            messageTitle = sub.MessageTitle,
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
                messageTemplate = s.MessageTemplate,
                messageTitle = s.MessageTitle,
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
            s.messageTemplate,
            s.messageTitle,
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
            messageTemplate = sub.MessageTemplate,
            messageTitle = sub.MessageTitle,
            sub.Active,
            sub.CreatedAt,
            sub.UpdatedAt,
            recentDeliveries,
        });
    }

    private static async Task<IResult> UpdateSubscription(
        PlatformDbContext db, IDataProtectionProvider dataProtection,
        EnvironmentAliasResolver environments, Guid id, UpdateWebhookRequest request)
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
            requireSecret: false,
            request.MessageTemplate ?? sub.MessageTemplate,
            request.MessageTitle ?? sub.MessageTitle);
        if (error is not null) return Results.BadRequest(new { error });

        if (request.Name is not null) sub.Name = request.Name;
        if (request.Url is not null) sub.Url = request.Url;
        if (request.Events is not null) sub.EventsJson = JsonSerializer.Serialize(request.Events);
        if (request.Filters is not null)
        {
            sub.FilterProduct = request.Filters.Product;
            sub.FilterEnvironment = await environments.ResolveFilterAsync(request.Filters.Environment);
        }
        if (request.SignatureHeader is not null)
            sub.SignatureHeader = NormalizeSignatureHeader(sub.TargetType, request.SignatureHeader);
        if (request.GitHubEventType is not null)
            sub.GitHubEventType = NormalizeGitHubEventType(sub.TargetType, request.GitHubEventType);
        if (request.MessageTemplate is not null)
            sub.MessageTemplate = NormalizeMessageTemplate(sub.TargetType, request.MessageTemplate);
        if (request.MessageTitle is not null)
            sub.MessageTitle = NormalizeMessageTitle(sub.TargetType, request.MessageTitle);
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
            messageTemplate = sub.MessageTemplate,
            messageTitle = sub.MessageTitle,
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

    /// <summary>
    /// Renders a message template against a representative payload for the chosen event, and frames
    /// it exactly as a real delivery would. Authoring a Handlebars template against a payload shape
    /// you cannot see is guesswork otherwise — and the alternative way to find out is to wait for a
    /// production event to land in a channel.
    /// </summary>
    private static IResult PreviewMessage(
        MessageTemplateRenderer renderer, PreviewMessageRequest request)
    {
        var targetType = string.IsNullOrWhiteSpace(request.TargetType)
            ? WebhookTargetTypes.MicrosoftTeams
            : request.TargetType.Trim();
        if (!WebhookTargetTypes.IsMessaging(targetType))
            return Results.BadRequest(new
            {
                error = $"targetType must be one of: {string.Join(", ", WebhookTargetTypes.Messaging)}",
            });

        var eventType = string.IsNullOrWhiteSpace(request.EventType) ? "ping" : request.EventType.Trim();

        if (MessageTemplateRenderer.Validate(request.MessageTemplate) is { } bodyError)
            return Results.BadRequest(new { error = $"messageTemplate is not a valid template: {bodyError}" });
        if (MessageTemplateRenderer.Validate(request.MessageTitle) is { } titleError)
            return Results.BadRequest(new { error = $"messageTitle is not a valid template: {titleError}" });

        var payload = NotificationTemplates.SampleEnvelope(eventType);
        var message = renderer.Render(request.MessageTitle, request.MessageTemplate, eventType, payload);

        // The URL matters for Teams, where it decides Adaptive Card versus legacy MessageCard, so the
        // preview is only faithful when the operator's own URL is the one being framed.
        var sub = new WebhookSubscription
        {
            TargetType = targetType,
            Url = request.Url ?? "",
        };
        var framed = WebhookRequestBuilder.Build(
            sub,
            new WebhookDelivery { EventType = eventType, PayloadJson = payload },
            secret: "",
            message);

        return Results.Ok(new
        {
            eventType,
            targetType,
            title = message.Title,
            text = message.Text,
            samplePayload = payload,
            requestBody = framed.Body,
            // The body is not always JSON — the Teams HTML target sends a fragment — so the preview
            // says which, rather than leaving the editor to infer it from the target type.
            contentType = framed.ContentType,
        });
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
    // generic (default) | azure_devops | github | msteams | msteams_html | discord
    string? TargetType = null,
    // Required for azure_devops and github; rejected for the messaging targets, whose URL is the
    // credential; ignored for generic, which mints its own.
    string? Secret = null,
    // azure_devops only — defaults to X-Hub-Signature.
    string? SignatureHeader = null,
    // github only — defaults to the InfraPilot event type.
    string? GitHubEventType = null,
    // Messaging targets only — blank falls back to the per-event default message.
    string? MessageTemplate = null,
    // Messaging targets only — omit for the per-event default heading, empty for no heading.
    string? MessageTitle = null);

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
    string? GitHubEventType = null,
    string? MessageTemplate = null,
    string? MessageTitle = null);

public record WebhookFilterDto(string? Product = null, string? Environment = null);

/// <summary>
/// A template render against a sample payload — nothing is stored and nothing is sent, so this is
/// safe to call on every keystroke in the editor.
/// </summary>
public record PreviewMessageRequest(
    string? TargetType = null,
    string? EventType = null,
    string? MessageTemplate = null,
    string? MessageTitle = null,
    string? Url = null);
