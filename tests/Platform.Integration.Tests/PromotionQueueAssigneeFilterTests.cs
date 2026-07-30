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
        await SeedPolicyAsync(product);

        // Mine: I'm the QA on a reference participant.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-mine",
            referenceKey: "MINE-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        // Theirs: someone else is the QA, I'm not on the candidate.
        await CreatePromotionWithReferenceAsync(
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
    }

    // ── 2. assignee=<other email> returns their candidates, none of mine ──

    [Fact]
    public async Task AssigneeFilter_ByOtherEmail_ReturnsTheirCandidatesNotMine()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product);

        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-mine-2",
            referenceKey: "MINE2-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        await CreatePromotionWithReferenceAsync(
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
        await SeedPolicyAsync(product);

        // Has a QA participant — should NOT show up as unassigned.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-named",
            referenceKey: "NAMED-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // No participants in any assignee role (only triggered-by event-level participant
        // which is not in the default assignee role set).
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-empty",
            referenceKey: "EMPTY-1",
            referenceParticipants: null);

        var unassigned = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(unassigned, t => t.WorkItemKey == "EMPTY-1");
        Assert.DoesNotContain(unassigned, t => t.WorkItemKey == "NAMED-1");
    }

    // ── 4. Any role counts as an assignment ──

    [Fact]
    public async Task AssigneeFilter_AnyRole_CountsAsAssigned()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product);

        // A reporter is not the assignee, the QA, or the reviewer — but they are named on the item, so
        // it is assigned, it is theirs to find, and it must not read as "nobody assigned". Restricting
        // this to a privileged subset of roles hid items from the very people recorded against them.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-reporter-only",
            referenceKey: "REP-1",
            referenceParticipants: new[]
            {
                new { role = "reporter", displayName = "Other", email = "other@example.com" },
            });

        var unassigned = await GetPendingAsync(assignee: "unassigned");
        Assert.DoesNotContain(unassigned, t => t.WorkItemKey == "REP-1");

        // And the person is offered in the assignee rollup that backs the person dropdown.
        var all = await GetPendingAsync(role: null, assignee: null);
        Assert.Contains(all.Assignees, a => a.Email == "other@example.com" && a.Role == "reporter");
    }

    // Deleted AssigneeFilter_TombstonedAssignee_IsTreatedAsUnassigned: the pending-queue assignee
    // filter now sources participants from the self-contained candidate (reference-level +
    // promotion-level participants), not from deploy-event references merged with operator
    // overrides/tombstones (D19). Deploy-event reference overrides no longer feed the promotion
    // queue, so the tombstone-driven behaviour this asserted no longer exists in this path.

    // ── 6. Email match is case-insensitive ──

    [Fact]
    public async Task AssigneeFilter_EmailMatch_IsCaseInsensitive()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product);

        await CreatePromotionWithReferenceAsync(
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
        await SeedPolicyAsync(product);

        // QA: someone in role.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-qa",
            referenceKey: "RQA-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // Reviewer-only — under role=qa narrowing this row should drop.
        await CreatePromotionWithReferenceAsync(
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
        await SeedPolicyAsync(product);

        // I'm QA → match.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-iq",
            referenceKey: "IQ-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        // I'm Reviewer (not QA) → drop under role=qa+me.
        await CreatePromotionWithReferenceAsync(
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
        await SeedPolicyAsync(product);

        // Other is QA → match.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-oq",
            referenceKey: "OQ-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // Other is Reviewer (not QA) → drop under role=qa+other.
        await CreatePromotionWithReferenceAsync(
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
        await SeedPolicyAsync(product);

        // Has QA → drop under role=qa+unassigned.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-hasqa",
            referenceKey: "HASQA-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // No QA but has reviewer → keep under role=qa+unassigned (other roles don't matter
        // when the role filter is set).
        await CreatePromotionWithReferenceAsync(
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
        await SeedPolicyAsync(product);

        // Test-scoped emails so the shared factory's residue from earlier tests doesn't
        // bleed into the rollup counts. The rollup is global across the user's authorized
        // list (intentionally — that's the spec), but the assertions here key off
        // unique-per-test emails to stay deterministic in xUnit's shared-fixture world.
        var scope = $"rollup-{Guid.NewGuid():N}"[..14];
        var aliceEmail = $"alice-{scope}@example.com";
        var bobEmail = $"bob-{scope}@example.com";

        // Alice is QA on two candidates → count=2, role=qa.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-a1",
            referenceKey: "A1-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Alice", email = aliceEmail },
            });
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-a2",
            referenceKey: "A2-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Alice", email = aliceEmail },
            });

        // Alice is Reviewer on one candidate → count=1, role=reviewer.
        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-a3",
            referenceKey: "A3-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Alice", email = aliceEmail },
            });

        // Bob is QA on one → count=1, role=qa.
        await CreatePromotionWithReferenceAsync(
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
        await SeedPolicyAsync(product);

        var scope = $"pre-{Guid.NewGuid():N}"[..12];
        var aliceEmail = $"alice-{scope}@example.com";
        var bobEmail = $"bob-{scope}@example.com";

        await CreatePromotionWithReferenceAsync(
            product,
            service: "svc-pre1",
            referenceKey: "PRE-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Alice", email = aliceEmail },
            });
        await CreatePromotionWithReferenceAsync(
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

    // ── Scoping: the person is looked for on the work item, nowhere else ──

    [Fact]
    public async Task AssigneeFilter_IgnoresParticipantsOnOtherReferences()
    {
        // Regression: the filter used to match against every participant on the promotion —
        // reference-level people from commits and pull requests included — so "assigned to
        // <committer>" returned work items that person had never been put on. Only the work item's
        // own participants count.
        var product = NewProduct();
        await SeedPolicyAsync(product);

        var scope = $"xref-{Guid.NewGuid():N}"[..13];
        var committerEmail = $"committer-{scope}@example.com";

        await CreatePromotionAsync(product, service: "svc-xref", references: new object[]
        {
            // The work item itself carries nobody.
            new { type = "work-item", provider = "jira", key = "XREF-1", title = "Cross-reference bleed" },
            // A commit in the same build does — in an assignee role, so the old candidate-wide
            // match picked it up.
            new
            {
                type = "commit",
                provider = "gitlab",
                key = "abc1234def5678",
                title = "Fix the thing",
                participants = new[]
                {
                    new { role = "qa", displayName = "Committer", email = committerEmail },
                },
            },
        });

        var byCommitter = await GetPendingAsync(assignee: committerEmail);
        Assert.DoesNotContain(byCommitter, t => t.WorkItemKey == "XREF-1");

        // Nobody is on the item, so it is unassigned — the commit author must not disguise that.
        var unassigned = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(unassigned, t => t.WorkItemKey == "XREF-1");

        // And the rollup must not offer the committer as a narrowing choice that returns nothing.
        var unfiltered = await GetPendingAsync(role: null, assignee: null);
        Assert.Contains(unfiltered.Tickets, t => t.WorkItemKey == "XREF-1");
        Assert.DoesNotContain(unfiltered.Assignees, a => a.Email == committerEmail);
    }

    [Fact]
    public async Task AssigneeFilter_NarrowsPerWorkItem_NotPerPromotion()
    {
        // One promotion, two work items, a different QA on each. Narrowing has to return only the
        // matching item — the whole promotion's worth of items used to come through together.
        var product = NewProduct();
        await SeedPolicyAsync(product);

        var scope = $"peritem-{Guid.NewGuid():N}"[..16];
        var aliceEmail = $"alice-{scope}@example.com";
        var bobEmail = $"bob-{scope}@example.com";

        await CreatePromotionAsync(product, service: "svc-per-item", references: new object[]
        {
            new
            {
                type = "work-item",
                provider = "jira",
                key = "PERITEM-A",
                title = "Alice's item",
                participants = new[]
                {
                    new { role = "qa", displayName = "Alice", email = aliceEmail },
                },
            },
            new
            {
                type = "work-item",
                provider = "jira",
                key = "PERITEM-B",
                title = "Bob's item",
                participants = new[]
                {
                    new { role = "qa", displayName = "Bob", email = bobEmail },
                },
            },
        });

        var alices = await GetPendingAsync(assignee: aliceEmail);
        Assert.Contains(alices, t => t.WorkItemKey == "PERITEM-A");
        Assert.DoesNotContain(alices, t => t.WorkItemKey == "PERITEM-B");

        var bobs = await GetPendingAsync(assignee: bobEmail);
        Assert.Contains(bobs, t => t.WorkItemKey == "PERITEM-B");
        Assert.DoesNotContain(bobs, t => t.WorkItemKey == "PERITEM-A");

        // Both are on one promotion, so each person's rollup entry counts one work item, not two.
        var unfiltered = await GetPendingAsync(role: null, assignee: null);
        var aliceQa = unfiltered.Assignees.FirstOrDefault(a => a.Email == aliceEmail && a.Role == "qa");
        Assert.NotNull(aliceQa);
        Assert.Equal(1, aliceQa!.Count);
    }

    [Fact]
    public async Task ResponseShape_RolesSet_IsTheConfiguredVocabulary()
    {
        // `roles` is the role dropdown's contents: the configured participant roles (Settings →
        // Participant Roles), independent of which roles the queue's items happen to carry — the
        // question "which items have nobody as QA owner?" is about a role that may appear nowhere.
        // See WorkItemRoleVocabularyTests for the vocabulary's own coverage.
        var result = await GetPendingAsync(role: null, assignee: null);
        Assert.Contains("qa", result.Roles);
        Assert.Contains("reviewer", result.Roles);
        Assert.Contains("author", result.Roles);
        Assert.Contains("qa-owner", result.Roles);
        Assert.Contains("assignee", result.Roles);
        Assert.Contains("reporter", result.Roles);
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

    // Create a Pending staging→prod promotion candidate carrying a single work-item reference.
    // The pending-queue assignee filter sources its per-ticket participants from the candidate's
    // own data now (reference-level nested participants + promotion-level participants) — no deploy
    // event, no overrides (D19). Returns the new candidate id.
    private async Task<string> CreatePromotionWithReferenceAsync(
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

        return await CreatePromotionAsync(product, service, refsArr);
    }

    // Create a Pending staging→prod promotion carrying an arbitrary reference set. Lets a test put
    // people on references that are NOT the work item (commits, pull requests), or several work items
    // with different people on a single promotion — both cases the person/role filter has to keep
    // apart. Returns the new candidate id.
    private async Task<string> CreatePromotionAsync(string product, string service, object[] references)
    {
        var version = $"v{Guid.NewGuid():N}"[..10];

        // Source validation requires a succeeded deploy of this version in the source env (staging).
        await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment = "staging",
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
        });

        var payload = new
        {
            product,
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version,
            references,
            // triggered-by is intentionally NOT in the default assignee role set, so it
            // doesn't trip the "assigned" check.
            participants = new[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        };

        var create = await _apiKeyClient.PostAsJsonAsync("/api/promotions", payload);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var createBody = await Deserialize(create);
        return createBody.GetProperty("id").GetString()!;
    }

    // Topology was removed (D19): policy resolution is the edge guard. Enable the flag and seed a
    // per-product gated step-tree policy (one InfraPortal.Admin approver) so created candidates are
    // born Pending and land in the approval queue.
    private async Task SeedPolicyAsync(string product)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
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
