using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// End-to-end integration tests for the external promotion-creation flow.
/// Exercises the full HTTP pipeline: API key auth → POST /api/promotions →
/// candidate creation → webhook dispatch → approve/reject → completion matching
/// via a target-env deploy event.
///
/// <para>Promotion candidates are no longer auto-generated from deploy-event ingest (D19):
/// they are created explicitly via <c>POST /api/promotions</c>. Deploy-event ingest only
/// records the deployment and, for a target-env landing, completes a matching candidate.</para>
/// </summary>
public class PromotionFlowTests : IClassFixture<PromotionFlowTests.FlowFactory>, IDisposable
{
    private readonly FlowFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public PromotionFlowTests(FlowFactory factory)
    {
        _factory = factory;

        // API-key client for deploy event ingest.
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", FlowFactory.TestApiKey);

        // Admin client for promotion actions.
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Setup helpers ───────────────────────────────────────────────────────

    // Topology was removed (D19): policy resolution is now the edge guard. We seed step-tree
    // policies (§8) directly. An empty steps[] list means auto-approve; a single group
    // requirement means a human gate (candidate born Pending).
    private async Task SeedPoliciesAsync()
    {
        // Enable the promotions feature flag.
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });

        // Policy: dev → staging = auto-approve (empty step tree).
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "acme",
            service = (string?)null,
            sourceEnv = "dev",
            targetEnv = "staging",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
        });

        // Policy: staging → prod = gated, one InfraPortal.Admin approver required.
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "acme",
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

    private object MakeDeployPayload(
        string env,
        string version = "v1.0.0",
        string status = "succeeded",
        string product = "acme",
        string service = "api") =>
        new
        {
            product,
            service,
            environment = env,
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status,
            participants = new[]
            {
                new { role = "PR Author", displayName = "Bob Builder", email = "bob@example.com" },
            },
        };

    // Create a promotion candidate via the external create endpoint (API-key auth + product
    // scope). Candidates are no longer derived from deploy events (D19); the external CI POSTs
    // the net change set here.
    private async Task<HttpResponseMessage> CreatePromotionAsync(
        string sourceEnv,
        string targetEnv,
        string version,
        string product = "acme",
        string service = "api",
        object[]? references = null)
    {
        // Source validation now requires a succeeded deploy of this exact version in the source env.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload(sourceEnv, version: version, status: "succeeded", product: product, service: service));

        return await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service,
            sourceEnv,
            targetEnv,
            version,
            references = references ?? Array.Empty<object>(),
            participants = new[]
            {
                new { role = "PR Author", displayName = "Bob Builder", email = "bob@example.com" },
            },
        });
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A block is a judgement about a specific build. When a new version arrives carrying the same
    /// work item, that judgement no longer describes anything, so it's cleared and the item goes back
    /// to undecided — otherwise the new promotion would stall on a stale objection nobody is looking
    /// at. The operator whose block vanished finds out from a system entry in the comment thread.
    /// </summary>
    [Fact]
    public async Task Create_NewVersion_ResetsHeldWorkItemDecisions()
    {
        await SeedPoliciesAsync();

        var workItemRef = new object[]
        {
            new { type = "work-item", key = "RESET-1", provider = "azure-devops", title = "Held item" },
        };

        var first = await CreatePromotionAsync("staging", "prod", "v9.0.0", references: workItemRef);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Hold the item back on that build.
        var block = await _adminClient.PostAsJsonAsync("/api/work-items/RESET-1/blocks",
            new { product = "acme", targetEnv = "prod" });
        Assert.Equal(HttpStatusCode.OK, block.StatusCode);

        var beforeCtx = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/work-items/RESET-1?product=acme&targetEnv=prod");
        Assert.Single(beforeCtx.GetProperty("approvals").EnumerateArray());

        // A new version carrying the same work item lands.
        var second = await CreatePromotionAsync("staging", "prod", "v9.1.0", references: workItemRef);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var afterCtx = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/work-items/RESET-1?product=acme&targetEnv=prod");
        Assert.Empty(afterCtx.GetProperty("approvals").EnumerateArray());
        Assert.True(afterCtx.GetProperty("canApprove").GetBoolean());

        // The thread carries both the block and the reset that undid it.
        var thread = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/work-items/RESET-1/comments?product=acme&targetEnv=prod");
        var bodies = thread.GetProperty("comments").EnumerateArray()
            .Select(c => c.GetProperty("body").GetString() ?? "")
            .ToList();
        Assert.Contains(bodies, b => b.Contains("Blocked this work item"));
        Assert.Contains(bodies, b => b.Contains("Reset to pending") && b.Contains("v9.1.0"));
    }

    /// <summary>
    /// The counterpart: an approval is defined to carry across builds, so a new version must leave it
    /// alone. Re-asking for every sign-off on every version would make the gate unusable.
    /// </summary>
    [Fact]
    public async Task Create_NewVersion_KeepsWorkItemApprovals()
    {
        await SeedPoliciesAsync();

        var workItemRef = new object[]
        {
            new { type = "work-item", key = "KEEP-1", provider = "azure-devops", title = "Signed item" },
        };

        await CreatePromotionAsync("staging", "prod", "v8.0.0", references: workItemRef);
        var approve = await _adminClient.PostAsJsonAsync("/api/work-items/KEEP-1/approvals",
            new { product = "acme", targetEnv = "prod" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        await CreatePromotionAsync("staging", "prod", "v8.1.0", references: workItemRef);

        var ctx = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/work-items/KEEP-1?product=acme&targetEnv=prod");
        Assert.Single(ctx.GetProperty("approvals").EnumerateArray());
        Assert.Equal("Approved", ctx.GetProperty("myDecision").GetString());
    }

    /// <summary>
    /// A work item is discovered by parsing commit messages, so the producer declares the commit
    /// hashes that mentioned it on the work-item reference. The detail projection hydrates those into
    /// the commits themselves and — through each commit's merge revision — the pull requests behind
    /// them, which is the only way the ticket→PR link exists: the payload never states it directly.
    ///
    /// <para>Shaped after a real mpt-extensions payload: two tickets, each with one commit, each
    /// commit merged by its own PR, plus repository and pipeline references that must not leak in.</para>
    /// </summary>
    [Fact]
    public async Task Create_WithCommitLinkedWorkItems_ResolvesCommitsAndPullRequests()
    {
        await SeedPoliciesAsync();

        const string shaA = "19a22406ddec682782c02f051b2303b4f3758a22";
        const string shaB = "32a0a09aa2f8f1711d781b802074f44974c8973c";

        var references = new object[]
        {
            new { type = "repository", provider = "azure-devops", revision = shaA,
                  url = "https://example.visualstudio.com/MPT/_git/svc", title = "svc" },
            new { type = "pipeline", provider = "azure-devops", key = "8688281",
                  url = "https://example.visualstudio.com/MPT/_build/results?buildId=8688281" },
            new { type = "commit", provider = "azure-devops", key = shaA,
                  title = "Merged PR 149502: Add CLI command",
                  url = $"https://example.com/_git/svc/commit/{shaA}",
                  participants = new[] { new { role = "author", displayName = "A Author", email = "a@example.com" } } },
            new { type = "work-item", provider = "jira", key = "MPT-23574",
                  url = "https://example.atlassian.net/browse/MPT-23574",
                  title = "Add CLI command to execute offboarding",
                  commits = new[] { shaA } },
            new { type = "pull-request", provider = "azure-devops", key = "149502", revision = shaA,
                  title = "Add CLI command",
                  url = "https://example.visualstudio.com/MPT/_git/svc/pullrequest/149502" },
            new { type = "commit", provider = "azure-devops", key = shaB,
                  title = "Merged PR 149496: Fix templates mapping",
                  url = $"https://example.com/_git/svc/commit/{shaB}" },
            new { type = "work-item", provider = "jira", key = "MPT-23640",
                  url = "https://example.atlassian.net/browse/MPT-23640",
                  title = "Invalid notification data",
                  commits = new[] { shaB } },
            new { type = "pull-request", provider = "azure-devops", key = "149496", revision = shaB,
                  title = "Fix templates mapping",
                  url = "https://example.visualstudio.com/MPT/_git/svc/pullrequest/149496" },
        };

        var created = await CreatePromotionAsync("staging", "prod", "v7.0.0", references: references);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Ticket A: its own commit and its own PR — not the other ticket's.
        var a = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/work-items/MPT-23574/detail?product=acme&targetEnv=prod");

        var aCommits = a.GetProperty("commits").EnumerateArray().ToList();
        Assert.Single(aCommits);
        Assert.Equal(shaA, aCommits[0].GetProperty("hash").GetString());
        Assert.Equal("Merged PR 149502: Add CLI command", aCommits[0].GetProperty("title").GetString());
        Assert.Equal($"https://example.com/_git/svc/commit/{shaA}", aCommits[0].GetProperty("url").GetString());
        Assert.Equal("A Author",
            aCommits[0].GetProperty("participants").EnumerateArray().First().GetProperty("displayName").GetString());

        var aPrs = a.GetProperty("pullRequests").EnumerateArray().ToList();
        Assert.Single(aPrs);
        Assert.Equal("149502", aPrs[0].GetProperty("key").GetString());
        Assert.Equal("Add CLI command", aPrs[0].GetProperty("title").GetString());
        Assert.Equal(shaA, aPrs[0].GetProperty("revision").GetString());

        // Ticket B resolves to the other pair — no cross-contamination between tickets.
        var b = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/work-items/MPT-23640/detail?product=acme&targetEnv=prod");
        Assert.Equal(shaB, b.GetProperty("commits").EnumerateArray().Single().GetProperty("hash").GetString());
        Assert.Equal("149496", b.GetProperty("pullRequests").EnumerateArray().Single().GetProperty("key").GetString());

        // The Jira link the header now promotes to a labelled button.
        Assert.Equal("jira", b.GetProperty("provider").GetString());
        Assert.Equal("https://example.atlassian.net/browse/MPT-23640", b.GetProperty("url").GetString());
    }

    /// <summary>
    /// A declared hash with no matching <c>commit</c> reference still renders — the producer saw that
    /// commit, and dropping it would understate the change set. Abbreviated hashes match the full
    /// reference, because commit messages and version strings routinely carry the short form.
    /// </summary>
    [Fact]
    public async Task Create_WithUnresolvableAndAbbreviatedHashes_StillListsThem()
    {
        await SeedPoliciesAsync();

        const string full = "a7ce996fb64bf76bf2c51f38208a2c1fd35740ab";
        var references = new object[]
        {
            new { type = "commit", provider = "azure-devops", key = full, title = "Add command",
                  url = $"https://example.com/_git/svc/commit/{full}" },
            new { type = "pull-request", provider = "azure-devops", key = "149478", revision = full,
                  title = "Add OffboardAgreementsCommand", url = "https://example.com/pr/149478" },
            new { type = "work-item", provider = "jira", key = "MPT-23508", title = "Create command",
                  url = "https://example.atlassian.net/browse/MPT-23508",
                  // First is abbreviated, second was never included as a `commit` reference.
                  commits = new[] { "a7ce996", "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef" } },
        };

        var created = await CreatePromotionAsync("staging", "prod", "v7.1.0", references: references);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/work-items/MPT-23508/detail?product=acme&targetEnv=prod");

        var commits = detail.GetProperty("commits").EnumerateArray().ToList();
        Assert.Equal(2, commits.Count);

        // Abbreviated hash hydrated from the full-hash reference.
        Assert.Equal("a7ce996", commits[0].GetProperty("hash").GetString());
        Assert.Equal("Add command", commits[0].GetProperty("title").GetString());

        // Unresolvable hash survives as a bare row with no link.
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", commits[1].GetProperty("hash").GetString());
        Assert.Equal(JsonValueKind.Null, commits[1].GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, commits[1].GetProperty("title").ValueKind);

        // The PR is reached via the abbreviated hash matching its full revision.
        Assert.Equal("149478",
            detail.GetProperty("pullRequests").EnumerateArray().Single().GetProperty("key").GetString());
    }

    [Fact]
    public async Task Ingest_DispatchesDeploymentCreatedWebhook()
    {
        // Act: ingest a deploy event.
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", MakeDeployPayload("dev"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Assert: deployment.created webhook was dispatched.
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "deployment.created",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Create_WithAutoApprovePolicy_CreatesApprovedCandidate()
    {
        // Was Ingest_WithTopologyAndPolicy_CreatesPromotionCandidate. Candidates are no longer
        // derived from deploy ingest (D19) — they're created via POST /api/promotions. The staging
        // policy has an empty step tree, so the candidate is born Approved (auto-approve).
        await SeedPoliciesAsync();

        var createResponse = await CreatePromotionAsync(sourceEnv: "dev", targetEnv: "staging", version: "v2.0.0");
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Assert: a promotion candidate exists for staging.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=staging");
        listResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(listResponse);
        var candidates = body.GetProperty("candidates");
        var match = FindCandidate(candidates, "v2.0.0", "staging");
        Assert.NotNull(match);

        // Auto-approve policy → candidate is born Approved.
        Assert.Equal("Approved", match.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_WithoutPolicy_IsRejected()
    {
        // Was Ingest_WithoutPolicy_DoesNotCreateCandidate. With topology gone, the policy-resolution
        // miss is the edge guard: a create for a product with no policy is rejected (422) rather
        // than silently dropped, and no candidate is recorded.
        await SeedPoliciesAsync();

        var createResponse = await CreatePromotionAsync(
            sourceEnv: "dev", targetEnv: "staging", version: "v3.0.0", product: "no-policy-product");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, createResponse.StatusCode);

        // Assert: no candidates for this product.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=no-policy-product");
        listResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(listResponse);
        var candidates = body.GetProperty("candidates");
        Assert.Equal(0, candidates.GetArrayLength());
    }

    [Fact]
    public async Task Create_WithoutSucceededSourceDeploy_IsRejected()
    {
        // The other cross-API guard: even for a policy-enrolled edge, you can only promote a version
        // that actually shipped to the source env. Here dev→staging IS enrolled, but no succeeded
        // deploy of this version exists in dev, so create is rejected (422 — SourceDeploymentNotFound)
        // and no candidate is recorded. (Note: we POST /api/promotions directly rather than via the
        // CreatePromotionAsync helper, which would seed the source deploy that this test omits.)
        await SeedPoliciesAsync();

        var createResponse = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product = "acme",
            service = "api",
            sourceEnv = "dev",
            targetEnv = "staging",
            version = "v-never-shipped",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, createResponse.StatusCode);

        // Assert: no candidate was recorded for the unshipped version.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=staging");
        listResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(listResponse);
        var candidates = body.GetProperty("candidates");
        Assert.Null(FindCandidate(candidates, "v-never-shipped", "staging"));
    }

    [Fact]
    public async Task Create_AutoApprovePolicy_DispatchesPromotionApprovedWebhook()
    {
        // Was Ingest_AutoApprovePolicy_DispatchesPromotionApprovedWebhook. Auto-approve now fires
        // on external create (born Approved), not on deploy ingest.
        await SeedPoliciesAsync();
        _factory.WebhookDispatcher.ClearReceivedCalls();

        await CreatePromotionAsync(sourceEnv: "dev", targetEnv: "staging", version: "v4.0.0");

        // Assert: promotion.approved was dispatched (auto-approve).
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>(),
            Arg.Any<WebhookDispatchOptions?>());
    }

    [Fact]
    public async Task Create_GatedPolicy_ApproveEmitsWebhook()
    {
        await SeedPoliciesAsync();

        // Use a unique service to avoid interference with other tests.
        var service = $"approve-svc-{Guid.NewGuid():N}"[..20];

        // Create a Pending candidate for prod (gated policy).
        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v5.0.0", service: service);

        // Find the pending candidate.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        listResponse.EnsureSuccessStatusCode();
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v5.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        _factory.WebhookDispatcher.ClearReceivedCalls();

        // Act: admin approves the candidate.
        var approveResponse = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve",
            new { comment = "ship it" });
        approveResponse.EnsureSuccessStatusCode();

        // Assert: promotion.approved webhook was dispatched.
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>(),
            Arg.Any<WebhookDispatchOptions?>());
    }

    [Fact]
    public async Task Create_GatedPolicy_RejectEmitsWebhook()
    {
        await SeedPoliciesAsync();

        // Use a unique service name to avoid interference with other tests' candidates.
        var service = $"reject-svc-{Guid.NewGuid():N}"[..20];

        // Create a Pending candidate for prod.
        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v6.0.0", service: service);

        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        listResponse.EnsureSuccessStatusCode();
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v6.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        _factory.WebhookDispatcher.ClearReceivedCalls();

        // Act: admin rejects.
        var rejectResponse = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/reject",
            new { comment = "not ready" });
        rejectResponse.EnsureSuccessStatusCode();

        // Assert: promotion.rejected webhook dispatched.
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.rejected",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Create_ThenDeployInTargetEnv_ClosesPromotionAsDeployed()
    {
        // Was Ingest_InTargetEnv_ClosesPromotionAsDeployed. The completion-match on a target-env
        // deploy event still exists (PromotionIngestHook.MatchCompletionAsync); only the candidate's
        // origin changed — it's now created via POST /api/promotions instead of derived from ingest.
        await SeedPoliciesAsync();

        // Use a unique service to avoid interference with other tests.
        var service = $"close-svc-{Guid.NewGuid():N}"[..20];

        // 1. Create a Pending candidate for prod.
        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v7.0.0", service: service);

        // 2. Find and approve the candidate.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v7.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve",
            new { comment = "approved" });

        // 3. Ingest the same version landing in prod → should close the candidate.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("prod", version: "v7.0.0", service: service));

        // 4. Assert: candidate is now Deployed.
        var detailResponse = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await Deserialize(detailResponse);
        var status = detail.GetProperty("candidate").GetProperty("status").GetString();
        Assert.Equal("Deployed", status);
    }

    /// <summary>
    /// Same landing, but nobody approved the candidate first: the version got to prod out-of-band.
    /// The change is live either way, so the candidate still closes as Deployed — with a system
    /// comment saying it happened outside this promotion.
    /// </summary>
    [Fact]
    public async Task Create_ThenDeployInTargetEnvWhilePending_ClosesPromotionAsDeployed()
    {
        await SeedPoliciesAsync();

        var service = $"oob-svc-{Guid.NewGuid():N}"[..20];

        // 1. Create a Pending candidate for prod (staging → prod is gated, so it stays Pending).
        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v7.5.0", service: service);

        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v7.5.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        // 2. The same version lands in prod without this promotion ever being approved.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("prod", version: "v7.5.0", service: service));

        // 3. Assert: closed as Deployed, with the explanatory system comment.
        var detailResponse = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await Deserialize(detailResponse);
        Assert.Equal("Deployed", detail.GetProperty("candidate").GetProperty("status").GetString());

        var bodies = detail.GetProperty("comments").EnumerateArray()
            .Select(c => c.GetProperty("body").GetString()!).ToList();
        Assert.Contains(bodies, b => b.Contains("outside this promotion"));
    }

    /// <summary>
    /// A rejection says the version should not ship. If it ships anyway, the candidate has to record
    /// that — it closes as Deployed, the rejection stays in the approval trail, and the thread
    /// explains the contradiction rather than leaving someone to puzzle it out.
    /// </summary>
    [Fact]
    public async Task Rejected_ThenDeployInTargetEnv_ClosesPromotionAsDeployed()
    {
        await SeedPoliciesAsync();

        var service = $"rejdep-svc-{Guid.NewGuid():N}"[..20];

        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v7.7.0", service: service);

        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v7.7.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        var reject = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/reject", new { comment = "not ready" });
        reject.EnsureSuccessStatusCode();

        // It ships anyway.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("prod", version: "v7.7.0", service: service));

        var detailResponse = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await Deserialize(detailResponse);
        Assert.Equal("Deployed", detail.GetProperty("candidate").GetProperty("status").GetString());

        // The rejection is still on the record.
        Assert.Contains(detail.GetProperty("approvals").EnumerateArray(),
            a => a.GetProperty("decision").GetString() == "Rejected");

        var bodies = detail.GetProperty("comments").EnumerateArray()
            .Select(c => c.GetProperty("body").GetString()!).ToList();
        Assert.Contains(bodies, b => b.Contains("rejected") && b.Contains("not ready"));
        Assert.Contains(bodies, b => b.Contains("after it had been rejected"));
    }

    /// <summary>
    /// A failed deploy of the candidate's version did not put it live, so nothing completes — the
    /// promotion must stay Pending.
    /// </summary>
    [Fact]
    public async Task Create_ThenFailedDeployInTargetEnv_LeavesPromotionPending()
    {
        await SeedPoliciesAsync();

        var service = $"oobfail-svc-{Guid.NewGuid():N}"[..20];

        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v7.6.0", service: service);

        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v7.6.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("prod", version: "v7.6.0", status: "failed", service: service));

        var detailResponse = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await Deserialize(detailResponse);
        Assert.Equal("Pending", detail.GetProperty("candidate").GetProperty("status").GetString());
    }

    /// <summary>
    /// The undo, over HTTP: approve, change your mind, and the queue has the promotion back as if the
    /// approval had never happened — same person able to approve it again.
    /// </summary>
    [Fact]
    public async Task Approve_ThenCancelApproval_ReturnsCandidateToPending()
    {
        await SeedPoliciesAsync();

        var service = $"undo-svc-{Guid.NewGuid():N}"[..20];
        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v8.1.0", service: service);

        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v8.1.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        var approveResponse = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve", new { comment = "ship it" });
        approveResponse.EnsureSuccessStatusCode();

        var beforeUndo = await Deserialize(await _adminClient.GetAsync($"/api/promotions/{candidateId}"));
        Assert.Equal("Approved", beforeUndo.GetProperty("candidate").GetProperty("status").GetString());
        Assert.True(beforeUndo.GetProperty("canCancelApproval").GetBoolean());

        _factory.WebhookDispatcher.ClearReceivedCalls();

        var cancelResponse = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/cancel-approval", new { comment = "wrong service" });
        cancelResponse.EnsureSuccessStatusCode();
        var cancelBody = await Deserialize(cancelResponse);
        Assert.Equal("Pending", cancelBody.GetProperty("candidate").GetProperty("status").GetString());
        Assert.Equal(1, cancelBody.GetProperty("clearedApprovals").GetInt32());

        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.approval.cancelled",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>(),
            Arg.Any<WebhookDispatchOptions?>());

        // The trail is empty again and the same approver can approve — the undo is real, not a label.
        var afterUndo = await Deserialize(await _adminClient.GetAsync($"/api/promotions/{candidateId}"));
        Assert.Empty(afterUndo.GetProperty("approvals").EnumerateArray());
        Assert.False(afterUndo.GetProperty("canCancelApproval").GetBoolean());

        var reapprove = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve", new { comment = "meant this one" });
        reapprove.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The window closes at dispatch: a promotion the executor already has cannot be un-approved,
    /// because un-approving it would not stop anything.
    /// </summary>
    [Fact]
    public async Task CancelApproval_OnADeployedPromotion_Is400()
    {
        await SeedPoliciesAsync();

        var service = $"undo-late-{Guid.NewGuid():N}"[..20];
        await CreatePromotionAsync(sourceEnv: "staging", targetEnv: "prod", version: "v8.2.0", service: service);

        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v8.2.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        await _adminClient.PostAsJsonAsync($"/api/promotions/{candidateId}/approve", new { comment = "ok" });
        // The version lands in prod, which closes the candidate as Deployed.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("prod", version: "v8.2.0", service: service));

        var cancelResponse = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/cancel-approval", new { comment = "too late" });

        Assert.Equal(HttpStatusCode.BadRequest, cancelResponse.StatusCode);
    }

    // ── Utility ─────────────────────────────────────────────────────────────

    private HttpClient CreateAuthenticatedClient(string email, string password)
    {
        var client = _factory.CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email, password })
            .GetAwaiter().GetResult();
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = Deserialize(loginResponse).GetAwaiter().GetResult();
        var token = loginBody.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    private static JsonElement? FindCandidate(JsonElement candidates, string version, string targetEnv)
    {
        foreach (var c in candidates.EnumerateArray())
        {
            if (c.GetProperty("version").GetString() == version &&
                c.GetProperty("targetEnv").GetString() == targetEnv)
                return c;
        }
        return null;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the test server with an in-memory SQLite database, local-JWT auth,
    /// a test API key for deployment ingest, and a captured <see cref="IWebhookDispatcher"/>
    /// mock so tests can verify webhook calls.
    /// </summary>
    public class FlowFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-integration-key-12345";

        /// <summary>
        /// Exposed so tests can assert which webhook events were dispatched.
        /// </summary>
        public IWebhookDispatcher WebhookDispatcher { get; } = Substitute.For<IWebhookDispatcher>();

        private readonly SqliteConnection _connection;

        public FlowFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
                .UseSqlite(_connection)
                .Options;
            using var db = new SqliteTestDbContext(options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // Add a test API key for deployment ingest.
            builder.UseSetting("Deployments:ApiKeys:0:Name", "integration-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");

            builder.ConfigureServices(services =>
            {
                // Remove the real DB registrations.
                RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
                RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
                RemoveService<DbContextOptions<PlatformDbContext>>(services);
                RemoveService<PostgresPlatformDbContext>(services);
                RemoveService<SqlServerPlatformDbContext>(services);
                RemoveService<PlatformDbContext>(services);

                services.AddSingleton<DbConnection>(_connection);
                // Register SqliteTestDbContext (with DateTimeOffset→long conversion) as PlatformDbContext.
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));

                // Replace the webhook dispatcher with our captured mock.
                RemoveService<IWebhookDispatcher>(services);
                services.AddSingleton(WebhookDispatcher);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }

        private static void RemoveService<T>(IServiceCollection services)
        {
            var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
            foreach (var d in descriptors) services.Remove(d);
        }
    }
}

// SqliteTestDbContext is now defined in TestInfrastructure.cs
