using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// The state matrix's read path — <c>GET /api/deployments/state</c>, the request the product page
/// repaints on every deploy event and that pipelines and scripts read "what runs where" from. Pins
/// what its callers rely on while the work behind it is kept cheap:
/// <list type="bullet">
///   <item>The default response, byte for byte, over history with repeated deploys, a failed and an
///         in-flight deploy on top, a rollback, <c>deployedAt</c> ties, a retired service and a
///         participant override — external readers consume the whole shape.</item>
///   <item>Which event is "current": the newest per (product, service, environment), whatever became
///         of it, chosen exactly as the previous GroupBy/ROW_NUMBER query chose it.</item>
///   <item>The opt-in <c>view=summary</c>: the same rows minus exactly the nested fields no web view
///         that reads the matrix uses.</item>
///   <item>The product / environment / service filters, in both views.</item>
/// </list>
/// Each test seeds its own product straight into the database, so the shared fixture's rows never
/// meet.
/// </summary>
public class DeploymentStateReadPathTests : IClassFixture<DeploymentStateReadPathTests.StateFactory>, IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions StoreJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>How the API escapes what it writes — needed to turn the golden back into its bytes.</summary>
    private static readonly JsonSerializerOptions ServerJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A state row's fields in the default (full) shape, in response order.</summary>
    private static readonly string[] FullFields =
    {
        "id", "product", "service", "environment", "version", "previousVersion", "isRollback", "status",
        "source", "deployedAt", "references", "participants", "enrichment", "run",
    };

    /// <summary>What <c>view=summary</c> leaves out of each row — and nothing else.</summary>
    private static readonly string[] SummaryDroppedFields = { "references", "participants", "enrichment", "run" };

    private readonly StateFactory _factory;
    private readonly HttpClient _adminClient;

    public DeploymentStateReadPathTests(StateFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose() => _adminClient.Dispose();

    // ── Default response ────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultState_IsByteIdenticalToThePreviousImplementation()
    {
        // Captured from the previous implementation (the GroupBy(...).First() query) over this exact
        // fixture. Compared as bytes, after compacting the golden the way the server writes JSON.
        await SeedHistoryAsync(GoldenProduct, idPrefix: 0x5a7e0001);

        var body = await BodyAsync($"/api/deployments/state?product={GoldenProduct}");

        Assert.Equal(JsonNode.Parse(Golden)!.ToJsonString(ServerJson), body);
    }

    [Fact]
    public async Task DefaultState_IsTheNewestEventPerCell_WhateverBecameOfIt()
    {
        var product = NewProduct();
        await SeedHistoryAsync(product, NewPrefix());

        var rows = JsonNode.Parse(await BodyAsync($"/api/deployments/state?product={product}"))!.AsArray();

        Assert.Equal(
            new[]
            {
                // (service, environment, version, status, isRollback) in response order.
                ("api", "dev", "v1.2", "failed", false),          // a failed deploy is still what was last tried
                ("api", "prod", "v1.0", "succeeded", true),       // the rollback is the newest event
                ("api", "staging", "v1.1", "in_progress", false), // so is a deploy still running
                ("web", "dev", "v2.0", "succeeded", false),       // a tie: index order, i.e. stored first here
                ("web", "prod", "v2.0", "failed", false),         // a tie again; the failure was stored first
                ("worker", "staging", "v3.0", "succeeded", false),
                // "legacy" is retired, and the "-x" product's rows are another product's.
            },
            rows.Select(r => (
                (string)r!["service"]!, (string)r["environment"]!, (string)r["version"]!,
                (string)r["status"]!, (bool)r["isRollback"]!)));
        Assert.Equal(FullFields, rows[0]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task DefaultState_PicksTheSameRowsAsTheGroupByQuery()
    {
        // The previous implementation, verbatim, as the oracle: newest DeployedAt per group, ties left
        // to the index order. Run over a larger generated history full of ties and statuses, for every
        // filter shape the endpoint takes.
        var product = NewProduct();
        var prefix = NewPrefix();
        var rng = new Random(7);
        var n = 0;
        var rows = new List<DeployEvent>();
        foreach (var service in new[] { "alpha", "beta", "gamma", "delta" })
        foreach (var env in new[] { "dev", "staging", "prod" })
        {
            for (var i = rng.Next(1, 7); i > 0; i--)
            {
                rows.Add(Event(product, prefix, ++n, service, env, $"v{rng.Next(1, 5)}.{i}",
                    rng.Next(3) switch { 0 => "succeeded", 1 => "failed", _ => "in_progress" },
                    Day(rng.Next(3)), isRollback: rng.Next(5) == 0));
            }
        }
        await SeedAsync(rows.ToArray());

        foreach (var (env, service) in new (string?, string?)[]
        {
            (null, null), ("prod", null), (null, "beta"), ("dev", "gamma"),
        })
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var query = db.DeployEvents.AsNoTracking().ExcludingDeletedServices(db).Where(e => e.Product == product);
            if (env is not null) query = query.Where(e => e.Environment == env);
            if (service is not null) query = query.Where(e => e.Service == service);
            var expected = (await query
                    .GroupBy(e => new { e.Product, e.Service, e.Environment })
                    .Select(g => g.OrderByDescending(e => e.DeployedAt).First())
                    .ToListAsync())
                .Select(e => e.Id.ToString())
                .ToList();

            var url = $"/api/deployments/state?product={product}"
                + (env is null ? "" : $"&environment={env}")
                + (service is null ? "" : $"&serviceName={service}");
            var actual = JsonNode.Parse(await BodyAsync(url))!.AsArray().Select(r => (string)r!["id"]!).ToList();

            Assert.True(expected.Count > 0, $"{url}: fixture produced no rows");
            Assert.Equal(expected, actual);
        }
    }

    // ── view ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullView_IsExactlyTheDefault()
    {
        var product = NewProduct();
        await SeedHistoryAsync(product, NewPrefix());

        var byDefault = await BodyAsync($"/api/deployments/state?product={product}");
        var full = await BodyAsync($"/api/deployments/state?product={product}&view=full");
        var upper = await BodyAsync($"/api/deployments/state?product={product}&view=FULL");

        Assert.Equal(byDefault, full);
        Assert.Equal(byDefault, upper);
    }

    [Fact]
    public async Task SummaryView_DropsOnlyTheNestedFields()
    {
        var product = NewProduct();
        await SeedHistoryAsync(product, NewPrefix());

        var full = JsonNode.Parse(await BodyAsync($"/api/deployments/state?product={product}"))!;
        var summary = JsonNode.Parse(await BodyAsync($"/api/deployments/state?product={product}&view=summary"))!;

        Assert.Equal(6, summary.AsArray().Count);
        foreach (var row in summary.AsArray())
            Assert.Equal(FullFields.Except(SummaryDroppedFields), row!.AsObject().Select(p => p.Key));

        // Everything else — every row, in order, every kept field and value — is the default
        // response's, once the dropped fields are taken out of it.
        foreach (var row in full.AsArray())
        {
            foreach (var field in SummaryDroppedFields) row!.AsObject().Remove(field);
        }
        Assert.True(JsonNode.DeepEquals(full, summary), $"full (trimmed): {full}\nsummary: {summary}");
    }

    [Fact]
    public async Task UnknownView_IsRejected()
    {
        var response = await _adminClient.GetAsync("/api/deployments/state?view=compact");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'view' must be one of", await response.Content.ReadAsStringAsync());
    }

    // ── Filters ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filters_NarrowToTheSameRows_InBothViews()
    {
        var product = NewProduct();
        await SeedHistoryAsync(product, NewPrefix());

        foreach (var view in new[] { "full", "summary" })
        {
            var all = JsonNode.Parse(await BodyAsync($"/api/deployments/state?product={product}&view={view}"))!.AsArray();

            foreach (var (env, service) in new (string?, string?)[]
            {
                ("dev", null), ("staging", null), ("prod", null),
                (null, "api"), (null, "worker"), ("prod", "web"), ("staging", "web"),
                // Retired stays retired under a filter that names it.
                (null, "legacy"),
            })
            {
                var url = $"/api/deployments/state?product={product}&view={view}"
                    + (env is null ? "" : $"&environment={env}")
                    + (service is null ? "" : $"&serviceName={service}");
                var expected = new JsonArray(all
                    .Where(r => (env is null || (string)r!["environment"]! == env)
                             && (service is null || (string)r!["service"]! == service))
                    .Select(r => r!.DeepClone())
                    .ToArray());

                var actual = JsonNode.Parse(await BodyAsync(url))!;

                Assert.True(JsonNode.DeepEquals(expected, actual), $"{url}\nexpected: {expected}\nactual: {actual}");
            }
        }
    }

    [Fact]
    public async Task Filters_WithoutAProduct_SpanProducts()
    {
        // Environment and service filters on their own reach across products — what a script asking
        // "what runs in prod" reads. A unique environment/service keeps other tests' rows out.
        var first = NewProduct();
        var second = NewProduct();
        var env = $"env-{Guid.NewGuid():N}"[..12];
        var svc = $"svc-{Guid.NewGuid():N}"[..12];
        var prefix = NewPrefix();
        await SeedAsync(
            Event(first, prefix, 1, svc, env, "v1", "succeeded", Day(1)),
            Event(first, prefix, 2, svc, env, "v2", "succeeded", Day(2)),
            Event(second, prefix, 3, svc, env, "v7", "failed", Day(3)),
            Event(second, prefix, 4, svc, "other-env", "v8", "succeeded", Day(4)));

        foreach (var view in new[] { "full", "summary" })
        {
            var byEnv = JsonNode.Parse(await BodyAsync($"/api/deployments/state?environment={env}&view={view}"))!.AsArray();
            Assert.Equal(
                new[] { (first, "v2"), (second, "v7") }.OrderBy(x => x.Item1, StringComparer.Ordinal),
                byEnv.Select(r => ((string)r!["product"]!, (string)r["version"]!)));

            var byService = JsonNode.Parse(await BodyAsync($"/api/deployments/state?serviceName={svc}&view={view}"))!.AsArray();
            Assert.Equal(
                new[] { (first, env, "v2"), (second, env, "v7"), (second, "other-env", "v8") }
                    .OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2, StringComparer.Ordinal),
                byService.Select(r => ((string)r!["product"]!, (string)r["environment"]!, (string)r["version"]!)));
        }
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private const string GoldenProduct = "dsr-golden";

    /// <summary>
    /// Six live cells over fifteen events. Stored one at a time, in this order, the way pipelines post
    /// them — insertion order is what settles a <c>deployedAt</c> tie.
    /// </summary>
    private async Task SeedHistoryAsync(string product, uint idPrefix)
    {
        var p = idPrefix;
        await SeedAsync(
            // api/dev: two good deploys, then a failure on top.
            Event(product, p, 1, "api", "dev", "v1.0", "succeeded", Day(1)),
            Event(product, p, 2, "api", "dev", "v1.1", "succeeded", Day(2), previousVersion: "v1.0"),
            Rich(Event(product, p, 3, "api", "dev", "v1.2", "failed", Day(3), previousVersion: "v1.1"), "api-dev",
                failureReason: "pod api-7d9 cannot start (container waiting reason=ErrImagePull)"),
            // api/staging: an in-flight deploy on top.
            Event(product, p, 4, "api", "staging", "v1.0", "succeeded", Day(1)),
            Event(product, p, 5, "api", "staging", "v1.1", "in_progress", Day(4), previousVersion: "v1.0"),
            // api/prod: v1.1 rolled back to v1.0.
            Event(product, p, 6, "api", "prod", "v1.0", "succeeded", Day(1)),
            Rich(Event(product, p, 7, "api", "prod", "v1.1", "succeeded", Day(2), previousVersion: "v1.0"), "api-prod-old"),
            Event(product, p, 8, "api", "prod", "v1.0", "succeeded", Day(3), isRollback: true, previousVersion: "v1.1"),
            // web/dev: two versions reported for the same instant, and an older one.
            Rich(Event(product, p, 9, "web", "dev", "v2.0", "succeeded", Day(5)), "web-dev-a"),
            Rich(Event(product, p, 10, "web", "dev", "v2.1", "succeeded", Day(5)), "web-dev-b"),
            Event(product, p, 11, "web", "dev", "v1.9", "succeeded", Day(4)),
            // web/prod: a failed and a succeeded report of the same instant, the failure first.
            Event(product, p, 12, "web", "prod", "v2.0", "failed", Day(6), source: "github-actions"),
            Event(product, p, 13, "web", "prod", "v2.0", "succeeded", Day(6), source: "manual"),
            // worker/staging: one deploy carrying everything, plus an operator override.
            Rich(Event(product, p, 14, "worker", "staging", "v3.0", "succeeded", Day(2)), "worker"),
            // legacy/prod: newest of all, but the service is retired.
            Event(product, p, 15, "legacy", "prod", "v0.1", "succeeded", Day(7)),
            // Same services under another product never leak in.
            Event(product + "-x", p, 16, "api", "dev", "v9.9", "succeeded", Day(9)),
            Event(product + "-x", p, 17, "web", "prod", "v9.9", "succeeded", Day(9)));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.DeletedServices.Add(new DeletedService
        {
            Id = Id(p, 100),
            Product = product,
            Service = "legacy",
            DeletedAt = Day(8),
            DeletedById = "admin",
            DeletedByName = "Admin",
        });
        db.ReferenceParticipantOverrides.Add(new ReferenceParticipantOverride
        {
            Id = Id(p, 101),
            DeployEventId = Id(p, 14),
            ReferenceKey = "WK-14",
            Role = "qa-owner",
            AssigneeEmail = "olga@example.com",
            AssigneeDisplayName = "Olga Override",
            AssignedById = "admin",
            AssignedByName = "Admin",
            AssignedAt = Day(3),
        });
        await db.SaveChangesAsync();
    }

    private static DeployEvent Event(
        string product, uint prefix, int n, string service, string env, string version, string status,
        DateTimeOffset at, bool isRollback = false, string? previousVersion = null, string source = "ci") => new()
    {
        Id = Id(prefix, n),
        Product = product,
        Service = service,
        Environment = env,
        Version = version,
        PreviousVersion = previousVersion,
        IsRollback = isRollback,
        Status = status,
        Source = source,
        DeployedAt = at,
        CreatedAt = at.AddSeconds(30),
    };

    /// <summary>Fills every JSON column the way ingest stores them — every reference field included.</summary>
    private static DeployEvent Rich(DeployEvent e, string tag, string? failureReason = null)
    {
        var n = Convert.ToInt32(e.Id.ToString()[^12..], 16).ToString();
        e.ReferencesJson = JsonSerializer.Serialize(new List<ReferenceDto>
        {
            new(
                Type: "work-item",
                Url: $"https://jira.example.com/browse/WK-{n}",
                Provider: "jira",
                Key: $"WK-{n}",
                Title: $"Ticket for {tag}",
                Participants: new[] { new ParticipantDto("qa-owner", "Quinn QA", "quinn@example.com") },
                Commits: new[] { $"c0ffee{n}" },
                Content: $"The whole description of the {tag} ticket — long, with \"quotes\" & <markup>.",
                Resolution: new ReferenceResolutionDto(true, "Done", T0, new ResolutionActorDto("Rita", "rita@example.com")),
                OccurredAt: T0,
                Priority: "High",
                WorkItemType: "Bug"),
            new(
                Type: "pull-request",
                Url: $"https://github.com/acme/{tag}/pull/{n}",
                Provider: "github",
                Key: n,
                Revision: $"c0ffee{n}",
                Title: $"PR for {tag}",
                Participants: new[] { new ParticipantDto("pr-author", "Bob Builder", "bob@example.com") },
                Content: "PR body.",
                OccurredAt: T0.AddHours(1)),
            new(Type: "pipeline", Url: $"https://dev.azure.com/acme/_build/results?buildId={n}", Provider: "azure-devops", Key: n),
        }, StoreJson);
        e.ParticipantsJson = JsonSerializer.Serialize(new List<ParticipantDto>
        {
            new("triggered-by", "Tess Trigger", "tess@example.com"),
        }, StoreJson);
        e.EnrichmentJson = JsonSerializer.Serialize(new EnrichmentDto(
            new Dictionary<string, string> { ["workItemTitle"] = $"Ticket for {tag}", ["team"] = "platform" },
            new List<ParticipantDto> { new("assignee", "Ann Assignee", "ann@example.com") },
            T0.AddHours(2)), StoreJson);
        e.RunJson = JsonSerializer.Serialize(new DeployRun(
            Provider: "github-actions", RunId: $"9{n}", RunNumber: $"#{n}", Attempt: 1, WorkflowName: "Deploy",
            JobName: $"Deploy Helm ({tag})", RunUrl: $"https://github.com/acme/release/actions/runs/9{n}",
            JobUrl: $"https://github.com/acme/release/actions/runs/9{n}/job/1", TriggeredBy: "tess",
            StartedAt: e.DeployedAt.AddMinutes(-5), CompletedAt: e.DeployedAt, FailureReason: failureReason), StoreJson);
        e.MetadataJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["chart"] = tag }, StoreJson);
        return e;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"dsr-{Guid.NewGuid():N}"[..16];

    private static uint NewPrefix() => (uint)Random.Shared.NextInt64(0x10000000, 0xFFFFFFFF);

    private static Guid Id(uint prefix, int n) => Guid.Parse($"{prefix:x8}-0000-4000-8000-{n:x12}");

    private static DateTimeOffset Day(double days) => T0.AddDays(days);

    /// <summary>One <c>SaveChanges</c> per event: EF orders a batch by key, and the order matters here.</summary>
    private async Task SeedAsync(params DeployEvent[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        foreach (var e in events)
        {
            db.DeployEvents.Add(e);
            await db.SaveChangesAsync();
        }
    }

    private async Task<string> BodyAsync(string url)
    {
        var response = await _adminClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    // ── Golden ──────────────────────────────────────────────────────────────

    private const string Golden = """
        [
          {
            "id": "5a7e0001-0000-4000-8000-000000000003",
            "product": "dsr-golden",
            "service": "api",
            "environment": "dev",
            "version": "v1.2",
            "previousVersion": "v1.1",
            "isRollback": false,
            "status": "failed",
            "source": "ci",
            "deployedAt": "2026-01-04T00:00:00+00:00",
            "references": [
              {"type": "work-item", "url": "https://jira.example.com/browse/WK-3", "provider": "jira", "key": "WK-3", "revision": null, "title": "Ticket for api-dev", "subTitle": null, "participants": [{"role": "qa-owner", "displayName": "Quinn QA", "email": "quinn@example.com", "isOverride": false, "assignedBy": null}], "commits": ["c0ffee3"], "content": "The whole description of the api-dev ticket — long, with \"quotes\" & <markup>.", "resolution": {"resolved": true, "status": "Done", "at": "2026-01-01T00:00:00+00:00", "by": {"displayName": "Rita", "email": "rita@example.com"}}, "occurredAt": "2026-01-01T00:00:00+00:00", "priority": "High", "workItemType": "Bug"},
              {"type": "pull-request", "url": "https://github.com/acme/api-dev/pull/3", "provider": "github", "key": "3", "revision": "c0ffee3", "title": "PR for api-dev", "subTitle": null, "participants": [{"role": "pr-author", "displayName": "Bob Builder", "email": "bob@example.com", "isOverride": false, "assignedBy": null}], "commits": null, "content": "PR body.", "resolution": null, "occurredAt": "2026-01-01T01:00:00+00:00", "priority": null, "workItemType": null},
              {"type": "pipeline", "url": "https://dev.azure.com/acme/_build/results?buildId=3", "provider": "azure-devops", "key": "3", "revision": null, "title": null, "subTitle": null, "participants": null, "commits": null, "content": null, "resolution": null, "occurredAt": null, "priority": null, "workItemType": null}
            ],
            "participants": [{"role": "triggered-by", "displayName": "Tess Trigger", "email": "tess@example.com", "isOverride": false, "assignedBy": null}],
            "enrichment": {"labels": {"workItemTitle": "Ticket for api-dev", "team": "platform"}, "participants": [{"role": "assignee", "displayName": "Ann Assignee", "email": "ann@example.com", "isOverride": false, "assignedBy": null}], "enrichedAt": "2026-01-01T02:00:00+00:00"},
            "run": {"provider": "github-actions", "runId": "93", "runNumber": "#3", "attempt": 1, "workflowName": "Deploy", "jobName": "Deploy Helm (api-dev)", "runUrl": "https://github.com/acme/release/actions/runs/93", "jobUrl": "https://github.com/acme/release/actions/runs/93/job/1", "triggeredBy": "tess", "startedAt": "2026-01-03T23:55:00+00:00", "completedAt": "2026-01-04T00:00:00+00:00", "failureReason": "pod api-7d9 cannot start (container waiting reason=ErrImagePull)"}
          },
          {
            "id": "5a7e0001-0000-4000-8000-000000000008",
            "product": "dsr-golden",
            "service": "api",
            "environment": "prod",
            "version": "v1.0",
            "previousVersion": "v1.1",
            "isRollback": true,
            "status": "succeeded",
            "source": "ci",
            "deployedAt": "2026-01-04T00:00:00+00:00",
            "references": [],
            "participants": [],
            "enrichment": null,
            "run": null
          },
          {
            "id": "5a7e0001-0000-4000-8000-000000000005",
            "product": "dsr-golden",
            "service": "api",
            "environment": "staging",
            "version": "v1.1",
            "previousVersion": "v1.0",
            "isRollback": false,
            "status": "in_progress",
            "source": "ci",
            "deployedAt": "2026-01-05T00:00:00+00:00",
            "references": [],
            "participants": [],
            "enrichment": null,
            "run": null
          },
          {
            "id": "5a7e0001-0000-4000-8000-000000000009",
            "product": "dsr-golden",
            "service": "web",
            "environment": "dev",
            "version": "v2.0",
            "previousVersion": null,
            "isRollback": false,
            "status": "succeeded",
            "source": "ci",
            "deployedAt": "2026-01-06T00:00:00+00:00",
            "references": [
              {"type": "work-item", "url": "https://jira.example.com/browse/WK-9", "provider": "jira", "key": "WK-9", "revision": null, "title": "Ticket for web-dev-a", "subTitle": null, "participants": [{"role": "qa-owner", "displayName": "Quinn QA", "email": "quinn@example.com", "isOverride": false, "assignedBy": null}], "commits": ["c0ffee9"], "content": "The whole description of the web-dev-a ticket — long, with \"quotes\" & <markup>.", "resolution": {"resolved": true, "status": "Done", "at": "2026-01-01T00:00:00+00:00", "by": {"displayName": "Rita", "email": "rita@example.com"}}, "occurredAt": "2026-01-01T00:00:00+00:00", "priority": "High", "workItemType": "Bug"},
              {"type": "pull-request", "url": "https://github.com/acme/web-dev-a/pull/9", "provider": "github", "key": "9", "revision": "c0ffee9", "title": "PR for web-dev-a", "subTitle": null, "participants": [{"role": "pr-author", "displayName": "Bob Builder", "email": "bob@example.com", "isOverride": false, "assignedBy": null}], "commits": null, "content": "PR body.", "resolution": null, "occurredAt": "2026-01-01T01:00:00+00:00", "priority": null, "workItemType": null},
              {"type": "pipeline", "url": "https://dev.azure.com/acme/_build/results?buildId=9", "provider": "azure-devops", "key": "9", "revision": null, "title": null, "subTitle": null, "participants": null, "commits": null, "content": null, "resolution": null, "occurredAt": null, "priority": null, "workItemType": null}
            ],
            "participants": [{"role": "triggered-by", "displayName": "Tess Trigger", "email": "tess@example.com", "isOverride": false, "assignedBy": null}],
            "enrichment": {"labels": {"workItemTitle": "Ticket for web-dev-a", "team": "platform"}, "participants": [{"role": "assignee", "displayName": "Ann Assignee", "email": "ann@example.com", "isOverride": false, "assignedBy": null}], "enrichedAt": "2026-01-01T02:00:00+00:00"},
            "run": {"provider": "github-actions", "runId": "99", "runNumber": "#9", "attempt": 1, "workflowName": "Deploy", "jobName": "Deploy Helm (web-dev-a)", "runUrl": "https://github.com/acme/release/actions/runs/99", "jobUrl": "https://github.com/acme/release/actions/runs/99/job/1", "triggeredBy": "tess", "startedAt": "2026-01-05T23:55:00+00:00", "completedAt": "2026-01-06T00:00:00+00:00", "failureReason": null}
          },
          {
            "id": "5a7e0001-0000-4000-8000-00000000000c",
            "product": "dsr-golden",
            "service": "web",
            "environment": "prod",
            "version": "v2.0",
            "previousVersion": null,
            "isRollback": false,
            "status": "failed",
            "source": "github-actions",
            "deployedAt": "2026-01-07T00:00:00+00:00",
            "references": [],
            "participants": [],
            "enrichment": null,
            "run": null
          },
          {
            "id": "5a7e0001-0000-4000-8000-00000000000e",
            "product": "dsr-golden",
            "service": "worker",
            "environment": "staging",
            "version": "v3.0",
            "previousVersion": null,
            "isRollback": false,
            "status": "succeeded",
            "source": "ci",
            "deployedAt": "2026-01-03T00:00:00+00:00",
            "references": [
              {"type": "work-item", "url": "https://jira.example.com/browse/WK-14", "provider": "jira", "key": "WK-14", "revision": null, "title": "Ticket for worker", "subTitle": null, "participants": [{"role": "qa-owner", "displayName": "Olga Override", "email": "olga@example.com", "isOverride": true, "assignedBy": "Admin"}], "commits": ["c0ffee14"], "content": "The whole description of the worker ticket — long, with \"quotes\" & <markup>.", "resolution": {"resolved": true, "status": "Done", "at": "2026-01-01T00:00:00+00:00", "by": {"displayName": "Rita", "email": "rita@example.com"}}, "occurredAt": "2026-01-01T00:00:00+00:00", "priority": "High", "workItemType": "Bug"},
              {"type": "pull-request", "url": "https://github.com/acme/worker/pull/14", "provider": "github", "key": "14", "revision": "c0ffee14", "title": "PR for worker", "subTitle": null, "participants": [{"role": "pr-author", "displayName": "Bob Builder", "email": "bob@example.com", "isOverride": false, "assignedBy": null}], "commits": null, "content": "PR body.", "resolution": null, "occurredAt": "2026-01-01T01:00:00+00:00", "priority": null, "workItemType": null},
              {"type": "pipeline", "url": "https://dev.azure.com/acme/_build/results?buildId=14", "provider": "azure-devops", "key": "14", "revision": null, "title": null, "subTitle": null, "participants": null, "commits": null, "content": null, "resolution": null, "occurredAt": null, "priority": null, "workItemType": null}
            ],
            "participants": [{"role": "triggered-by", "displayName": "Tess Trigger", "email": "tess@example.com", "isOverride": false, "assignedBy": null}],
            "enrichment": {"labels": {"workItemTitle": "Ticket for worker", "team": "platform"}, "participants": [{"role": "assignee", "displayName": "Ann Assignee", "email": "ann@example.com", "isOverride": false, "assignedBy": null}], "enrichedAt": "2026-01-01T02:00:00+00:00"},
            "run": {"provider": "github-actions", "runId": "914", "runNumber": "#14", "attempt": 1, "workflowName": "Deploy", "jobName": "Deploy Helm (worker)", "runUrl": "https://github.com/acme/release/actions/runs/914", "jobUrl": "https://github.com/acme/release/actions/runs/914/job/1", "triggeredBy": "tess", "startedAt": "2026-01-02T23:55:00+00:00", "completedAt": "2026-01-03T00:00:00+00:00", "failureReason": null}
          }
        ]
        """;

    // ── Factory ─────────────────────────────────────────────────────────────

    public class StateFactory : TestFactory
    {
    }
}
