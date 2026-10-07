using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Users.Models;
using Platform.Api.Infrastructure.Persistence;
using static Platform.Integration.Tests.WorkItemApprovalTests;

namespace Platform.Integration.Tests;

/// <summary>
/// Pins what the work-item queue reads render — <c>GET /api/work-items/me/pending</c> in both its
/// pending and its <c>status=decided</c> views — over one deterministic data set that exercises every
/// branch at once: live and dead (orphan) carriers, a ticket shared by two promotions of one service
/// and carried by a second service, auto-approve edges, a hidden product, a retired service, decisions
/// by the caller and by others, reference- and promotion-level participants, policy-required roles and
/// deployed environments.
///
/// <para>The queue reads load only the columns a row renders and look up just the decisions on the
/// work items in play. These tests were written against the implementation that loaded whole rows, so
/// they hold the narrower reads to exactly the same rows, order and fields.</para>
/// </summary>
public class WorkItemQueueReadTests
{
    private const string Me = "qa@example.com";
    private const string Other = "other@example.com";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Pending carriers.
    private static readonly Guid ApiLatest = Id("a1");     // acme/api v3, staging→prod — owns A-1 and A-2
    private static readonly Guid ApiUat = Id("a2");        // acme/api v3, uat→prod — a second edge carrying A-1
    private static readonly Guid Web = Id("a3");           // acme/web w7 — A-1 on another service
    private static readonly Guid BatchAuto = Id("a4");     // auto-approve — no sign-off work
    private static readonly Guid Hidden = Id("a5");        // a product the caller hides
    private static readonly Guid Retired = Id("a6");       // a retired service
    private static readonly Guid MobileDecided = Id("a7"); // every ticket already decided by the caller

    // Dead carriers (the orphan scan).
    private static readonly Guid ApiSuperseded = Id("b1"); // acme/api v2 — A-1 (claimed), A-3 (orphan), A-4 (approved)
    private static readonly Guid WorkerRejected = Id("b2"); // acme/worker k1 — C-1 (issue by other), C-2 (mine)
    private static readonly Guid ApiOldest = Id("b3");     // acme/api v1 — an older copy of A-3
    private static readonly Guid BatchAutoDead = Id("b4"); // auto-approve, superseded
    private static readonly Guid RetiredDead = Id("b5");   // retired service, rejected
    private static readonly Guid ApiDeployed = Id("b6");   // shipped — neither pending nor orphan

    [Fact]
    public async Task Pending_RendersLiveRowsThenOrphans_WithEveryField()
    {
        await using var factory = await SeededFactoryAsync();

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        var queue = await svc.GetPendingForCurrentUserAsync(default);

        // Live candidates first (newest first), then the dead ones; a ticket renders once per service,
        // from the newest carrier that still needs it.
        Assert.Equal(new[]
        {
            "A-1/api@v3 Pending x2",
            "A-2/api@v3 Pending x1",
            "A-1/web@w7 Pending x1",
            "A-3/api@v2 Superseded x1",
            "C-1/worker@k1 Rejected x1",
        }, queue.Tickets.Select(Sig));

        var a1 = queue.Tickets[0];
        Assert.Equal(ApiLatest, a1.CandidateId);
        Assert.Equal("acme", a1.Product);
        Assert.Equal("prod", a1.TargetEnv);
        Assert.Equal("jira", a1.Provider);
        Assert.Equal("https://jira.example.com/browse/A-1", a1.Url);
        Assert.Equal("A one", a1.Title);
        Assert.Equal("fix login", a1.SubTitle);
        Assert.Equal("High", a1.Priority);
        Assert.Equal("Bug", a1.WorkItemType);
        Assert.Equal("qa-owner:alice@example.com,triggered-by:bob@example.com,assignee:dana@example.com",
            People(a1));
        Assert.Equal(new[] { "qa-owner" }, a1.RequiredRoles);
        Assert.Empty(a1.MissingRoles!);
        Assert.Equal(new[] { "dev@v3", "staging@v3" }, Envs(a1));
        Assert.Equal(T0.AddHours(-1), a1.Environments[0].DeployedAt);
        Assert.Equal("Pending 2 (a1 i0 b0 p1) api:Approved:A one,web:Pending:A one (web)", Overall(a1));
        Assert.Null(a1.Decision);
        Assert.Null(a1.DecidedByEmail);

        var a2 = queue.Tickets[1];
        Assert.Equal(ApiLatest, a2.CandidateId);
        Assert.Equal("A two", a2.Title);
        Assert.Null(a2.SubTitle);
        Assert.Null(a2.Priority);
        Assert.Null(a2.WorkItemType);
        Assert.Equal("reporter:rita@example.com,triggered-by:bob@example.com,assignee:dana@example.com",
            People(a2));
        // Nobody holds qa-owner, but somebody has ruled on the item, so nothing is asked for.
        Assert.Equal(new[] { "qa-owner" }, a2.RequiredRoles);
        Assert.Empty(a2.MissingRoles!);
        Assert.Equal("Blocked 1 (a0 i0 b1 p0) api:Blocked:A two", Overall(a2));

        var web = queue.Tickets[2];
        Assert.Equal(Web, web.CandidateId);
        Assert.Equal("A one (web)", web.Title);
        Assert.Equal("", People(web));
        Assert.Empty(web.RequiredRoles!);
        Assert.Empty(web.MissingRoles!);
        Assert.Equal(new[] { "staging@w7" }, Envs(web));
        Assert.Equal(Overall(a1), Overall(web));

        var a3 = queue.Tickets[3];
        Assert.Equal(ApiSuperseded, a3.CandidateId);
        Assert.Equal("A three", a3.Title);
        Assert.Equal("assignee:dana@example.com", People(a3));
        Assert.Equal(new[] { "qa-owner" }, a3.MissingRoles);
        Assert.Equal(new[] { "qa@v2" }, Envs(a3));
        Assert.Equal("Pending 1 (a0 i0 b0 p1) api:Pending:A three", Overall(a3));

        var c1 = queue.Tickets[4];
        Assert.Equal(WorkerRejected, c1.CandidateId);
        Assert.Equal("qa-owner:alice@example.com", People(c1));
        Assert.Empty(c1.MissingRoles!);
        Assert.Equal(new[] { "staging@k1" }, Envs(c1));
        Assert.Equal("Issue 1 (a0 i1 b0 p0) worker:Issue:C one", Overall(c1));

        // Only holders of a policy-required role, counted once per rendered row. Eve is qa-owner on
        // the uat edge's copy of A-1, which the newer promotion owns — so she is not offered.
        Assert.Equal(new[] { "alice@example.com|Alice|qa-owner|2" }, queue.Assignees.Select(Sig));
    }

    [Fact]
    public async Task Pending_PersonAndRoleNarrowings_FilterTheSameRows_AndKeepTheFullRollup()
    {
        await using var factory = await SeededFactoryAsync();

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();

        async Task<string[]> Keys(string? assignee, WorkItemRoleRequirementFilter role = WorkItemRoleRequirementFilter.Any)
        {
            var queue = await svc.GetPendingForCurrentUserAsync(default, assignee, role);
            // The rollup is computed before narrowing, so every narrowing reports the same one.
            Assert.Equal(new[] { "alice@example.com|Alice|qa-owner|2" }, queue.Assignees.Select(Sig));
            return queue.Tickets.Select(t => $"{t.WorkItemKey}/{t.Service}").ToArray();
        }

        Assert.Equal(new[] { "A-1/api", "C-1/worker" }, await Keys("ALICE@example.com"));
        Assert.Equal(new[] { "A-1/api", "A-2/api", "A-3/api" }, await Keys("dana@example.com"));
        // triggered-by records the pipeline run, not an assignment.
        Assert.Empty(await Keys("bob@example.com"));
        Assert.Equal(new[] { "A-1/web" }, await Keys("unassigned"));
        Assert.Equal(new[] { "A-1/api", "C-1/worker" }, await Keys(null, WorkItemRoleRequirementFilter.Assigned));
        Assert.Empty(await Keys("dana@example.com", WorkItemRoleRequirementFilter.Assigned));
        Assert.Equal(new[] { "A-3/api" }, await Keys(null, WorkItemRoleRequirementFilter.Missing));
    }

    [Fact]
    public async Task Pending_CallerWithoutQaOrAdmin_GetsAnEmptyQueue()
    {
        await using var factory = await SeededFactoryAsync();
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        var queue = await svc.GetPendingForCurrentUserAsync(default);

        Assert.Empty(queue.Tickets);
        Assert.Empty(queue.Assignees);
    }

    [Fact]
    public async Task Decided_RendersEachDecisionAgainstItsNewestCarrier()
    {
        await using var factory = await SeededFactoryAsync();

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        var decided = await svc.GetDecidedAsync(decision: null, since: null);

        // Newest decision first; the hidden product's decision is left out.
        Assert.Equal(new[]
        {
            "A-1/api@v3 Pending x0",
            "A-2/api@v3 Pending x0",
            "M-1/mobile@m1 Pending x0",
            "A-4/api@v2 Superseded x0",
            "C-1/worker@k1 Rejected x0",
            "C-2/worker@k1 Rejected x0",
            "A-5/api@v0 Deployed x0",
            "GHOST-1/api@ Unknown x0",
        }, decided.Tickets.Select(Sig));

        var a1 = decided.Tickets[0];
        // Three candidates carry api/A-1; the newest one supplies the row.
        Assert.Equal(ApiLatest, a1.CandidateId);
        Assert.Equal("A one", a1.Title);
        Assert.Equal("fix login", a1.SubTitle);
        Assert.Equal("High", a1.Priority);
        Assert.Equal("Bug", a1.WorkItemType);
        Assert.Equal("https://jira.example.com/browse/A-1", a1.Url);
        Assert.Equal("jira", a1.Provider);
        Assert.Equal("qa-owner:alice@example.com,triggered-by:bob@example.com,assignee:dana@example.com",
            People(a1));
        Assert.Equal(new[] { "qa-owner" }, a1.RequiredRoles);
        Assert.Empty(a1.MissingRoles!);
        Assert.Equal(new[] { "dev@v3", "staging@v3" }, Envs(a1));
        Assert.Equal("Approved", a1.Decision);
        Assert.Equal(T0.AddMinutes(-10), a1.DecidedAt);
        Assert.Equal(Other, a1.DecidedByEmail);
        Assert.Equal("Other", a1.DecidedByName);
        Assert.Equal("ship it", a1.DecisionComment);
        Assert.Equal("Pending 2 (a1 i0 b0 p1) api:Approved:A one,web:Pending:A one (web)", Overall(a1));

        var a4 = decided.Tickets[3];
        Assert.Equal(ApiSuperseded, a4.CandidateId);
        Assert.Equal("A four", a4.Title);
        Assert.Equal("assignee:dana@example.com", People(a4));
        Assert.Equal(new[] { "qa@v2" }, Envs(a4));

        var a5 = decided.Tickets[6];
        Assert.Equal(ApiDeployed, a5.CandidateId);
        Assert.Equal("qa-owner:alice@example.com", People(a5));
        Assert.Empty(Envs(a5));

        var ghost = decided.Tickets[7];
        Assert.Equal(Guid.Empty, ghost.CandidateId);
        Assert.Null(ghost.Title);
        Assert.Null(ghost.Url);
        Assert.Empty(ghost.Participants);
        Assert.Empty(ghost.RequiredRoles!);
        Assert.Empty(ghost.MissingRoles!);
        Assert.Empty(ghost.Environments);
        Assert.Null(ghost.Overall);

        Assert.Equal(new[]
        {
            "other@example.com|Other|||4",
            "qa@example.com|QA Me|||2",
            "alice@example.com|Alice|||1",
            "zed@example.com|zed@example.com|||1",
        }, decided.Assignees.Select(a => $"{a.Email}|{a.DisplayName}||{a.Role}|{a.Count}"));

        // Narrowing by decider keeps the full rollup.
        var alice = await svc.GetDecidedAsync(decision: null, since: null, decidedBy: "ALICE@example.com");
        Assert.Equal(new[] { "A-5/api@v0 Deployed x0" }, alice.Tickets.Select(Sig));
        Assert.Equal(decided.Assignees, alice.Assignees);

        var recent = await svc.GetDecidedAsync(decision: null, since: T0.AddHours(-1));
        Assert.Equal(new[] { "A-1/api", "A-2/api", "M-1/mobile" },
            recent.Tickets.Select(t => $"{t.WorkItemKey}/{t.Service}"));

        var blocked = await svc.GetDecidedAsync(decision: WorkItemDecision.Blocked, since: null);
        Assert.Equal(new[] { "A-2/api", "C-2/worker" }, blocked.Tickets.Select(t => $"{t.WorkItemKey}/{t.Service}"));
    }

    // ── Data set ──────────────────────────────────────────────────────────────

    private static async Task<WorkItemTestFactory> SeededFactoryAsync()
    {
        var factory = new WorkItemTestFactory();
        factory.Current.Email = Me;
        factory.Current.Name = "QA Me";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var gated = Snapshot(gated: true, "qa-owner");
        var gatedNoRoles = Snapshot(gated: true);
        var auto = Snapshot(gated: false);

        var alice = new ParticipantDto("qa-owner", "Alice", "alice@example.com");
        var bob = new PromotionParticipant("triggered-by", "Bob", "bob@example.com");
        var dana = new PromotionParticipant("assignee", "Dana", "dana@example.com");
        // Ticket bodies ride on the references and the work-item rows; nothing in the queue reads them.
        var body = new string('x', 20_000);

        var n = 0;
        void Add(PromotionCandidate c, params (string Key, string? Title, string? SubTitle, string? Priority, string? Type)[] items)
        {
            db.PromotionCandidates.Add(c);
            foreach (var (key, title, subTitle, priority, type) in items)
            {
                db.PromotionWorkItems.Add(new PromotionWorkItem
                {
                    Id = Guid.Parse($"00000000-0000-0000-0001-{++n:D12}"),
                    CandidateId = c.Id,
                    WorkItemKey = key,
                    Product = c.Product,
                    Service = c.Service,
                    TargetEnv = c.TargetEnv,
                    Provider = "jira",
                    Url = $"https://jira.example.com/browse/{key}",
                    Title = title,
                    SubTitle = subTitle,
                    Content = body,
                    Priority = priority,
                    WorkItemType = type,
                    CreatedAt = c.CreatedAt,
                });
            }
        }

        Add(Candidate(ApiLatest, PromotionStatus.Pending, "api", "v3", TimeSpan.FromHours(1), gated,
                new[]
                {
                    WorkItemRef("A-1", body, alice),
                    WorkItemRef("A-2", body, new ParticipantDto("reporter", "Rita", "rita@example.com")),
                    new ReferenceDto("commit", Key: "abc123", Title: "fix login", Content: body,
                        Participants: new[] { new ParticipantDto("author", "Carl", "carl@example.com") }),
                },
                participants: new[] { bob, dana }),
            ("A-1", "A one", "fix login", "High", "Bug"),
            ("A-2", "A two", null, null, null));
        Add(Candidate(ApiUat, PromotionStatus.Pending, "api", "v3", TimeSpan.FromHours(2), gated,
                new[] { WorkItemRef("A-1", body, new ParticipantDto("qa-owner", "Eve", "eve@example.com")) },
                sourceEnv: "uat"),
            ("A-1", "A one (uat)", null, "Low", "Story"));
        Add(Candidate(Web, PromotionStatus.Pending, "web", "w7", TimeSpan.FromHours(3), gatedNoRoles,
                new[] { WorkItemRef("A-1", body) }),
            ("A-1", "A one (web)", null, null, null));
        Add(Candidate(BatchAuto, PromotionStatus.Pending, "batch", "b1", TimeSpan.FromMinutes(30), auto,
                new[] { WorkItemRef("B-1", body) }),
            ("B-1", "B one", null, null, null));
        Add(Candidate(Hidden, PromotionStatus.Pending, "api", "h1", TimeSpan.FromMinutes(10), gated,
                new[] { WorkItemRef("H-1", body, alice) }, product: "hidden-prod"),
            ("H-1", "H one", null, null, null));
        Add(Candidate(Retired, PromotionStatus.Pending, "retired", "r1", TimeSpan.FromMinutes(15), gated,
                new[] { WorkItemRef("R-1", body, alice) }),
            ("R-1", "R one", null, null, null));
        Add(Candidate(MobileDecided, PromotionStatus.Pending, "mobile", "m1", TimeSpan.FromMinutes(45), gated,
                new[] { WorkItemRef("M-1", body, alice) }),
            ("M-1", "M one", null, null, null));

        Add(Candidate(ApiSuperseded, PromotionStatus.Superseded, "api", "v2", TimeSpan.FromHours(5), gated,
                new[]
                {
                    WorkItemRef("A-1", body, new ParticipantDto("qa-owner", "Old", "oldqa@example.com")),
                    WorkItemRef("A-3", body),
                    WorkItemRef("A-4", body),
                },
                participants: new[] { dana }),
            ("A-1", "A one (old)", null, null, null),
            ("A-3", "A three", null, null, null),
            ("A-4", "A four", null, null, null));
        Add(Candidate(WorkerRejected, PromotionStatus.Rejected, "worker", "k1", TimeSpan.FromHours(6), gated,
                new[] { WorkItemRef("C-1", body, alice), WorkItemRef("C-2", body) }),
            ("C-1", "C one", null, null, null),
            ("C-2", "C two", null, null, null));
        Add(Candidate(ApiOldest, PromotionStatus.Superseded, "api", "v1", TimeSpan.FromHours(8), gated,
                new[] { WorkItemRef("A-3", body, new ParticipantDto("qa-owner", "Zed", "zed@example.com")) }),
            ("A-3", "A three (oldest)", null, null, null));
        Add(Candidate(BatchAutoDead, PromotionStatus.Superseded, "batch", "b0", TimeSpan.FromHours(7), auto,
                new[] { WorkItemRef("D-1", body) }),
            ("D-1", "D one", null, null, null));
        Add(Candidate(RetiredDead, PromotionStatus.Rejected, "retired", "r0", TimeSpan.FromHours(9), gated,
                new[] { WorkItemRef("R-0", body) }),
            ("R-0", "R zero", null, null, null));
        Add(Candidate(ApiDeployed, PromotionStatus.Deployed, "api", "v0", TimeSpan.FromHours(20), gated,
                new[] { WorkItemRef("A-5", body, alice) }),
            ("A-5", "A five", null, null, null));

        db.WorkItemApprovals.AddRange(
            Decision("A-1", "api", Other, "Other", WorkItemDecision.Approved, TimeSpan.FromMinutes(10), "ship it"),
            Decision("A-2", "api", Other, "Other", WorkItemDecision.Blocked, TimeSpan.FromMinutes(20)),
            Decision("M-1", "mobile", Me, "QA Me", WorkItemDecision.Approved, TimeSpan.FromMinutes(30)),
            Decision("A-4", "api", Other, "Other", WorkItemDecision.Approved, TimeSpan.FromHours(2)),
            Decision("C-1", "worker", Other, "Other", WorkItemDecision.Issue, TimeSpan.FromHours(3)),
            Decision("C-2", "worker", Me, "QA Me", WorkItemDecision.Blocked, TimeSpan.FromHours(4)),
            Decision("A-5", "api", "alice@example.com", "Alice", WorkItemDecision.Approved, TimeSpan.FromDays(1)),
            Decision("GHOST-1", "api", "zed@example.com", "", WorkItemDecision.Approved, TimeSpan.FromDays(2)),
            Decision("H-1", "api", Other, "Other", WorkItemDecision.Approved, TimeSpan.FromMinutes(5),
                product: "hidden-prod"));

        db.DeployEvents.AddRange(
            Deploy("api", "staging", "v3", TimeSpan.FromHours(2)),
            Deploy("api", "dev", "v3", TimeSpan.FromHours(3)),
            // A redeploy of the same version: the environment reports its latest deploy.
            Deploy("api", "dev", "v3", TimeSpan.FromHours(1)),
            Deploy("api", "uat", "v3", TimeSpan.FromHours(1), status: "failed"),
            Deploy("api", "qa", "v2", TimeSpan.FromHours(6)),
            Deploy("web", "staging", "w7", TimeSpan.FromHours(4)),
            Deploy("worker", "staging", "k1", TimeSpan.FromHours(7)));

        db.UserPreferences.Add(new UserPreference
        {
            Id = Guid.NewGuid(),
            UserEmail = Me,
            Key = UserPreferenceKeys.HiddenProducts,
            Value = "[\"hidden-prod\"]",
        });
        db.DeletedServices.Add(new DeletedService
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "retired",
            DeletedById = "admin",
            DeletedByName = "Admin",
        });

        await db.SaveChangesAsync();
        return factory;
    }

    private static Guid Id(string suffix) => Guid.Parse($"00000000-0000-0000-0000-0000000000{suffix}");

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Snapshot(bool gated, params string[] requiredRoles)
        => JsonSerializer.Serialize(new ResolvedPolicySnapshot(gated ? Guid.Parse("00000000-0000-0000-0002-000000000001") : null, null)
        {
            ApprovalSteps = gated
                ? new()
                {
                    new ApprovalStep("Approval", new()
                    {
                        new ApproverRequirement("Approvers", new() { new GroupRef("ReleaseApprovers", "ReleaseApprovers") }, new(), 1),
                    }),
                }
                : new(),
            RequiredWorkItemRoles = requiredRoles.ToList(),
        }, CamelCase);

    private static PromotionCandidate Candidate(
        Guid id, PromotionStatus status, string service, string version, TimeSpan age, string snapshot,
        ReferenceDto[] references, PromotionParticipant[]? participants = null,
        string product = "acme", string sourceEnv = "staging")
        => new()
        {
            Id = id,
            Product = product,
            Service = service,
            SourceEnv = sourceEnv,
            TargetEnv = "prod",
            Version = version,
            Status = status,
            ResolvedPolicyJson = snapshot,
            CreatedAt = T0 - age,
            References = references.ToList(),
            Participants = (participants ?? Array.Empty<PromotionParticipant>()).ToList(),
        };

    private static ReferenceDto WorkItemRef(string key, string content, params ParticipantDto[] participants)
        => new("work-item", Provider: "jira", Key: key, Title: key, Content: content,
            Participants: participants.Length == 0 ? null : participants);

    private static WorkItemApproval Decision(
        string key, string service, string email, string name, WorkItemDecision decision, TimeSpan age,
        string? comment = null, string product = "acme")
        => new()
        {
            Id = Guid.NewGuid(),
            WorkItemKey = key,
            Product = product,
            Service = service,
            TargetEnv = "prod",
            ApproverEmail = email,
            ApproverName = name,
            Decision = decision,
            Comment = comment,
            CreatedAt = T0 - age,
        };

    private static DeployEvent Deploy(string service, string environment, string version, TimeSpan age,
        string status = "succeeded")
        => new()
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            Environment = environment,
            Version = version,
            Source = "ci",
            Status = status,
            DeployedAt = T0 - age,
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            MetadataJson = "{}",
            CreatedAt = T0 - age,
        };

    // ── Row rendering for assertions ──────────────────────────────────────────

    private static string Sig(PendingTicketView t)
        => $"{t.WorkItemKey}/{t.Service}@{t.Version} {t.CandidateStatus} x{t.BlockingPromotions}";

    private static string Sig(PendingAssigneeView a) => $"{a.Email}|{a.DisplayName}|{a.Role}|{a.Count}";

    private static string People(PendingTicketView t)
        => string.Join(",", t.Participants.Select(p => $"{p.Role}:{p.Email}"));

    private static string[] Envs(PendingTicketView t)
        => t.Environments.Select(e => $"{e.Environment}@{e.Version}").ToArray();

    private static string? Overall(PendingTicketView t)
        => t.Overall is not { } o
            ? null
            : $"{o.State} {o.Instances} (a{o.Approved} i{o.Issues} b{o.Blocked} p{o.Pending}) "
              + string.Join(",", o.InstanceStatuses.Select(i => $"{i.Service}:{i.State}:{i.Title}"));
}
