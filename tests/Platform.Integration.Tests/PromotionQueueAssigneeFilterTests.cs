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
using Platform.Api.Features.Promotions;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Tests covering the assignee filter on <c>GET /api/work-items/me/pending</c>. The filter is
/// a display-only narrowing — server-side authorisation (group membership, excluded role,
/// not-yet-decided) is unchanged. Each test seeds its own service / candidate and asserts the
/// filter returns the expected subset of the user's authorized queue.
/// </summary>
public class PromotionQueueAssigneeFilterTests
    : IClassFixture<PromotionQueueAssigneeFilterTests.AssigneeFilterFactory>, IDisposable
{
    private readonly AssigneeFilterFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public PromotionQueueAssigneeFilterTests(AssigneeFilterFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", AssigneeFilterFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── 1. assignee=<my email> returns only candidates where I'm a named assignee ──

    [Fact]
    public async Task AssigneeFilter_ByCurrentUserEmail_ReturnsOnlyCandidatesAssignedToMe()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Mine: I'm the QA on a reference participant.
        var (eventMine, _) = await IngestDeployAsync(
            product,
            service: "svc-mine",
            referenceKey: "MINE-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        // Theirs: someone else is the QA, I'm not on the candidate.
        var (eventTheirs, _) = await IngestDeployAsync(
            product,
            service: "svc-theirs",
            referenceKey: "THEIRS-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        var mine = await GetPendingAsync(assignee: "admin@localhost");
        Assert.Contains(mine, t => t.WorkItemKey == "MINE-1");
        Assert.DoesNotContain(mine, t => t.WorkItemKey == "THEIRS-1");

        // Sanity: with no filter, both rows are present (authorisation is the same).
        var unfiltered = await GetPendingAsync(assignee: null);
        Assert.Contains(unfiltered, t => t.WorkItemKey == "MINE-1");
        Assert.Contains(unfiltered, t => t.WorkItemKey == "THEIRS-1");

        // Touch the local variables so unused-warning analyzers don't trip.
        Assert.NotEqual(Guid.Empty, eventMine);
        Assert.NotEqual(Guid.Empty, eventTheirs);
    }

    // ── 2. assignee=<other email> returns their candidates, none of mine ──

    [Fact]
    public async Task AssigneeFilter_ByOtherEmail_ReturnsTheirCandidatesNotMine()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        await IngestDeployAsync(
            product,
            service: "svc-mine-2",
            referenceKey: "MINE2-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        await IngestDeployAsync(
            product,
            service: "svc-them",
            referenceKey: "THEM-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Other", email = "other@example.com" },
            });

        var theirs = await GetPendingAsync(assignee: "other@example.com");
        Assert.Contains(theirs, t => t.WorkItemKey == "THEM-1");
        Assert.DoesNotContain(theirs, t => t.WorkItemKey == "MINE2-1");
    }

    // ── 3. assignee=unassigned returns candidates with no participant in any assignee role ──

    [Fact]
    public async Task AssigneeFilter_Unassigned_ReturnsCandidatesWithoutNamedAssignees()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Has a QA participant — should NOT show up as unassigned.
        await IngestDeployAsync(
            product,
            service: "svc-named",
            referenceKey: "NAMED-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // No participants in any assignee role (only triggered-by event-level participant
        // which is not in the default assignee role set).
        await IngestDeployAsync(
            product,
            service: "svc-empty",
            referenceKey: "EMPTY-1",
            referenceParticipants: null);

        var unassigned = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(unassigned, t => t.WorkItemKey == "EMPTY-1");
        Assert.DoesNotContain(unassigned, t => t.WorkItemKey == "NAMED-1");
    }

    // ── 4. Setting override changes which roles count as "assigned" ──

    [Fact]
    public async Task AssigneeFilter_RoleSetOverride_RestrictsAssigneeRoles()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Reviewer-only — with the default role set ["qa","reviewer","assignee"] this
        // candidate is "assigned"; with the override ["qa"] it's NOT, so it should appear
        // under the unassigned filter once we override.
        await IngestDeployAsync(
            product,
            service: "svc-rev-only",
            referenceKey: "REV-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Other", email = "other@example.com" },
            });

        // Sanity: with the default set, the reviewer-only candidate is NOT unassigned.
        var beforeOverride = await GetPendingAsync(assignee: "unassigned");
        Assert.DoesNotContain(beforeOverride, t => t.WorkItemKey == "REV-1");

        // Insert / update the platform_settings row so only "qa" counts as an assignee role.
        await SetAssigneeRoleSettingAsync("[\"qa\"]");

        var afterOverride = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(afterOverride, t => t.WorkItemKey == "REV-1");

        // Reset for any sibling tests that expect the default. The fixture is shared.
        await ResetAssigneeRoleSettingAsync();
    }

    // ── 5. Tombstone interaction — overridden-cleared QA → treated as unassigned ──

    [Fact]
    public async Task AssigneeFilter_TombstonedAssignee_IsTreatedAsUnassigned()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        var (eventId, _) = await IngestDeployAsync(
            product,
            service: "svc-tomb",
            referenceKey: "TOMB-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // Tombstone — clears the QA slot, so the merged view should treat the candidate as
        // having no participant in any assignee role.
        var patch = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/TOMB-1/participants",
            new { role = "qa", assignee = (object?)null });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var unassigned = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(unassigned, t => t.WorkItemKey == "TOMB-1");

        // And conversely, filtering by the now-tombstoned email should NOT return the row
        // (the override merge has dropped the tombstoned participant from the merged view).
        var byOldEmail = await GetPendingAsync(assignee: "other@example.com");
        Assert.DoesNotContain(byOldEmail, t => t.WorkItemKey == "TOMB-1");
    }

    // ── 6. Email match is case-insensitive ──

    [Fact]
    public async Task AssigneeFilter_EmailMatch_IsCaseInsensitive()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        await IngestDeployAsync(
            product,
            service: "svc-case",
            referenceKey: "CASE-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Mixed Case", email = "Mixed.Case@Example.COM" },
            });

        var upper = await GetPendingAsync(assignee: "MIXED.CASE@EXAMPLE.COM");
        Assert.Contains(upper, t => t.WorkItemKey == "CASE-1");

        var lower = await GetPendingAsync(assignee: "mixed.case@example.com");
        Assert.Contains(lower, t => t.WorkItemKey == "CASE-1");
    }

    // ── 7. Role + person matrix ─────────────────────────────────────────────

    [Fact]
    public async Task RolePersonMatrix_RoleOnly_ReturnsCandidatesWithSomeoneInRole()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // QA: someone in role.
        await IngestDeployAsync(
            product,
            service: "svc-qa",
            referenceKey: "RQA-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // Reviewer-only — under role=qa narrowing this row should drop.
        await IngestDeployAsync(
            product,
            service: "svc-rev",
            referenceKey: "RREV-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Other", email = "other@example.com" },
            });

        var result = await GetPendingAsync(role: "qa", assignee: null);
        Assert.Contains(result.Tickets, t => t.WorkItemKey == "RQA-1");
        Assert.DoesNotContain(result.Tickets, t => t.WorkItemKey == "RREV-1");
    }

    [Fact]
    public async Task RolePersonMatrix_RoleAndMe_ReturnsOnlyWhereIAmInThatRole()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // I'm QA → match.
        await IngestDeployAsync(
            product,
            service: "svc-iq",
            referenceKey: "IQ-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        // I'm Reviewer (not QA) → drop under role=qa+me.
        await IngestDeployAsync(
            product,
            service: "svc-ir",
            referenceKey: "IR-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Admin", email = "admin@localhost" },
            });

        var result = await GetPendingAsync(role: "qa", assignee: "admin@localhost");
        Assert.Contains(result.Tickets, t => t.WorkItemKey == "IQ-1");
        Assert.DoesNotContain(result.Tickets, t => t.WorkItemKey == "IR-1");
    }

    [Fact]
    public async Task RolePersonMatrix_RoleAndOtherPerson_ReturnsOnlyWhereTheyAreInRole()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Other is QA → match.
        await IngestDeployAsync(
            product,
            service: "svc-oq",
            referenceKey: "OQ-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // Other is Reviewer (not QA) → drop under role=qa+other.
        await IngestDeployAsync(
            product,
            service: "svc-or",
            referenceKey: "OR-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Other", email = "other@example.com" },
            });

        var result = await GetPendingAsync(role: "qa", assignee: "other@example.com");
        Assert.Contains(result.Tickets, t => t.WorkItemKey == "OQ-1");
        Assert.DoesNotContain(result.Tickets, t => t.WorkItemKey == "OR-1");
    }

    [Fact]
    public async Task RolePersonMatrix_RoleAndUnassigned_ReturnsOnlyWhereThatRoleIsEmpty()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Has QA → drop under role=qa+unassigned.
        await IngestDeployAsync(
            product,
            service: "svc-hasqa",
            referenceKey: "HASQA-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // No QA but has reviewer → keep under role=qa+unassigned (other roles don't matter
        // when the role filter is set).
        await IngestDeployAsync(
            product,
            service: "svc-noqa",
            referenceKey: "NOQA-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Other", email = "other@example.com" },
            });

        var result = await GetPendingAsync(role: "qa", assignee: "unassigned");
        Assert.Contains(result.Tickets, t => t.WorkItemKey == "NOQA-1");
        Assert.DoesNotContain(result.Tickets, t => t.WorkItemKey == "HASQA-1");
    }

    // ── 8. Response shape — assignees rollup + roles set ─────────────────────

    [Fact]
    public async Task ResponseShape_AssigneesRollup_DedupedPerEmailRole_WithCorrectCounts()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Test-scoped emails so the shared factory's residue from earlier tests doesn't
        // bleed into the rollup counts. The rollup is global across the user's authorized
        // list (intentionally — that's the spec), but the assertions here key off
        // unique-per-test emails to stay deterministic in xUnit's shared-fixture world.
        var scope = $"rollup-{Guid.NewGuid():N}"[..14];
        var aliceEmail = $"alice-{scope}@example.com";
        var bobEmail = $"bob-{scope}@example.com";

        // Alice is QA on two candidates → count=2, role=qa.
        await IngestDeployAsync(
            product,
            service: "svc-a1",
            referenceKey: "A1-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Alice", email = aliceEmail },
            });
        await IngestDeployAsync(
            product,
            service: "svc-a2",
            referenceKey: "A2-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Alice", email = aliceEmail },
            });

        // Alice is Reviewer on one candidate → count=1, role=reviewer.
        await IngestDeployAsync(
            product,
            service: "svc-a3",
            referenceKey: "A3-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Alice", email = aliceEmail },
            });

        // Bob is QA on one → count=1, role=qa.
        await IngestDeployAsync(
            product,
            service: "svc-b1",
            referenceKey: "B1-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Bob", email = bobEmail },
            });

        var unfiltered = await GetPendingAsync(role: null, assignee: null);

        var aliceQa = unfiltered.Assignees
            .FirstOrDefault(a => a.Email == aliceEmail && a.Role == "qa");
        Assert.NotNull(aliceQa);
        Assert.Equal(2, aliceQa!.Count);
        Assert.Equal("Alice", aliceQa.DisplayName);

        var aliceReviewer = unfiltered.Assignees
            .FirstOrDefault(a => a.Email == aliceEmail && a.Role == "reviewer");
        Assert.NotNull(aliceReviewer);
        Assert.Equal(1, aliceReviewer!.Count);

        var bobQa = unfiltered.Assignees
            .FirstOrDefault(a => a.Email == bobEmail && a.Role == "qa");
        Assert.NotNull(bobQa);
        Assert.Equal(1, bobQa!.Count);

        // Sort: count desc then displayName asc — Alice/qa (2) must come before Bob/qa (1).
        var aliceQaIdx = unfiltered.Assignees.FindIndex(a =>
            a.Email == aliceEmail && a.Role == "qa");
        var bobQaIdx = unfiltered.Assignees.FindIndex(a =>
            a.Email == bobEmail && a.Role == "qa");
        Assert.True(aliceQaIdx < bobQaIdx);
    }

    [Fact]
    public async Task ResponseShape_RollupBuiltAgainstUnfilteredAuthorizedList()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        var scope = $"pre-{Guid.NewGuid():N}"[..12];
        var aliceEmail = $"alice-{scope}@example.com";
        var bobEmail = $"bob-{scope}@example.com";

        await IngestDeployAsync(
            product,
            service: "svc-pre1",
            referenceKey: "PRE-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Alice", email = aliceEmail },
            });
        await IngestDeployAsync(
            product,
            service: "svc-pre2",
            referenceKey: "PRE-2",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Bob", email = bobEmail },
            });

        // Filter by Alice — Bob should still appear in the rollup since the rollup is
        // computed pre-narrowing.
        var filtered = await GetPendingAsync(role: null, assignee: aliceEmail);
        Assert.Contains(filtered.Assignees, a => a.Email == aliceEmail);
        Assert.Contains(filtered.Assignees, a => a.Email == bobEmail);
        // But the tickets list is narrowed.
        Assert.Contains(filtered.Tickets, t => t.WorkItemKey == "PRE-1");
        Assert.DoesNotContain(filtered.Tickets, t => t.WorkItemKey == "PRE-2");
    }

    [Fact]
    public async Task ResponseShape_RolesSet_DefaultMatchesConfigured()
    {
        // Default: ["qa","reviewer","assignee"].
        var defaultResult = await GetPendingAsync(role: null, assignee: null);
        Assert.Contains("qa", defaultResult.Roles);
        Assert.Contains("reviewer", defaultResult.Roles);
        Assert.Contains("assignee", defaultResult.Roles);

        // Override to ["qa"] — only qa should be present.
        await SetAssigneeRoleSettingAsync("[\"qa\"]");
        try
        {
            var overridden = await GetPendingAsync(role: null, assignee: null);
            Assert.Contains("qa", overridden.Roles);
            Assert.DoesNotContain("reviewer", overridden.Roles);
            Assert.DoesNotContain("assignee", overridden.Roles);
        }
        finally
        {
            await ResetAssigneeRoleSettingAsync();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"acme-{Guid.NewGuid():N}"[..18];

    private async Task<List<PendingTicketDto>> GetPendingAsync(string? assignee)
    {
        var result = await GetPendingAsync(role: null, assignee: assignee);
        return result.Tickets;
    }

    private async Task<PendingQueueResponse> GetPendingAsync(string? role, string? assignee)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(role)) query.Add($"role={Uri.EscapeDataString(role)}");
        if (!string.IsNullOrEmpty(assignee)) query.Add($"assignee={Uri.EscapeDataString(assignee)}");
        var url = query.Count == 0
            ? "/api/work-items/me/pending"
            : $"/api/work-items/me/pending?{string.Join("&", query)}";
        var resp = await _adminClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);

        var tickets = new List<PendingTicketDto>();
        foreach (var t in body.GetProperty("tickets").EnumerateArray())
        {
            tickets.Add(new PendingTicketDto(
                WorkItemKey: t.GetProperty("workItemKey").GetString()!,
                Product: t.GetProperty("product").GetString()!,
                TargetEnv: t.GetProperty("targetEnv").GetString()!));
        }

        var assignees = new List<PendingAssigneeDto>();
        if (body.TryGetProperty("assignees", out var assigneesEl))
        {
            foreach (var a in assigneesEl.EnumerateArray())
            {
                assignees.Add(new PendingAssigneeDto(
                    Email: a.GetProperty("email").GetString()!,
                    DisplayName: a.GetProperty("displayName").GetString()!,
                    Role: a.GetProperty("role").GetString()!,
                    Count: a.GetProperty("count").GetInt32()));
            }
        }

        var roles = new List<string>();
        if (body.TryGetProperty("roles", out var rolesEl))
        {
            foreach (var r in rolesEl.EnumerateArray())
            {
                roles.Add(r.GetString()!);
            }
        }

        return new PendingQueueResponse(tickets, assignees, roles);
    }

    private async Task<(Guid eventId, string candidateId)> IngestDeployAsync(
        string product,
        string service,
        string referenceKey,
        object[]? referenceParticipants)
    {
        var refsArr = referenceParticipants is null
            ? (object[])new[]
            {
                new { type = "work-item", provider = "jira", key = referenceKey, title = "Assignee filter test" },
            }
            : new[]
            {
                new
                {
                    type = "work-item",
                    provider = "jira",
                    key = referenceKey,
                    title = "Assignee filter test",
                    participants = referenceParticipants,
                },
            };

        var version = $"v{Guid.NewGuid():N}"[..10];
        var payload = new
        {
            product,
            service,
            environment = "staging",
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
            references = refsArr,
            // triggered-by is intentionally NOT in the default assignee role set, so it
            // doesn't trip the "assigned" check.
            participants = new[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        };

        var ingest = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", payload);
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        var ingestBody = await Deserialize(ingest);
        var eventId = Guid.Parse(ingestBody.GetProperty("id").GetString()!);

        var listResp = await _adminClient.GetAsync(
            $"/api/promotions/?product={product}&service={service}&targetEnv=prod&status=Pending");
        var list = await Deserialize(listResp);
        var candidate = FindCandidateByVersion(list.GetProperty("candidates"), version, "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;
        return (eventId, candidateId);
    }

    private static JsonElement? FindCandidateByVersion(JsonElement candidates, string version, string targetEnv)
    {
        foreach (var c in candidates.EnumerateArray())
        {
            if (c.GetProperty("version").GetString() == version
                && c.GetProperty("targetEnv").GetString() == targetEnv)
                return c;
        }
        return null;
    }

    private async Task SeedTopologyAsync()
    {
        await _adminClient.PutAsJsonAsync("/api/promotions/admin/topology", new
        {
            environments = new[] { "dev", "staging", "prod" },
            edges = new[]
            {
                new { from = "dev", to = "staging" },
                new { from = "staging", to = "prod" },
            },
        });
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
    }

    private async Task SeedTopologyAndPolicyAsync(string product)
    {
        await SeedTopologyAsync();
        // Per-product / per-target-env policy with no excluded role and InfraPortal.Admin
        // as the approver group — admin@localhost is in that group via the seeded local user.
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            targetEnv = "prod",
            approverGroup = "InfraPortal.Admin",
            strategy = "Any",
            minApprovers = 1,
            excludeRole = (string?)null,
            timeoutHours = 24,
            escalationGroup = (string?)null,
        });
    }

    /// <summary>
    /// Inserts (or updates) the <c>promotions.assignee_roles</c> setting directly via the DB
    /// — no admin endpoint exists for this setting on purpose. Mirrors the operator workflow
    /// documented on <see cref="PromotionAssigneeRoleSettings"/>.
    /// </summary>
    private async Task SetAssigneeRoleSettingAsync(string json)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var existing = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == PromotionAssigneeRoleSettings.SettingKey);
        if (existing is null)
        {
            db.PlatformSettings.Add(new PlatformSetting
            {
                Key = PromotionAssigneeRoleSettings.SettingKey,
                Value = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "test",
            });
        }
        else
        {
            existing.Value = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = "test";
        }
        await db.SaveChangesAsync();
    }

    private async Task ResetAssigneeRoleSettingAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var existing = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == PromotionAssigneeRoleSettings.SettingKey);
        if (existing is not null)
        {
            db.PlatformSettings.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

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

    private record PendingTicketDto(string WorkItemKey, string Product, string TargetEnv);
    private record PendingAssigneeDto(string Email, string DisplayName, string Role, int Count);
    private record PendingQueueResponse(
        List<PendingTicketDto> Tickets,
        List<PendingAssigneeDto> Assignees,
        List<string> Roles);

    // ── Factory ─────────────────────────────────────────────────────────────

    public class AssigneeFilterFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "assignee-filter-test-api-key-24680";

        private readonly SqliteConnection _connection;

        public AssigneeFilterFactory()
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

            builder.UseSetting("Deployments:ApiKeys:0:Name", "assignee-integration-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");

            builder.ConfigureServices(services =>
            {
                RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
                RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
                RemoveService<DbContextOptions<PlatformDbContext>>(services);
                RemoveService<PostgresPlatformDbContext>(services);
                RemoveService<SqlServerPlatformDbContext>(services);
                RemoveService<PlatformDbContext>(services);

                services.AddSingleton<DbConnection>(_connection);
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));
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
