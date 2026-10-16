using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;
using static Platform.Integration.Tests.WorkItemApprovalTests;

namespace Platform.Integration.Tests;

/// <summary>
/// Paging of the decided view — <c>GET /api/work-items/me/pending?status=decided</c>. The view used
/// to return every decision in the window in one response; it now returns <c>limit</c> rows at a time
/// with a keyset cursor on <c>(CreatedAt DESC, Id DESC)</c>. These pin that walking the pages gives
/// back exactly the one-shot list — same rows, same order, nothing twice, nothing skipped, ties on
/// the decision time included — that a cursor holds its place while decisions land above it, and the
/// limit's default and bounds.
/// </summary>
public class WorkItemDecidedPagingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pages_ConcatenateToTheWholeWindow_InOrder_WithTiesAcrossPageBoundaries()
    {
        await using var factory = NewFactory();
        // 20 decisions in groups of three sharing a timestamp, so most page sizes split a tie. Every
        // fourth ticket rides on a candidate, so rows render a carrier as well as the bare decision.
        var seeded = await SeedAsync(factory, 20, i => T0.AddMinutes(-(i / 3)), carriedEvery: 4);

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();

        var whole = await svc.GetDecidedAsync(decision: null, since: null, limit: WorkItemApprovalService.DecidedPageSizeMax);
        Assert.Null(whole.NextCursor);
        Assert.Equal(20, whole.Total);
        // Newest first, as before; within a tie the id decides, so the order is total.
        Assert.Equal(
            seeded.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Select(a => a.WorkItemKey),
            whole.Tickets.Select(t => t.WorkItemKey));
        Assert.Contains(whole.Tickets, t => t.CandidateId != Guid.Empty);

        foreach (var size in new[] { 1, 2, 3, 4, 6, 7, 19, 20, 21 })
        {
            var pages = await WalkAsync(svc, size);
            Assert.All(pages.SkipLast(1), p => Assert.Equal(size, p.Tickets.Count));
            Assert.All(pages.SkipLast(1), p => Assert.NotNull(p.NextCursor));
            Assert.Null(pages[^1].NextCursor);
            // The rollup and total describe the window, so every page reports the same ones.
            Assert.All(pages, p => Assert.Equal(20, p.Total));
            Assert.All(pages, p => Assert.Equal(whole.Assignees, p.Assignees));
            Assert.Equal(whole.Tickets.Select(Sig), pages.SelectMany(p => p.Tickets).Select(Sig));
        }
    }

    [Fact]
    public async Task Cursor_HoldsItsPlace_WhenDecisionsArriveBetweenPages()
    {
        await using var factory = NewFactory();
        await SeedAsync(factory, 10, i => T0.AddMinutes(-i));

        DecidedQueueResult first;
        List<string> before;
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            before = (await svc.GetDecidedAsync(decision: null, since: null, limit: 500)).Tickets.Select(t => t.WorkItemKey).ToList();
            first = await svc.GetDecidedAsync(decision: null, since: null, limit: 4);
        }

        // Between the pages: three new decisions (newer than everything, as a new decision always
        // is) and a change of mind on a row the next page holds.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.WorkItemApprovals.AddRange(
                Decision("NEW-1", "alice@example.com", T0.AddMinutes(1)),
                Decision("NEW-2", "alice@example.com", T0.AddMinutes(2)),
                Decision("NEW-3", "bob@example.com", T0.AddMinutes(3)));
            var changed = await db.WorkItemApprovals.SingleAsync(a => a.WorkItemKey == before[5]);
            changed.Decision = WorkItemDecision.Blocked;
            changed.UpdatedAt = T0.AddMinutes(4);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var rest = new List<PendingTicketView>();
            var cursor = first.NextCursor;
            while (cursor is { } c)
            {
                var page = await svc.GetDecidedAsync(decision: null, since: null, limit: 4, after: c);
                rest.AddRange(page.Tickets);
                Assert.Equal(13, page.Total);
                cursor = page.NextCursor;
            }

            // The rows after the cursor are the ones that were after it: nothing new, nothing repeated.
            Assert.Equal(before, first.Tickets.Concat(rest).Select(t => t.WorkItemKey));
            // The change of mind stays where the decision was first made, with its new value.
            Assert.Equal("Blocked", rest.Single(t => t.WorkItemKey == before[5]).Decision);

            // The new decisions are on the next first page.
            var fresh = await svc.GetDecidedAsync(decision: null, since: null, limit: 4);
            Assert.Equal(new[] { "NEW-3", "NEW-2", "NEW-1", before[0] }, fresh.Tickets.Select(t => t.WorkItemKey));
        }
    }

    [Fact]
    public async Task DecidedBy_PagesThatDeciderOnly_AcrossSpellingsOfTheEmail()
    {
        await using var factory = NewFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            for (var i = 0; i < 7; i++)
            {
                // Alice's email arrives spelled two ways; the narrowing has always been case-insensitive.
                var alice = i % 2 == 0 ? "alice@example.com" : "Alice@Example.com";
                db.WorkItemApprovals.Add(Decision($"AL-{i}", alice, T0.AddMinutes(-2 * i), name: "Alice"));
                db.WorkItemApprovals.Add(Decision($"BO-{i}", "bob@example.com", T0.AddMinutes(-2 * i - 1), name: "Bob"));
            }
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var pages = await WalkAsync(svc, 3, decidedBy: "ALICE@example.com");

            Assert.Equal(3, pages.Count);
            Assert.Equal(Enumerable.Range(0, 7).Select(i => $"AL-{i}"),
                pages.SelectMany(p => p.Tickets).Select(t => t.WorkItemKey));
            Assert.All(pages, p => Assert.Equal(7, p.Total));
            // The rollup is the window's, before the narrowing — Bob is still offered — and the two
            // spellings of Alice's email are one person, named as most recently spelled.
            Assert.All(pages, p => Assert.Equal(
                new[] { "alice@example.com|Alice|7", "bob@example.com|Bob|7" },
                p.Assignees.Select(a => $"{a.Email}|{a.DisplayName}|{a.Count}").OrderBy(s => s, StringComparer.Ordinal)));

            var nobody = await svc.GetDecidedAsync(decision: null, since: null, decidedBy: "carol@example.com");
            Assert.Empty(nobody.Tickets);
            Assert.Equal(0, nobody.Total);
            Assert.Null(nobody.NextCursor);
            Assert.Equal(2, nobody.Assignees.Count);
        }
    }

    [Fact]
    public async Task Endpoint_Limit_DefaultsTo100_IsClampedTo1Through500_AndTheCursorRoundTrips()
    {
        await using var factory = NewFactory();
        await SeedAsync(factory, 501, i => T0.AddSeconds(-i));
        var client = factory.CreateAdminClient();

        // No limit: the first 100, and how to get the rest.
        var byDefault = await GetAsync(client, "status=decided");
        Assert.Equal(100, byDefault.GetProperty("tickets").GetArrayLength());
        Assert.True(byDefault.GetProperty("hasMore").GetBoolean());
        Assert.Equal(501, byDefault.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.String, byDefault.GetProperty("nextCursor").ValueKind);
        Assert.Equal(JsonValueKind.Array, byDefault.GetProperty("assignees").ValueKind);

        Assert.Equal(500, (await GetAsync(client, "status=decided&limit=10000")).GetProperty("tickets").GetArrayLength());
        Assert.Equal(1, (await GetAsync(client, "status=decided&limit=0")).GetProperty("tickets").GetArrayLength());
        Assert.Equal(1, (await GetAsync(client, "status=decided&limit=-3")).GetProperty("tickets").GetArrayLength());

        // The cursor is what a page hands out, passed back as is.
        var cursor = byDefault.GetProperty("nextCursor").GetString()!;
        var second = await GetAsync(client, $"status=decided&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal("G-100", second.GetProperty("tickets")[0].GetProperty("workItemKey").GetString());

        var page500 = await GetAsync(client, "status=decided&limit=500");
        var last = await GetAsync(client,
            $"status=decided&limit=500&cursor={Uri.EscapeDataString(page500.GetProperty("nextCursor").GetString()!)}");
        Assert.Equal("G-500", Assert.Single(last.GetProperty("tickets").EnumerateArray()).GetProperty("workItemKey").GetString());
        Assert.False(last.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);

        // Anything else is refused rather than read as "from the top".
        foreach (var bad in new[] { "nope", "bm9wZQ", Uri.EscapeDataString(cursor[..^2]) })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"/api/work-items/me/pending?status=decided&cursor={bad}")).StatusCode);

        // The pending views are not paged, and their response shape is unchanged.
        var pending = await GetAsync(client, "");
        Assert.Equal(new[] { "assignees", "tickets" },
            pending.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkItemTestFactory NewFactory()
    {
        var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.Name = "QA";
        factory.Current.RolesList = new() { "InfraPortal.QA" };
        return factory;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> decisions <c>G-0</c>… with ids in seeding order and times from
    /// <paramref name="at"/>, by two deciders. Every <paramref name="carriedEvery"/>-th ticket gets a
    /// candidate carrying it; the rest render as decisions with no carrier.
    /// </summary>
    private static async Task<List<WorkItemApproval>> SeedAsync(
        WorkItemTestFactory factory, int count, Func<int, DateTimeOffset> at, int carriedEvery = 0)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var seeded = new List<WorkItemApproval>();
        for (var i = 0; i < count; i++)
        {
            var key = $"G-{i}";
            var decision = Decision(key, i % 3 == 0 ? "bob@example.com" : "alice@example.com", at(i));
            decision.Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}");
            seeded.Add(decision);
            if (carriedEvery > 0 && i % carriedEvery == 0)
            {
                var candidate = new PromotionCandidate
                {
                    Id = Guid.Parse($"00000000-0000-0000-0001-{i + 1:D12}"),
                    Product = "acme", Service = "api", SourceEnv = "staging", TargetEnv = "prod",
                    Version = $"v{i}", Status = PromotionStatus.Deployed, CreatedAt = at(i).AddHours(-1),
                };
                db.PromotionCandidates.Add(candidate);
                db.PromotionWorkItems.Add(new PromotionWorkItem
                {
                    Id = Guid.Parse($"00000000-0000-0000-0002-{i + 1:D12}"),
                    CandidateId = candidate.Id, WorkItemKey = key,
                    Product = "acme", Service = "api", TargetEnv = "prod",
                    Provider = "jira", Title = $"Ticket {i}", CreatedAt = candidate.CreatedAt,
                });
            }
        }
        db.WorkItemApprovals.AddRange(seeded);
        await db.SaveChangesAsync();
        return seeded;
    }

    private static WorkItemApproval Decision(string key, string email, DateTimeOffset at, string? name = null)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkItemKey = key,
            Product = "acme",
            Service = "api",
            TargetEnv = "prod",
            ApproverEmail = email,
            ApproverName = name ?? email.Split('@')[0],
            Decision = WorkItemDecision.Approved,
            CreatedAt = at,
        };

    /// <summary>Every page of the decided view at <paramref name="size"/> rows a page, first to last.</summary>
    private static async Task<List<DecidedQueueResult>> WalkAsync(
        WorkItemApprovalService svc, int size, string? decidedBy = null)
    {
        var pages = new List<DecidedQueueResult>();
        DecidedCursor? cursor = null;
        do
        {
            var page = await svc.GetDecidedAsync(decision: null, since: null, decidedBy, size, cursor);
            pages.Add(page);
            cursor = page.NextCursor;
            Assert.True(pages.Count <= 1000, "paging did not terminate");
        } while (cursor is not null);
        return pages;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/work-items/me/pending" + (query.Length == 0 ? "" : "?" + query));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>Everything a decided row shows that could differ between two reads of it.</summary>
    private static string Sig(PendingTicketView t)
        => $"{t.WorkItemKey}/{t.Product}/{t.Service}/{t.TargetEnv} {t.CandidateId} {t.Version} {t.CandidateStatus} "
           + $"{t.Title} {t.Decision} {t.DecidedAt:O} {t.DecidedByEmail} {t.DecidedByName} {t.Overall?.State}";
}
