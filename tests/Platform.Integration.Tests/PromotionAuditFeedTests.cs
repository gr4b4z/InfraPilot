using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// The promotions activity feed (<c>GET /api/promotions/audit</c>), exercised through the questions
/// it exists to answer: what was approved today, what was created today, and what went to prod last
/// week with whose name on it.
///
/// <para>Every test seeds its own product and filters the feed by it. The audit log is global and the
/// fixture's database is shared across the class, so a test that read the unfiltered feed would be
/// asserting on every other test's rows as well.</para>
/// </summary>
public class PromotionAuditFeedTests : IClassFixture<PromotionAuditFeedTests.AuditFactory>, IDisposable
{
    private const string TestApiKey = "test-promotion-audit-key-12345";

    private readonly AuditFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public PromotionAuditFeedTests(AuditFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A product name no other test in the class shares. Short — the column is bounded.</summary>
    private static string NewProduct() => $"aud{Guid.NewGuid():N}"[..12];

    /// <summary>
    /// dev → staging auto-approves (empty step tree); staging → prod needs one admin signature. The
    /// two edges together give a feed with both a system-approved and a human-approved promotion in
    /// it, which is the distinction the "who did it" assertions turn on.
    /// </summary>
    private async Task SeedPoliciesAsync(string product)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });

        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "dev",
            targetEnv = "staging",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
        });

        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "staging",
            targetEnv = "prod",
            steps = new[]
            {
                new
                {
                    name = "Release Approval",
                    requirements = new[]
                    {
                        new
                        {
                            name = "Approvers",
                            groups = new[] { "InfraPortal.Admin" },
                            users = Array.Empty<string>(),
                            minApprovers = 1,
                        },
                    },
                },
            },
            escalationGroup = (string?)null,
        });
    }

    /// <summary>
    /// Creates a candidate the way CI does: a succeeded deploy in the source environment (which
    /// external create validates against), then the net change set.
    /// </summary>
    private async Task<Guid> CreatePromotionAsync(
        string product, string sourceEnv, string targetEnv, string version,
        string service = "api", object[]? references = null)
    {
        var deploy = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment = sourceEnv,
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
        });
        Assert.Equal(HttpStatusCode.Created, deploy.StatusCode);

        var created = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service,
            sourceEnv,
            targetEnv,
            version,
            references = references ?? Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> FeedAsync(string query)
    {
        var response = await _adminClient.GetAsync($"/api/promotions/audit{query}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Entries(JsonElement feed) =>
        feed.GetProperty("entries").EnumerateArray().ToList();

    private static List<string> Actions(JsonElement feed) =>
        Entries(feed).Select(e => e.GetProperty("action").GetString()!).ToList();

    private static string? Str(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// "What was approved for prod, and who did it?" — one filter, one row, and the human's name on
    /// it. The row itself is written by the gate evaluator with the system as its actor, so the
    /// answer has to be lifted from the approval that opened the gate; without that this page would
    /// answer "the system did it", which is true and useless.
    /// </summary>
    [Fact]
    public async Task ApprovedForProd_NamesTheHumanWhoOpenedTheGate()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        var candidateId = await CreatePromotionAsync(product, "staging", "prod", "v1.0.0");

        var approve = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve", new { comment = "ship it" });
        approve.EnsureSuccessStatusCode();

        var feed = await FeedAsync($"?product={product}&targetEnv=prod&category=approved");

        Assert.Equal(1, feed.GetProperty("total").GetInt32());
        var entry = Assert.Single(Entries(feed));
        Assert.Equal("promotion.approved", entry.GetProperty("action").GetString());
        Assert.Equal("approved", entry.GetProperty("category").GetString());
        Assert.Equal(product, entry.GetProperty("product").GetString());
        Assert.Equal("api", entry.GetProperty("service").GetString());
        Assert.Equal("prod", entry.GetProperty("targetEnv").GetString());
        Assert.Equal("v1.0.0", entry.GetProperty("version").GetString());
        Assert.Equal(candidateId, entry.GetProperty("candidateId").GetGuid());

        var approvedBy = entry.GetProperty("approvedBy").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();
        Assert.Single(approvedBy);
        Assert.NotNull(approvedBy[0]);
        Assert.NotEqual("System", approvedBy[0]);

        // The signature itself is its own line, and it carries what the approver typed.
        var steps = await FeedAsync($"?product={product}&category=approval-step");
        var step = Assert.Single(Entries(steps));
        Assert.Equal("ship it", step.GetProperty("comment").GetString());
        Assert.Equal(approvedBy[0], step.GetProperty("actorName").GetString());
    }

    /// <summary>
    /// An auto-approved promotion has nobody to name, and must say so rather than borrowing a name
    /// from somewhere. It still belongs in the "approved" answer — something did go to staging.
    /// </summary>
    [Fact]
    public async Task AutoApproved_IsInTheApprovedAnswerWithNoApproverNamed()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        await CreatePromotionAsync(product, "dev", "staging", "v2.0.0");

        var feed = await FeedAsync($"?product={product}&category=approved");

        var entry = Assert.Single(Entries(feed));
        Assert.Equal("promotion.approved", entry.GetProperty("action").GetString());
        Assert.Equal("system", entry.GetProperty("actorType").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("approvedBy").ValueKind);
    }

    /// <summary>
    /// "What new promotions were created today?" — the created slice, with the promotion each row is
    /// about spelled out. The window is the caller's: an absolute instant, so a calendar day means
    /// the reader's day rather than the server's.
    /// </summary>
    [Fact]
    public async Task CreatedSlice_ListsNewPromotionsInsideTheWindow()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        await CreatePromotionAsync(product, "staging", "prod", "v3.0.0");
        await CreatePromotionAsync(product, "dev", "staging", "v3.1.0", service: "worker");

        var since = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        var feed = await FeedAsync($"?product={product}&category=created&from={since}");

        var versions = Entries(feed)
            .Select(e => e.GetProperty("version").GetString())
            .ToList();
        Assert.Equal(2, versions.Count);
        Assert.Contains("v3.0.0", versions);
        Assert.Contains("v3.1.0", versions);
        Assert.All(Entries(feed), e => Assert.Equal("promotion.candidate.created", e.GetProperty("action").GetString()));

        // A window that starts after the fact is empty — the range is doing the work, not the page size.
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var nothing = await FeedAsync($"?product={product}&from={future}");
        Assert.Empty(Entries(nothing));
        Assert.Equal(0, nothing.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// A rejection is its own answer, and the reason travels with it. Rejecting also has to leave the
    /// approved slice alone — the two questions are asked by different people for different reasons.
    /// </summary>
    [Fact]
    public async Task Rejection_IsItsOwnSliceAndCarriesTheComment()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        var candidateId = await CreatePromotionAsync(product, "staging", "prod", "v4.0.0");

        var reject = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/reject", new { comment = "waiting on the DBA" });
        reject.EnsureSuccessStatusCode();

        var rejected = await FeedAsync($"?product={product}&category=rejected");
        var entry = Assert.Single(Entries(rejected));
        Assert.Equal("promotion.rejected", entry.GetProperty("action").GetString());
        Assert.Equal("waiting on the DBA", entry.GetProperty("comment").GetString());
        Assert.Equal("Rejected", entry.GetProperty("candidateStatus").GetString());

        var approved = await FeedAsync($"?product={product}&category=approved");
        Assert.Empty(Entries(approved));
    }

    /// <summary>
    /// A work-item sign-off writes two audit rows — one against the approval row, one against the
    /// candidate — and one action must be one line here. The candidate-anchored row is the one that
    /// survives, because it is the one that knows which promotion it was about.
    /// </summary>
    [Fact]
    public async Task WorkItemSignoff_IsOneLineNamingTheTicket()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        var references = new object[]
        {
            new { type = "work-item", key = "AUDIT-1", provider = "jira", title = "A ticket" },
        };
        await CreatePromotionAsync(product, "staging", "prod", "v5.0.0", references: references);

        var signoff = await _adminClient.PostAsJsonAsync("/api/work-items/AUDIT-1/approvals",
            new { product, targetEnv = "prod", comment = "tested" });
        signoff.EnsureSuccessStatusCode();

        var feed = await FeedAsync($"?product={product}&category=work-item");

        var entry = Assert.Single(Entries(feed));
        Assert.Equal("promotion.ticket.approved", entry.GetProperty("action").GetString());
        Assert.Equal("AUDIT-1", entry.GetProperty("workItemKey").GetString());
        Assert.Equal("tested", entry.GetProperty("comment").GetString());

        // The legacy per-approval-row duplicate is not in the feed: it is anchored to the approval
        // row rather than to a candidate, so the join leaves it out.
        var everything = await FeedAsync($"?product={product}");
        Assert.DoesNotContain("work-item.approved", Actions(everything));
    }

    /// <summary>
    /// A bypass is an approval that skipped the gate, so it answers "what was approved" — with the
    /// person who forced it and the reason they gave, which is the whole point of recording one.
    /// </summary>
    [Fact]
    public async Task Bypass_AnswersWhatWasApprovedAndCarriesItsReason()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        var candidateId = await CreatePromotionAsync(product, "staging", "prod", "v6.0.0");

        var bypass = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{candidateId}/bypass", new { reason = "incident 4412, approver unreachable" });
        bypass.EnsureSuccessStatusCode();

        var feed = await FeedAsync($"?product={product}&category=approved");
        var entry = Assert.Single(Entries(feed));
        Assert.Equal("promotion.bypassed", entry.GetProperty("action").GetString());
        Assert.Equal("approved", entry.GetProperty("category").GetString());
        Assert.Equal("incident 4412, approver unreachable", entry.GetProperty("reason").GetString());
        Assert.Equal("user", entry.GetProperty("actorType").GetString());
        Assert.NotNull(Str(entry, "actorName"));
    }

    /// <summary>
    /// The facets are what the page's tabs and dropdowns are built from, so they have to be counted
    /// under every filter except their own — a tab badge that ignored the product filter would send
    /// people to an empty tab, and one that counted its own filter would always read as the total.
    /// </summary>
    [Fact]
    public async Task Facets_CountUnderEveryFilterButTheirOwn()
    {
        var product = NewProduct();
        var other = NewProduct();
        await SeedPoliciesAsync(product);
        await SeedPoliciesAsync(other);

        var candidateId = await CreatePromotionAsync(product, "staging", "prod", "v7.0.0");
        await _adminClient.PostAsJsonAsync($"/api/promotions/{candidateId}/approve", new { comment = (string?)null });
        await CreatePromotionAsync(other, "staging", "prod", "v7.0.0");

        var feed = await FeedAsync($"?product={product}&category=approved");

        var actions = feed.GetProperty("actions").EnumerateArray()
            .ToDictionary(a => a.GetProperty("action").GetString()!, a => a.GetProperty("count").GetInt32());

        // Scoped to the product — the other product's creation is not counted…
        Assert.Equal(1, actions["promotion.candidate.created"]);
        // …but not scoped to the selected category, or the tabs could only ever show the one you are on.
        Assert.Equal(1, actions["promotion.approved"]);
        Assert.Equal(1, actions["promotion.approval.recorded"]);

        // The rows themselves are the selected category only, and `total` agrees with the badge.
        Assert.Single(Entries(feed));
        Assert.Equal(actions["promotion.approved"], feed.GetProperty("total").GetInt32());

        var actors = feed.GetProperty("actors").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("System", actors);
        Assert.True(actors.Count >= 2, "the human approver should be offered as an actor filter too");

        // One entry per actor id. The system writes under more than one name — "System" for an
        // auto-approval, "System (gate satisfied)" for a gate it opened — and both carry the id
        // `system`, which is what the filter matches on. Two entries filtering to the same rows is a
        // dropdown that looks broken (and, in React, a duplicate key).
        var ids = feed.GetProperty("actors").EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()!)
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Filtering to one actor must not empty the actor dropdown of everybody else — a facet that
        // counts its own filter turns picking a name into a one-way door out of the view.
        var filtered = await FeedAsync($"?product={product}&actor=system");
        var offered = filtered.GetProperty("actors").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("System", offered);
        Assert.True(offered.Count >= 2, "the actor facet must be counted without the actor filter");
        // …while the rows themselves are only that actor's.
        Assert.All(Entries(filtered), e => Assert.Equal("system", e.GetProperty("actorId").GetString()));
    }

    /// <summary>
    /// Paging must not change what the page is a page of: `total` counts the filtered set, not the
    /// slice, or "12 of 3 shown" is what the reader gets.
    /// </summary>
    [Fact]
    public async Task Paging_SlicesTheRowsWithoutChangingTheTotal()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        await CreatePromotionAsync(product, "staging", "prod", "v10.0.0");
        await CreatePromotionAsync(product, "staging", "prod", "v10.1.0");

        var all = await FeedAsync($"?product={product}&category=created");
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        var first = await FeedAsync($"?product={product}&category=created&pageSize=1");
        Assert.Equal(2, first.GetProperty("total").GetInt32());
        Assert.Single(Entries(first));

        var second = await FeedAsync($"?product={product}&category=created&pageSize=1&page=2");
        Assert.Single(Entries(second));
        Assert.NotEqual(
            Entries(first)[0].GetProperty("id").GetGuid(),
            Entries(second)[0].GetProperty("id").GetGuid());

        // Newest first, so page 1 holds the later of the two creations.
        Assert.Equal("v10.1.0", Entries(first)[0].GetProperty("version").GetString());
        Assert.Equal("v10.0.0", Entries(second)[0].GetProperty("version").GetString());
    }

    /// <summary>
    /// The feed is candidate-anchored precisely so that the visibility rules the promotions list runs
    /// under apply here too. A product the reader has hidden must not come back through the audit
    /// page — otherwise hiding one becomes a setting that only works on some pages.
    /// </summary>
    [Fact]
    public async Task HiddenProduct_DropsOutOfTheFeedAndItsFacets()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        await CreatePromotionAsync(product, "staging", "prod", "v11.0.0");

        Assert.NotEmpty(Entries(await FeedAsync($"?product={product}")));

        var hide = await _adminClient.PutAsJsonAsync("/api/me/preferences/hidden-products",
            new { products = new[] { product } });
        hide.EnsureSuccessStatusCode();

        try
        {
            var feed = await FeedAsync($"?product={product}");
            Assert.Empty(Entries(feed));
            Assert.Equal(0, feed.GetProperty("total").GetInt32());
            Assert.Empty(feed.GetProperty("actions").EnumerateArray());

            // …and it is gone from the unfiltered feed too, not merely from the filtered one.
            var everything = await FeedAsync("?pageSize=200");
            Assert.DoesNotContain(
                product,
                Entries(everything).Select(e => e.GetProperty("product").GetString()));
        }
        finally
        {
            await _adminClient.PutAsJsonAsync("/api/me/preferences/hidden-products",
                new { products = Array.Empty<string>() });
        }
    }

    /// <summary>
    /// An action name nobody has heard of still has to appear — a feed that only shows actions its
    /// category map knows would silently drop whatever the promotions module starts recording next.
    /// </summary>
    [Fact]
    public async Task UnmappedAction_AppearsAsOtherRatherThanVanishing()
    {
        var product = NewProduct();
        await SeedPoliciesAsync(product);
        var candidateId = await CreatePromotionAsync(product, "staging", "prod", "v12.0.0");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Module = "promotions",
            Action = "promotion.something.new",
            ActorId = "someone",
            ActorName = "Someone",
            ActorType = "user",
            EntityType = "PromotionCandidate",
            EntityId = candidateId,
        });
        await db.SaveChangesAsync();

        var feed = await FeedAsync($"?product={product}");
        var entry = Entries(feed).Single(e => e.GetProperty("action").GetString() == "promotion.something.new");
        Assert.Equal("other", entry.GetProperty("category").GetString());

        // And it is selectable by name, and by the category — "everything else" is resolved against
        // the actions actually present, so it can hold an action added after this page was written.
        var byName = await FeedAsync($"?product={product}&action=promotion.something.new");
        Assert.Single(Entries(byName));

        var byCategory = await FeedAsync($"?product={product}&category=other");
        Assert.Single(Entries(byCategory));
        Assert.Equal(1, byCategory.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The feed is for the people who do the approving, not just for admins — but it is not public.
    /// </summary>
    [Fact]
    public async Task Feed_RequiresAuthentication()
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/promotions/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A non-admin approver can read it — that is the point of the CanApprove gate.</summary>
    [Fact]
    public async Task Feed_IsReadableByANonAdminApprover()
    {
        using var user = _factory.CreateAuthenticatedClient("user@localhost", "user123");
        var response = await user.GetAsync("/api/promotions/audit?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public class AuditFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "promotion-audit-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");
        }
    }
}
