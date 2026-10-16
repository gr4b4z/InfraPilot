using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;

namespace Platform.Api.Tests.Features.Webhooks;

/// <summary>
/// What a chat notification actually says. Two properties matter more than any individual wording:
/// a delivery must always produce a message (a template that cannot render still has to yield
/// something postable, or the delivery retries forever to no effect), and the release-note default
/// must forward the already-rendered note untouched — that is the whole point of posting to Teams
/// directly instead of through a relay that reformats it on the way.
/// </summary>
public class NotificationMessageTests
{
    private readonly MessageTemplateRenderer _renderer = new();

    private static WebhookDelivery Delivery(string eventType, string payloadJson) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType,
        PayloadJson = payloadJson,
        Status = "pending",
    };

    private static WebhookSubscription Subscription(
        string? template = null, string? title = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "chat",
        Url = "https://discord.com/api/webhooks/1/t",
        TargetType = WebhookTargetTypes.Discord,
        MessageTemplate = template,
        MessageTitle = title,
    };

    // ── template resolution ─────────────────────────────────────────────────

    [Fact]
    public void ReleaseNoteDefault_ForwardsTheRenderedNoteVerbatim()
    {
        var payload = """
        {"id":"1","eventType":"release_note.generated","data":{"product":"billing","environment":"production","renderedContent":"## billing\n\n- **api** 4.12.0\n"}}
        """;

        var message = _renderer.Render(Subscription(), Delivery("release_note.generated", payload));

        Assert.Equal("## billing\n\n- **api** 4.12.0", message.Text);
        Assert.Equal("Release notes — billing / production", message.Title);
    }

    /// <summary>
    /// Markdown reaches the channel as written — the renderer must not HTML-escape it, or a note with
    /// an ampersand or a quote in a service name arrives full of entity references.
    /// </summary>
    [Fact]
    public void RenderedText_IsNotHtmlEscaped()
    {
        var payload = """
        {"data":{"renderedContent":"R&D \"api\" <v2> 'x'"}}
        """;

        var message = _renderer.Render(Subscription(), Delivery("release_note.generated", payload));

        Assert.Equal("R&D \"api\" <v2> 'x'", message.Text);
    }

    [Fact]
    public void ACustomTemplate_WinsOverTheDefault()
    {
        var payload = """{"data":{"product":"billing","service":"api","version":"4.12.0"}}""";

        var message = _renderer.Render(
            Subscription(template: "{{data.service}} is now {{data.version}}", title: "ship it"),
            Delivery("deployment.created", payload));

        Assert.Equal("api is now 4.12.0", message.Text);
        Assert.Equal("ship it", message.Title);
    }

    /// <summary>
    /// A blank body means "use the default", because neither platform accepts an empty message — but a
    /// blank title means "no heading", because a heading is genuinely optional. Only an omitted title
    /// (null) falls back.
    /// </summary>
    [Fact]
    public void BlankTitle_MeansNoHeading_WhileBlankBodyFallsBackToTheDefault()
    {
        var payload = """{"data":{"product":"billing","service":"api","version":"4.12.0","status":"succeeded"}}""";

        var message = _renderer.Render(
            Subscription(template: "   ", title: ""), Delivery("deployment.created", payload));

        Assert.Equal("", message.Title);
        Assert.Contains("api", message.Text);
        Assert.NotEqual("", message.Text);
    }

    /// <summary>
    /// A newly added event reaching a subscription that predates it. The event type comes from the
    /// delivery rather than the payload, so <c>{{eventType}}</c> resolves even for a payload that
    /// does not carry it — templates are told it is always available.
    /// </summary>
    [Fact]
    public void AnUnknownEvent_FallsBackToATemplateThatNamesTheEvent()
    {
        var message = _renderer.Render(Subscription(), Delivery("widget.exploded", "{}"));

        Assert.Contains("widget.exploded", message.Text);
        Assert.Equal("widget.exploded", message.Title);
    }

    [Fact]
    public void TicketEvents_ResolveToTheirOwnDefault_NotTheBroaderPromotionFamily()
    {
        var ticket = NotificationTemplates.For("promotion.ticket.approved");
        var promotion = NotificationTemplates.For("promotion.approved");

        Assert.NotEqual(promotion.Body, ticket.Body);
        Assert.Contains("workItemKey", ticket.Body);
    }

    // ── conditionals, loops and missing data ────────────────────────────────

    [Fact]
    public void OptionalFields_RenderTheirBlockOnlyWhenPresent()
    {
        const string template = "{{data.service}}{{#if data.runUrl}} [run]({{data.runUrl}}){{/if}}";

        var withUrl = _renderer.Render(
            Subscription(template: template),
            Delivery("deployment.created", """{"data":{"service":"api","runUrl":"https://ci.test/1"}}"""));
        var withoutUrl = _renderer.Render(
            Subscription(template: template),
            Delivery("deployment.created", """{"data":{"service":"api"}}"""));

        Assert.Equal("api [run](https://ci.test/1)", withUrl.Text);
        Assert.Equal("api", withoutUrl.Text);
    }

    [Fact]
    public void Collections_RenderThroughEach()
    {
        var payload = """
        {"data":{"items":[{"service":"api","fromVersion":"4.12.0","toVersion":"4.11.3"},{"service":"worker","fromVersion":"2.8.1","toVersion":"2.8.0"}]}}
        """;

        var message = _renderer.Render(
            Subscription(template: "{{#each data.items}}{{this.service}}:{{this.toVersion}} {{/each}}"),
            Delivery("rollback.approved", payload));

        Assert.Equal("api:4.11.3 worker:2.8.0", message.Text);
    }

    /// <summary>
    /// Integers must not acquire a decimal point on the way into a sentence a person reads.
    /// </summary>
    [Fact]
    public void WholeNumbers_RenderWithoutADecimalPoint()
    {
        var message = _renderer.Render(
            Subscription(template: "{{data.servicesCount}} services"),
            Delivery("release_note.generated", """{"data":{"servicesCount":3}}"""));

        Assert.Equal("3 services", message.Text);
    }

    [Fact]
    public void AMissingField_RendersEmptyRatherThanFailing()
    {
        var message = _renderer.Render(
            Subscription(template: "[{{data.nope.deeper}}]"),
            Delivery("deployment.created", """{"data":{}}"""));

        Assert.Equal("[]", message.Text);
    }

    /// <summary>
    /// A malformed envelope should still produce a postable message. Silently dropping the delivery
    /// would hide the problem, and throwing would retry it five times to the same end.
    /// </summary>
    [Fact]
    public void AMalformedEnvelope_StillProducesAMessage()
    {
        var message = _renderer.Render(Subscription(), Delivery("deployment.created", "not json"));

        Assert.NotEqual("", message.Text);
    }

    /// <summary>
    /// A template can compile and still render to nothing — an <c>{{#each}}</c> over a field that
    /// turned out to be scalar iterates zero times without complaint. Both platforms reject an empty
    /// body, so that must not reach the wire: the default takes over instead.
    /// </summary>
    [Fact]
    public void ATemplateRenderingToNothing_FallsBackRatherThanPostingAnEmptyMessage()
    {
        var message = _renderer.Render(
            Subscription(template: "{{#each data.product}}{{this.x}}{{/each}}"),
            Delivery("deployment.created", """{"data":{"product":"billing","service":"api","version":"4.12.0","status":"succeeded"}}"""));

        Assert.NotEqual("", message.Text);
        Assert.Contains("api", message.Text);
    }

    /// <summary>
    /// The last resort when even the default renders empty — an unknown event whose payload carries
    /// nothing. Naming the event beats posting a blank message or dropping the delivery.
    /// </summary>
    [Fact]
    public void AnEmptyRenderOfAnUnknownEvent_StillNamesTheEvent()
    {
        var message = _renderer.Render(
            Subscription(template: "{{data.nothing.here}}"),
            Delivery("widget.exploded", "{}"));

        Assert.Contains("widget.exploded", message.Text);
    }

    // ── validation ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_AcceptsAWellFormedTemplate()
        => Assert.Null(MessageTemplateRenderer.Validate("{{#if data.x}}{{data.x}}{{/if}}"));

    [Fact]
    public void Validate_RejectsAnUnclosedBlock()
        => Assert.NotNull(MessageTemplateRenderer.Validate("{{#if data.x}}unclosed"));

    [Fact]
    public void Validate_TreatsBlankAsValid_SinceItMeansUseTheDefault()
    {
        Assert.Null(MessageTemplateRenderer.Validate(null));
        Assert.Null(MessageTemplateRenderer.Validate("  "));
    }

    // ── preview samples ─────────────────────────────────────────────────────

    /// <summary>
    /// The editor's preview is only worth trusting if every default renders something against its own
    /// sample — that pairing is what an operator reads before saving a template.
    /// </summary>
    [Theory]
    [InlineData("release_note.generated")]
    [InlineData("release_note.generated.html")]
    [InlineData("deployment.created")]
    [InlineData("promotion.approved")]
    [InlineData("promotion.ticket.approved")]
    [InlineData("rollback.approved")]
    [InlineData("approval.approved")]
    [InlineData("request.status_changed")]
    [InlineData("ping")]
    public void EveryDefaultRendersNonEmptyAgainstItsSample(string eventType)
    {
        var message = _renderer.Render(
            titleTemplate: null,
            bodyTemplate: null,
            eventType,
            NotificationTemplates.SampleEnvelope(eventType));

        Assert.NotEqual("", message.Text);
        Assert.DoesNotContain("template error", message.Text);
        // Handlebars leaves unresolved expressions empty, so a stray brace means a broken default.
        Assert.DoesNotContain("{{", message.Text);
    }
}
