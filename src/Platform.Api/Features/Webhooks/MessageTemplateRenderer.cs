using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandlebarsDotNet;
using Platform.Api.Features.Webhooks.Models;

namespace Platform.Api.Features.Webhooks;

/// <summary>
/// Turns a delivery envelope into the text a chat notification posts, by rendering the
/// subscription's Handlebars template (or the per-event default) against the envelope JSON.
/// <para>
/// Kept separate from <see cref="WebhookRequestBuilder"/> deliberately: the builder stays pure and
/// synchronous so per-platform body shapes are testable in isolation, while all the template
/// machinery — compilation, caching, failure handling — lives here behind one call.
/// </para>
/// </summary>
public class MessageTemplateRenderer
{
    /// <summary>
    /// Beyond this many distinct templates the cache is dropped wholesale rather than evicted
    /// cleverly. Real deployments have a handful of notification subscriptions; the cap exists so a
    /// preview endpoint hammered with one-off templates cannot grow the process without bound.
    /// </summary>
    private const int MaxCachedTemplates = 200;

    /// <summary>
    /// HTML escaping is off: the output is chat markdown, not a web page, and JSON serialisation of
    /// the body already handles quoting. Left on, an ampersand in a service name would reach the
    /// channel as <c>&amp;amp;</c>. Unresolved bindings render empty instead of throwing, so a
    /// payload that simply lacks an optional field still produces a message.
    /// </summary>
    private static readonly IHandlebars Engine = CreateEngine();

    private static readonly ConcurrentDictionary<string, HandlebarsTemplate<object, object>> Cache = new();

    private static IHandlebars CreateEngine()
    {
        var hb = Handlebars.Create();
        hb.Configuration.ThrowOnUnresolvedBindingExpression = false;
        hb.Configuration.NoEscape = true;
        return hb;
    }

    /// <summary>The heading and body a notification posts. <paramref name="Title"/> is empty when the message has none.</summary>
    public sealed record RenderedMessage(string Title, string Text);

    /// <summary>
    /// Renders the message for a queued delivery. Never throws: a template that fails at render time
    /// would otherwise strand the delivery in a retry loop that can only ever fail the same way, so
    /// the error becomes the message instead — visible in the channel and in the delivery history.
    /// </summary>
    public RenderedMessage Render(WebhookSubscription sub, WebhookDelivery delivery)
        => Render(sub.MessageTitle, sub.MessageTemplate, delivery.EventType, delivery.PayloadJson);

    /// <summary>
    /// The rendering rules, independent of any stored entity so the preview endpoint runs exactly
    /// what a real delivery would.
    /// <para>
    /// A blank <paramref name="bodyTemplate"/> means "use the default for this event" — an empty chat
    /// message is not a thing either platform accepts, so blank cannot mean empty. A blank
    /// <paramref name="titleTemplate"/> is the opposite: an explicitly empty string means "no
    /// heading", while null means "use the default". A heading is genuinely optional; a body is not.
    /// </para>
    /// </summary>
    public RenderedMessage Render(
        string? titleTemplate, string? bodyTemplate, string eventType, string payloadJson)
    {
        var defaults = NotificationTemplates.For(eventType);
        var context = BuildContext(payloadJson, eventType);

        var usingDefaultBody = string.IsNullOrWhiteSpace(bodyTemplate);
        var body = usingDefaultBody ? defaults.Body : bodyTemplate!;
        // Null and blank differ here, so IsNullOrWhiteSpace is the wrong test on purpose.
        var title = titleTemplate is null ? defaults.Title : titleTemplate;

        var renderedBody = RenderSafely(body, context).Trim();

        // Both platforms reject an empty body, which would cost five retries to discover. A custom
        // template can render to nothing for reasons the operator did not intend — an {{#each}} over
        // a field that turned out to be scalar, a conditional that never matched — so fall back to
        // the default rather than posting nothing, and to naming the event if that is empty too.
        if (renderedBody.Length == 0 && !usingDefaultBody)
            renderedBody = RenderSafely(defaults.Body, context).Trim();
        if (renderedBody.Length == 0)
            renderedBody = $"{eventType} (the configured message template rendered nothing)";

        return new RenderedMessage(RenderSafely(title, context).Trim(), renderedBody);
    }

    /// <summary>
    /// Compiles a template without rendering it, returning an error message or null when it is
    /// valid. Used at create/update time so a broken template is rejected while the operator is
    /// looking at it, rather than surfacing later as a run of failed deliveries.
    /// </summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        try
        {
            Compile(template);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string RenderSafely(string template, object context)
    {
        if (string.IsNullOrEmpty(template)) return "";
        try
        {
            return Compile(template)(context);
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: a channel showing this is how an operator learns the
            // template is broken, and it beats both a silent skip and an endless retry.
            return $"[InfraPilot] template error: {ex.Message}";
        }
    }

    private static HandlebarsTemplate<object, object> Compile(string template)
    {
        if (Cache.TryGetValue(template, out var cached)) return cached;
        var compiled = Engine.Compile(template);
        if (Cache.Count >= MaxCachedTemplates) Cache.Clear();
        Cache[template] = compiled;
        return compiled;
    }

    /// <summary>
    /// Converts the stored envelope into the plain dictionary/list/scalar tree Handlebars resolves
    /// against, so templates address fields exactly as they appear in the delivery payload
    /// (<c>{{data.product}}</c>). A malformed envelope yields a context carrying only the event type
    /// rather than an exception — the default templates then render with blanks, which is still a
    /// deliverable message.
    /// <para>
    /// <c>eventType</c> is guaranteed present. Every real envelope carries it, but a test delivery,
    /// a hand-written payload or a truncated row need not — and templates are told it is always
    /// available, so it is filled in from the delivery rather than left to the payload.
    /// </para>
    /// </summary>
    private static object BuildContext(string payloadJson, string eventType)
    {
        Dictionary<string, object?> context;
        try
        {
            context = ToPlain(JsonNode.Parse(payloadJson)) as Dictionary<string, object?>
                      ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            context = new Dictionary<string, object?>();
        }

        if (context.GetValueOrDefault("eventType") is not string { Length: > 0 })
            context["eventType"] = eventType;

        return context;
    }

    private static object? ToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(p => p.Key, p => ToPlain(p.Value)),
        JsonArray arr => arr.Select(ToPlain).ToList(),
        JsonValue value => Scalar(value),
        _ => node.ToString(),
    };

    private static object? Scalar(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b)) return b;
        // Integers before doubles: a version count rendering as "3" rather than "3.0" matters in a
        // message a human reads.
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<string>(out var s)) return s;
        return value.ToString();
    }
}
