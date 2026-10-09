using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// The promotions list's read path — the request every open InfraPortal tab polls. Pins what its
/// callers rely on while the work behind it is kept cheap:
/// <list type="bullet">
///   <item>The default response shape. Release automation reads candidates here and re-POSTs every
///         reference it finds on upsert, so the default must keep carrying all of them, whole.</item>
///   <item>The opt-in <c>view=summary</c>: the same response minus exactly the per-reference fields no
///         list view reads.</item>
///   <item><c>targetCurrentVersion</c> / <c>fromVersion</c>, resolved per candidate in SQL — over
///         history with repeated deploys, failures, an in-flight deploy and a rollback.</item>
///   <item>Approval capability under a work-item gate, which the list evaluates once and shares.</item>
/// </list>
/// Each test seeds its own product straight into the database, so the shared fixture's rows never
/// meet.
/// </summary>
public class PromotionListReadPathTests : IClassFixture<PromotionListReadPathTests.ListFactory>, IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The list candidate's fields, in response order.</summary>
    private static readonly string[] CandidateFields =
    {
        "id", "product", "service", "sourceEnv", "targetEnv", "version", "fromRevision", "toRevision",
        "targetCurrentVersion", "fromVersion", "sourceBranch", "status", "externalRunUrl", "createdAt",
        "approvedAt", "deployedAt", "supersededById", "participants", "sourceEventParticipants",
        "sourceEventReferences", "canApprove", "pendingGates", "approvableGates", "workItemsOutstanding",
        "deploysOnApproval", "tracksWorkItems", "requiredWorkItemRoles", "workItemRoleGaps",
    };

    /// <summary>A reference in the default (full) shape — every <see cref="ReferenceDto"/> field.</summary>
    private static readonly string[] FullReferenceFields =
    {
        "type", "url", "provider", "key", "revision", "title", "subTitle", "participants", "commits",
        "content", "resolution", "occurredAt", "priority", "workItemType",
    };

    /// <summary>What <c>view=summary</c> leaves out of each reference — and nothing else.</summary>
    private static readonly string[] SummaryDroppedFields =
        { "participants", "commits", "content", "resolution", "occurredAt" };

    private readonly ListFactory _factory;
    private readonly HttpClient _adminClient;

    public PromotionListReadPathTests(ListFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreateAdminClient();
        _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true })
            .GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }

    public void Dispose() => _adminClient.Dispose();

    // ── Response shape ──────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultList_CarriesEveryCandidateFieldAndEveryReferenceWhole()
    {
        var product = NewProduct();
        var id = await SeedCandidateAsync(product, "api", references: RichReferences());

        foreach (var url in new[]
        {
            $"/api/promotions/?product={product}",
            $"/api/promotions/?product={product}&status=Pending",
        })
        {
            var row = await ListRowAsync(url, id);

            Assert.Equal(CandidateFields, row.EnumerateObject().Select(p => p.Name));
            var refs = row.GetProperty("sourceEventReferences").EnumerateArray().ToList();
            Assert.Equal(3, refs.Count);
            foreach (var r in refs)
                Assert.Equal(FullReferenceFields, r.EnumerateObject().Select(p => p.Name));

            // The heavy fields go out as ingested — this is the copy release automation echoes back.
            var workItem = refs.Single(r => r.GetProperty("type").GetString() == "work-item");
            Assert.Equal("The whole ticket description.", workItem.GetProperty("content").GetString());
            Assert.Equal("qa-owner", workItem.GetProperty("participants")[0].GetProperty("role").GetString());
            Assert.Equal("abc1234", workItem.GetProperty("commits")[0].GetString());
            Assert.Equal("In Progress", workItem.GetProperty("resolution").GetProperty("status").GetString());
            Assert.Equal(T0, workItem.GetProperty("occurredAt").GetDateTimeOffset());
            Assert.Equal("PR body.", refs.Single(r => r.GetProperty("type").GetString() == "pull-request")
                .GetProperty("content").GetString());
            // Display resolution still applies: the ticket's commit message is its second line.
            Assert.Equal("Fix the login redirect", workItem.GetProperty("subTitle").GetString());
        }
    }

    [Fact]
    public async Task FullView_IsExactlyTheDefault()
    {
        var product = NewProduct();
        await SeedCandidateAsync(product, "api", references: RichReferences());

        var byDefault = await ListBodyAsync($"/api/promotions/?product={product}");
        var full = await ListBodyAsync($"/api/promotions/?product={product}&view=full");
        var upper = await ListBodyAsync($"/api/promotions/?product={product}&view=FULL");

        Assert.Equal(byDefault, full);
        Assert.Equal(byDefault, upper);
    }

    [Fact]
    public async Task SummaryView_DropsOnlyTheUnreadReferenceFields()
    {
        var product = NewProduct();
        await SeedCandidateAsync(product, "api", references: RichReferences());
        await SeedCandidateAsync(product, "web", references: RichReferences());

        var full = JsonNode.Parse(await ListBodyAsync($"/api/promotions/?product={product}"))!;
        var summary = JsonNode.Parse(await ListBodyAsync($"/api/promotions/?product={product}&view=summary"))!;

        var summaryRefs = summary["candidates"]!.AsArray()
            .SelectMany(c => c!["sourceEventReferences"]!.AsArray())
            .ToList();
        Assert.Equal(6, summaryRefs.Count);
        foreach (var r in summaryRefs)
        {
            Assert.Equal(
                FullReferenceFields.Except(SummaryDroppedFields),
                r!.AsObject().Select(p => p.Key));
        }

        // Everything else — every candidate field, every kept reference field, every value — is the
        // default response's, byte for byte once the dropped fields are taken out of it.
        foreach (var r in full["candidates"]!.AsArray().SelectMany(c => c!["sourceEventReferences"]!.AsArray()))
        {
            foreach (var field in SummaryDroppedFields) r!.AsObject().Remove(field);
        }
        Assert.True(JsonNode.DeepEquals(full, summary), $"full (trimmed): {full}\nsummary: {summary}");
    }

    [Fact]
    public async Task UnknownView_IsRejected()
    {
        var response = await _adminClient.GetAsync("/api/promotions/?view=compact");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'view' must be one of", await response.Content.ReadAsStringAsync());
    }

    // ── Approval capability ─────────────────────────────────────────────────

    [Fact]
    public async Task List_HoldsApprovalBehindAnOutstandingWorkItemGate()
    {
        // The list evaluates the work-item gate once and feeds it both to pendingGates /
        // workItemsOutstanding and to canApprove / approvableGates. Both halves have to agree.
        var product = NewProduct();
        var held = await SeedCandidateAsync(product, "held", requireAllWorkItemsApproved: true, workItemKeys: new[] { "TV-1" });
        var free = await SeedCandidateAsync(product, "free", requireAllWorkItemsApproved: false, workItemKeys: new[] { "TV-1" });

        var heldRow = await ListRowAsync($"/api/promotions/?product={product}&status=Pending", held);
        Assert.True(heldRow.GetProperty("workItemsOutstanding").GetBoolean());
        Assert.False(heldRow.GetProperty("canApprove").GetBoolean());
        Assert.Empty(heldRow.GetProperty("approvableGates").EnumerateArray());
        Assert.Equal(new[] { "Release Approval" }, Strings(heldRow, "pendingGates"));

        var freeRow = await ListRowAsync($"/api/promotions/?product={product}&status=Pending", free);
        Assert.False(freeRow.GetProperty("workItemsOutstanding").GetBoolean());
        Assert.True(freeRow.GetProperty("canApprove").GetBoolean());
        Assert.Equal(new[] { "Release Approval" }, Strings(freeRow, "approvableGates"));
    }

    // ── Target-environment versions ─────────────────────────────────────────

    [Fact]
    public async Task TargetVersions_ResolveTheSameOverRealisticDeployHistory()
    {
        var product = NewProduct();

        // svc "multi" in prod: repeated deploys, a failure, and an in-flight deploy on top.
        await SeedDeploysAsync(
            (product, "multi", "prod", "v1.0", "succeeded", Day(1), false),
            (product, "multi", "prod", "v1.1", "succeeded", Day(2), false),
            (product, "multi", "prod", "v1.2", "failed", Day(3), false),
            (product, "multi", "prod", "v1.3", "succeeded", Day(4), false),
            (product, "multi", "prod", "v1.4", "in_progress", Day(5), false),
            // Neighbours that must not leak in: another env, another service, another product.
            (product, "multi", "staging", "v9.0", "succeeded", Day(6), false),
            (product, "other", "prod", "v7.0", "succeeded", Day(7), false),
            (product + "-x", "multi", "prod", "v8.0", "succeeded", Day(8), false));

        // svc "rollback" in prod: v2.1 rolled back to v2.0.
        await SeedDeploysAsync(
            (product, "rollback", "prod", "v2.0", "succeeded", Day(1), false),
            (product, "rollback", "prod", "v2.1", "succeeded", Day(2), false),
            (product, "rollback", "prod", "v2.0", "succeeded", Day(3), true));

        // svc "failonly": prod has only ever seen a failed deploy.
        await SeedDeploysAsync((product, "failonly", "prod", "v3.0", "failed", Day(1), false));

        var cases = new (string Name, Guid Id, string? Current, string? From)[]
        {
            // Open: a stored baseline wins; without one it is live state — the newest event, whatever
            // became of it.
            ("pending, stored", await Seed("multi", PromotionStatus.Pending, fromVersion: "v1.3"), "v1.4", "v1.3"),
            ("pending, none", await Seed("multi", PromotionStatus.Pending), "v1.4", "v1.4"),
            ("approved, none", await Seed("multi", PromotionStatus.Approved), "v1.4", "v1.4"),
            ("deploying, none", await Seed("multi", PromotionStatus.Deploying), "v1.4", "v1.4"),
            // Closed with a stored baseline: stored, whatever history says.
            ("deployed, stored", await Seed("multi", PromotionStatus.Deployed, fromVersion: "v0.9", deployedAt: Day(4.5)), "v1.4", "v0.9"),
            // Closed without one: the newest succeeded deploy strictly before it closed — the failed
            // v1.2 never counts, and a landing at exactly a deploy's timestamp excludes that deploy.
            ("deployed, after failure", await Seed("multi", PromotionStatus.Deployed, deployedAt: Day(3.5)), "v1.4", "v1.1"),
            ("deployed, at a deploy", await Seed("multi", PromotionStatus.Deployed, deployedAt: Day(2)), "v1.4", "v1.0"),
            ("deployed, no landing time", await Seed("multi", PromotionStatus.Deployed, createdAt: Day(4.5)), "v1.4", "v1.3"),
            ("rejected, empty stored", await Seed("multi", PromotionStatus.Rejected, fromVersion: "", createdAt: Day(2.5)), "v1.4", "v1.1"),
            ("superseded, before history", await Seed("multi", PromotionStatus.Superseded, createdAt: Day(0.5)), "v1.4", null),
            // A rollback is a succeeded deploy like any other.
            ("rollback, pending", await Seed("rollback", PromotionStatus.Pending), "v2.0", "v2.0"),
            ("rollback, deployed after it", await Seed("rollback", PromotionStatus.Deployed, deployedAt: Day(3.5)), "v2.0", "v2.0"),
            ("rollback, rejected before it", await Seed("rollback", PromotionStatus.Rejected, createdAt: Day(2.5)), "v2.0", "v2.1"),
            // Nothing succeeded, or nothing at all, in the target.
            ("failonly, deployed", await Seed("failonly", PromotionStatus.Deployed, deployedAt: Day(2)), "v3.0", null),
            ("never, pending", await Seed("never", PromotionStatus.Pending), null, null),
            ("never, deployed", await Seed("never", PromotionStatus.Deployed, deployedAt: Day(2)), null, null),
        };

        var body = JsonDocument.Parse(await ListBodyAsync($"/api/promotions/?product={product}&limit=200")).RootElement;
        var rows = body.GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => Guid.Parse(c.GetProperty("id").GetString()!));

        foreach (var (name, id, current, from) in cases)
        {
            Assert.True(rows.ContainsKey(id), $"{name}: missing from the list");
            Assert.Equal((name, current, from), (name,
                rows[id].GetProperty("targetCurrentVersion").GetString(),
                rows[id].GetProperty("fromVersion").GetString()));
        }

        // The detail endpoint resolves a single candidate through the same path.
        foreach (var (name, id, current, from) in cases.Where(c => c.Name.StartsWith("deployed") || c.Name.StartsWith("never")))
        {
            var candidate = JsonDocument.Parse(await (await _adminClient.GetAsync($"/api/promotions/{id}"))
                .Content.ReadAsStringAsync()).RootElement.GetProperty("candidate");
            Assert.Equal((name, current, from), (name,
                candidate.GetProperty("targetCurrentVersion").GetString(),
                candidate.GetProperty("fromVersion").GetString()));
        }

        Task<Guid> Seed(
            string service, PromotionStatus status, string? fromVersion = null,
            DateTimeOffset? createdAt = null, DateTimeOffset? deployedAt = null)
            => SeedCandidateAsync(product, service, status, fromVersion, createdAt ?? Day(0.1), deployedAt);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"lrp-{Guid.NewGuid():N}"[..16];

    private static DateTimeOffset Day(double days) => T0.AddDays(days);

    /// <summary>A work item, the commit it rode in on and a PR — every reference field populated.</summary>
    private static List<ReferenceDto> RichReferences() => new()
    {
        new ReferenceDto(
            Type: "work-item",
            Url: "https://jira.example.com/browse/TV-1",
            Provider: "jira",
            Key: "TV-1",
            Title: "Login redirects to the wrong tenant",
            Participants: new[] { new ParticipantDto("qa-owner", "Quinn QA", "quinn@example.com") },
            Commits: new[] { "abc1234" },
            Content: "The whole ticket description.",
            Resolution: new ReferenceResolutionDto(false, "In Progress"),
            OccurredAt: T0,
            Priority: "High",
            WorkItemType: "Bug"),
        new ReferenceDto(
            Type: "commit",
            Url: "https://github.com/acme/api/commit/abc1234def",
            Provider: "github",
            Key: "abc1234def",
            Title: "Fix the login redirect",
            Content: "Commit body.",
            OccurredAt: T0),
        new ReferenceDto(
            Type: "pull-request",
            Url: "https://github.com/acme/api/pull/77",
            Provider: "github",
            Key: "77",
            Revision: "abc1234def",
            Title: "Fix login",
            Participants: new[] { new ParticipantDto("pr-author", "Bob Builder", "bob@example.com") },
            Content: "PR body.",
            OccurredAt: T0),
    };

    private async Task<Guid> SeedCandidateAsync(
        string product,
        string service,
        PromotionStatus status = PromotionStatus.Pending,
        string? fromVersion = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? deployedAt = null,
        List<ReferenceDto>? references = null,
        bool requireAllWorkItemsApproved = false,
        IEnumerable<string>? workItemKeys = null)
    {
        var snapshot = new ResolvedPolicySnapshot(PolicyId: Guid.NewGuid(), EscalationGroup: null)
        {
            ApprovalSteps = new()
            {
                new ApprovalStep("Release Approval", new()
                {
                    new ApproverRequirement(
                        "Approvers", new() { new GroupRef("InfraPortal.Admin", "InfraPortal.Admin") }, new(), 1),
                }),
            },
            RequireAllWorkItemsApproved = requireAllWorkItemsApproved,
        };

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = $"v-{Guid.NewGuid():N}"[..10],
            FromVersion = fromVersion,
            Status = status,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = JsonSerializer.Serialize(
                snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            DeployedAt = deployedAt,
            References = references ?? new List<ReferenceDto>(),
        };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.PromotionCandidates.Add(candidate);
        foreach (var key in workItemKeys ?? Array.Empty<string>())
        {
            db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                WorkItemKey = key,
                Product = product,
                Service = service,
                TargetEnv = candidate.TargetEnv,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return candidate.Id;
    }

    private async Task SeedDeploysAsync(
        params (string Product, string Service, string Env, string Version, string Status, DateTimeOffset At, bool Rollback)[] deploys)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        foreach (var d in deploys)
        {
            db.DeployEvents.Add(new DeployEvent
            {
                Id = Guid.NewGuid(),
                Product = d.Product,
                Service = d.Service,
                Environment = d.Env,
                Version = d.Version,
                Status = d.Status,
                IsRollback = d.Rollback,
                Source = "integration-test",
                DeployedAt = d.At,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<string> ListBodyAsync(string url)
    {
        var response = await _adminClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<JsonElement> ListRowAsync(string url, Guid id)
    {
        var body = JsonDocument.Parse(await ListBodyAsync(url)).RootElement;
        return body.GetProperty("candidates").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == id.ToString());
    }

    private static string[] Strings(JsonElement row, string property)
        => row.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    // ── Factory ─────────────────────────────────────────────────────────────

    public class ListFactory : TestFactory
    {
    }
}
