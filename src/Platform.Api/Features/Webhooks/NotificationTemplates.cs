namespace Platform.Api.Features.Webhooks;

/// <summary>
/// The message a chat notification posts when the subscription has no template of its own, plus the
/// sample envelopes the template editor previews against.
/// <para>
/// Defaults exist because the useful thing to say about an event is knowable from the event: a
/// release note already arrives rendered, so its default just forwards <c>data.renderedContent</c>,
/// and a deployment reads as one line. An operator only writes a template when the default is not
/// the message they want — not to get a working notification in the first place.
/// </para>
/// Templates are Handlebars over the delivery envelope: <c>{{eventType}}</c>, <c>{{id}}</c>,
/// <c>{{timestamp}}</c> and the event's own fields under <c>{{data.*}}</c>, camelCase throughout
/// because that is how the dispatcher serialises the envelope.
/// </summary>
public static class NotificationTemplates
{
    public sealed record DefaultTemplate(string Title, string Body);

    /// <summary>
    /// Resolved longest-prefix-first, so <c>promotion.ticket.approved</c> never falls into the
    /// broader <c>promotion.</c> family. Exact event names win over any prefix.
    /// </summary>
    private static readonly (string Match, DefaultTemplate Template)[] Defaults =
    [
        // ── release notes ───────────────────────────────────────────────────
        // Already rendered markdown — the whole point of the direct-to-Teams path is that this note
        // reaches the channel without a function app in between reformatting it.
        ("release_note.generated", new DefaultTemplate(
            "Release notes — {{data.product}} / {{data.environment}}",
            "{{data.renderedContent}}")),

        // ── deployments ─────────────────────────────────────────────────────
        ("deployment.created", new DefaultTemplate(
            "{{data.product}}/{{data.service}} → {{data.environment}}",
            """
            **{{data.service}}** `{{data.version}}` deployed to **{{data.environment}}**{{#if data.previousVersion}} (was `{{data.previousVersion}}`){{/if}}
            Status: {{data.status}}{{#if data.isRollback}} · rollback{{/if}}{{#if data.failureReason}}
            Failure: {{data.failureReason}}{{/if}}{{#if data.runUrl}}
            [View run]({{data.runUrl}}){{/if}}
            """)),

        // ── promotions ──────────────────────────────────────────────────────
        // Ticket sign-offs first: they are a different shape from the candidate events below.
        ("promotion.ticket.", new DefaultTemplate(
            "{{eventType}} — {{data.workItemKey}}",
            """
            **{{data.workItemKey}}** · {{data.product}} → {{data.targetEnv}}
            {{eventType}} by {{data.approver}}{{#if data.comment}}
            > {{data.comment}}{{/if}}
            """)),
        ("promotion.", new DefaultTemplate(
            "{{data.product}}/{{data.service}} {{data.sourceEnv}} → {{data.targetEnv}}",
            """
            **{{data.service}}** `{{data.version}}` · {{data.sourceEnv}} → **{{data.targetEnv}}**
            {{eventType}} (status: {{data.status}}){{#if data.approvedBy}}
            Approved by:{{#each data.approvedBy}}
            - {{this.name}}{{#if this.reason}} (bypass: {{this.reason}}){{/if}}{{/each}}{{/if}}
            """)),

        // ── rollbacks ───────────────────────────────────────────────────────
        ("rollback.", new DefaultTemplate(
            "Rollback — {{data.product}} / {{data.targetEnv}}",
            """
            **{{data.product}}** rollback in **{{data.targetEnv}}** — {{eventType}} (status: {{data.status}}){{#if data.reason}}
            Reason: {{data.reason}}{{/if}}{{#if data.items}}
            {{#each data.items}}
            - {{this.service}}: `{{this.fromVersion}}` → `{{this.toVersion}}`{{/each}}{{/if}}
            """)),

        // ── approvals and requests ──────────────────────────────────────────
        ("approval.", new DefaultTemplate(
            "Approval — {{eventType}}",
            """
            Request `{{data.serviceRequestId}}` — {{eventType}}{{#if data.decidedBy}} by {{data.decidedBy}}{{/if}}
            Status: {{data.status}}{{#if data.comment}}
            > {{data.comment}}{{/if}}
            """)),
        ("request.status_changed", new DefaultTemplate(
            "Request {{data.newStatus}}",
            "Request `{{data.requestId}}`: {{data.previousStatus}} → **{{data.newStatus}}**{{#if data.actorName}} by {{data.actorName}}{{/if}}")),

        // ── test send ───────────────────────────────────────────────────────
        ("ping", new DefaultTemplate(
            "InfraPilot test notification",
            "This channel is wired up correctly — the notification was delivered by InfraPilot.")),
    ];

    /// <summary>
    /// Last resort for an event with no default of its own — a newly added event type reaching a
    /// subscription that predates it. It names the event and says what to do about it rather than
    /// posting an empty message, because a silent or blank notification looks like a broken channel.
    /// </summary>
    public static readonly DefaultTemplate Fallback = new(
        "{{eventType}}",
        """
        **{{eventType}}**
        _No message template is configured for this event. Set one on the notification to control what gets posted._
        """);

    public static DefaultTemplate For(string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return Fallback;

        // Longest match wins, so an exact event name beats the family prefix it sits under.
        DefaultTemplate? best = null;
        var bestLength = -1;
        foreach (var (match, template) in Defaults)
        {
            if (!eventType.StartsWith(match, StringComparison.Ordinal)) continue;
            if (match.Length <= bestLength) continue;
            best = template;
            bestLength = match.Length;
        }

        return best ?? Fallback;
    }

    // ── Preview samples ─────────────────────────────────────────────────────

    /// <summary>
    /// A representative envelope per event family, so the template editor can show what a message
    /// will actually look like before anything is dispatched. Shapes mirror the real dispatch sites;
    /// when one of those payloads gains a field, the sample here is what tells an operator it exists.
    /// </summary>
    public static string SampleEnvelope(string eventType)
    {
        var data = SampleData(eventType);
        return $$"""
        {"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","eventType":"{{eventType}}","timestamp":"2026-08-13T09:15:00+00:00","data":{{data}}}
        """;
    }

    private static string SampleData(string eventType) => eventType switch
    {
        var e when e.StartsWith("release_note.generated", StringComparison.Ordinal) => """
        {
          "id": "9c1b7e64-2f10-4a3d-9d2e-7ab0c5f31d22",
          "product": "billing-platform",
          "environment": "production",
          "from": "2026-08-06T09:00:00+00:00",
          "to": "2026-08-13T09:00:00+00:00",
          "generatedAt": "2026-08-13T09:15:00+00:00",
          "renderedContent": "## billing-platform — production\n\n- **api** 4.12.0 (was 4.11.3)\n- **worker** 2.8.1 (was 2.8.0)\n",
          "services": [
            { "service": "api", "previousVersion": "4.11.3", "currentVersion": "4.12.0", "isRollback": false },
            { "service": "worker", "previousVersion": "2.8.0", "currentVersion": "2.8.1", "isRollback": false }
          ]
        }
        """,

        "deployment.created" => """
        {
          "id": "5e2a1c90-8b44-4f19-a0d7-6c1e93f2b845",
          "product": "billing-platform",
          "service": "api",
          "environment": "production",
          "version": "4.12.0",
          "previousVersion": "4.11.3",
          "isRollback": false,
          "status": "succeeded",
          "source": "azure-devops",
          "deployedAt": "2026-08-13T09:14:20+00:00",
          "runUrl": "https://dev.azure.com/contoso/billing/_build/results?buildId=8421",
          "failureReason": null
        }
        """,

        var e when e.StartsWith("promotion.ticket.", StringComparison.Ordinal) => """
        {
          "workItemKey": "BILL-4417",
          "product": "billing-platform",
          "targetEnv": "production",
          "candidateId": "b7d3f0a2-1c48-4e6b-9f52-83ad10c4e7b9",
          "approver": "dana.reed@contoso.com",
          "comment": "Regression suite green on staging."
        }
        """,

        var e when e.StartsWith("promotion.", StringComparison.Ordinal) => """
        {
          "candidateId": "b7d3f0a2-1c48-4e6b-9f52-83ad10c4e7b9",
          "product": "billing-platform",
          "service": "api",
          "sourceEnv": "staging",
          "targetEnv": "production",
          "version": "4.12.0",
          "status": "Approved",
          "approvedAt": "2026-08-13T09:10:00+00:00",
          "approvedBy": [
            { "name": "Dana Reed", "email": "dana.reed@contoso.com", "via": "approval", "stepName": "QA sign-off", "reason": null }
          ],
          "references": [
            { "type": "workItem", "key": "BILL-4417", "title": "Invoice rounding fix" }
          ]
        }
        """,

        var e when e.StartsWith("rollback.", StringComparison.Ordinal) => """
        {
          "rollbackId": "c41f8b25-6a37-4de9-8f01-2b95ce7a4d13",
          "product": "billing-platform",
          "targetEnv": "production",
          "mode": "ReferenceEnvironment",
          "referenceEnv": "staging",
          "status": "Approved",
          "reason": "Invoice totals off by a cent for multi-currency accounts.",
          "approvedAt": "2026-08-13T09:12:00+00:00",
          "items": [
            { "service": "api", "fromVersion": "4.12.0", "toVersion": "4.11.3", "status": "Pending" }
          ]
        }
        """,

        var e when e.StartsWith("approval.", StringComparison.Ordinal) => """
        {
          "approvalId": "7d1e4a08-93bc-4f27-b6d5-0a48f21c9e64",
          "serviceRequestId": "1f8c05b3-72ae-4d91-8c36-59b7ea402d18",
          "decision": "Approved",
          "decidedBy": "Dana Reed",
          "comment": "Capacity confirmed with the platform team.",
          "status": "Approved"
        }
        """,

        "request.status_changed" => """
        {
          "requestId": "1f8c05b3-72ae-4d91-8c36-59b7ea402d18",
          "catalogItemId": "a2b46d19-5f83-4c07-9e21-6d8b0f37a9c5",
          "previousStatus": "Pending",
          "newStatus": "Approved",
          "actorName": "Dana Reed"
        }
        """,

        "ping" => """
        { "message": "Test webhook delivery", "subscriptionId": "e93a5c71-408d-4b62-9f17-2c05ad8be634" }
        """,

        // Unknown event: an empty object is the honest sample. It also shows the operator exactly
        // what the fallback template posts when nothing is known about the payload.
        _ => "{}",
    };
}
