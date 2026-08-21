using System.Text.Json;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Generates deterministic demo promotion data that builds on top of
/// <see cref="DeploymentSeedData"/>. Seeds policies, self-contained candidates
/// in mixed lifecycle states, their work-item index rows, approval trails, and per-work-item sign-off
/// state (approved / issue / blocked / undecided, with the matching comment thread) so the Promotions
/// and work-items surfaces all have something to display on first run.
///
/// <para>The two seeded policies are the two shapes worth demonstrating: development → staging
/// auto-approves and tracks no work items, staging → production needs an admin's approval and a
/// <c>qa-owner</c> on every work item.</para>
///
/// <para>Candidates are now self-contained (each carries its own References) and created
/// externally in production — there is no topology to seed. For demo data we copy the
/// originating deploy event's references onto the candidate.</para>
///
/// <para>Must run <b>after</b> <see cref="DeploymentSeedData.Seed"/> so the
/// <c>DeployEvents</c> table is already populated.</para>
/// </summary>
public static class PromotionSeedData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Reuse the same people pool from DeploymentSeedData for consistency.
    private static readonly (string Name, string Email)[] Approvers =
    [
        ("Jan Kowalski", "jan.kowalski@acmetrix.com"),
        ("Anna Kowalska", "anna.kowalska@acmetrix.com"),
        ("Piotr Nowak", "piotr.nowak@acmetrix.com"),
        ("Marta Wiśniewska", "marta.wisniewska@acmetrix.com"),
        ("Sylwester Grabowski", "sylwester.grabowski@acmetrix.com"),
        ("Tomasz Wójcik", "tomasz.wojcik@acmetrix.com"),
        ("Katarzyna Lewandowska", "katarzyna.lewandowska@acmetrix.com"),
        ("Michał Zieliński", "michal.zielinski@acmetrix.com"),
    ];

    private static readonly string[] ApprovalComments =
    [
        "Looks good, staging metrics are healthy.",
        "Approved — all smoke tests green.",
        "LGTM. Rollback plan is documented.",
        "Verified in staging, performance baseline maintained.",
        "Checked dashboards, no anomalies. Ship it.",
        "Approved after reviewing the changelog.",
        "Infrastructure validated, proceeding.",
        "Signed off — change window open.",
    ];

    private static readonly string[] RejectionComments =
    [
        "Staging has elevated error rates — hold until investigated.",
        "This version has a known regression in the billing module.",
        "Blocked: security scan flagged a high-severity CVE.",
        "Needs load test results before production promotion.",
    ];

    // Sign-off notes on individual work items. Deliberately narrower in scope than the promotion-level
    // comments above: these are about one ticket, not about a release.
    private static readonly string[] WorkItemApprovalComments =
    [
        "Tested on staging, behaves as described in the ticket.",
        "Acceptance criteria all met. Good to go.",
        "Regression suite green, no change to the public contract.",
        "Verified the edge case from the bug report — fixed.",
        "Checked with the reporter, they're happy with it.",
    ];

    private static readonly string[] WorkItemProblemComments =
    [
        "Repro still happens on the second attempt — needs another look.",
        "Missing the migration for the new column; would fail on prod data.",
        "This changes the response shape without a version bump.",
        "Waiting on the security team to sign off the dependency bump.",
        "No test covers the acceptance criteria in the ticket.",
    ];

    private static readonly string[] ThreadReplies =
    [
        "Picked this up — should have an answer by tomorrow.",
        "Agreed, let's hold it for the next release train.",
        "I can reproduce it too. Raised a follow-up ticket.",
        "Fixed on the branch, waiting for CI.",
        "Talked to QA, they'll re-test once staging is redeployed.",
    ];

    public static async Task Seed(PlatformDbContext db)
    {
        // Guard: only seed if no candidates exist yet.
        if (db.PromotionCandidates.Any()) return;

        // Guard: we need deployment events to derive candidates from.
        if (!db.DeployEvents.Any()) return;

        var rand = new Random(20260416); // deterministic, different seed from DeploymentSeedData
        var now = DateTimeOffset.UtcNow;

        // ── 1. Seed policies ──────────────────────────────────────────────
        var policies = SeedPolicies(db, now);
        await db.SaveChangesAsync();

        // ── 2. Seed self-contained candidates derived from real deploy events ──
        await SeedCandidates(db, policies, rand, now);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Copies a deploy event's references onto a candidate (the self-contained net change set) and
    /// stages <see cref="PromotionWorkItem"/> rows for its work-item references.
    ///
    /// <para><paramref name="tracksWorkItems"/> mirrors the runtime's create path: when the resolved
    /// policy opts the edge out of work items, the references are still copied (they are what shipped)
    /// but no index rows are staged, so the seeded data can't contradict the flag on its own policy.</para>
    /// </summary>
    private static void PopulateChangeSet(
        PlatformDbContext db, PromotionCandidate candidate, DeployEvent source, bool tracksWorkItems = true)
    {
        var refs = string.IsNullOrEmpty(source.ReferencesJson)
            ? new List<ReferenceDto>()
            : JsonSerializer.Deserialize<List<ReferenceDto>>(source.ReferencesJson, JsonOptions) ?? new();
        candidate.References = refs;

        // Commit-level provenance: real producers send the target env's current SHA and the SHA
        // being promoted, and the detail page renders them as a compare link off the repository
        // reference. Mirror that here (toRevision from the source event's repository reference,
        // fromRevision synthesized) so the revision row shows up on a fresh database.
        var repositoryRevision = refs.FirstOrDefault(r =>
            string.Equals(r.Type, "repository", StringComparison.OrdinalIgnoreCase))?.Revision;
        if (!string.IsNullOrWhiteSpace(repositoryRevision))
        {
            candidate.ToRevision = repositoryRevision;
            candidate.FromRevision = Guid.NewGuid().ToString("N")[..12];
        }

        if (!tracksWorkItems) return;

        var workItems = refs
            .Where(r => string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (var r in workItems)
        {
            // Same resolver the ingest path uses, so a seeded row is named like a real one.
            var (title, subTitle) = Platform.Api.Features.Deployments.WorkItemDisplay.Resolve(r, refs);
            db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                WorkItemKey = r.Key!,
                Product = candidate.Product,
                TargetEnv = candidate.TargetEnv,
                Provider = r.Provider,
                Url = r.Url,
                Title = title,
                SubTitle = subTitle,
                Content = r.Content,
                Revision = r.Revision,
                CreatedAt = candidate.CreatedAt,
            });
        }
    }

    /// <summary>
    /// Creates promotion policies for each product × target-env combination.
    /// dev→staging is auto-approve (no approver group); staging→production requires approval.
    /// </summary>
    private static List<PromotionPolicy> SeedPolicies(PlatformDbContext db, DateTimeOffset now)
    {
        // Must match DeploymentSeedData.Catalog — a policy for a product with no deploy events would
        // never produce a candidate, and a product with no policy is silently skipped below.
        var products = new[] { "mpt", "mpt-extentions", "extra" };
        var policies = new List<PromotionPolicy>();

        foreach (var product in products)
        {
            // development → staging: auto-approve (no approval steps), and no work items. Staging is
            // where a change lands to be integrated, not where QA signs it off — putting every dev
            // promotion's tickets in the work-items queue would bury the ones that are actually
            // somebody's to review. They become work items on the edge into production, below.
            policies.Add(new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = product,
                TargetEnv = "staging",
                ApprovalSteps = new(), // empty ⇒ auto-approve
                TracksWorkItems = false,
                CreatedAt = now.AddDays(-28),
                UpdatedAt = now.AddDays(-28),
            });

            // staging → production: a single "Release Approval" step, approved by the admin group.
            //
            // MinApprovers is 1 because locally there is exactly one admin (admin@localhost, see
            // SeedData.SeedLocalUsers). Asking for 2 distinct approvers from a group of one produces a
            // queue of promotions that can be looked at and never approved, which makes the whole
            // approve path untestable on a fresh database — the opposite of what demo data is for.
            policies.Add(new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = product,
                TargetEnv = "production",
                ApprovalSteps = new()
                {
                    new ApprovalStep("Release Approval", new()
                    {
                        new ApproverRequirement(
                            Name: "Release managers",
                            Groups: new() { new GroupRef("InfraPortal.Admin", "InfraPortal.Admin") },
                            Users: new(),
                            MinApprovers: 1),
                    }),
                },
                EscalationGroup = "SWO-PLT-TeamLeads",
                // Production promotions must name a QA owner per work item. Seeded candidates carry no
                // participants, so this is what makes the "needs attention" / "Not assigned" surfaces
                // show something in a fresh dev database instead of looking unimplemented.
                RequiredWorkItemRoles = new() { "qa-owner" },
                CreatedAt = now.AddDays(-28),
                UpdatedAt = now.AddDays(-14),
            });
        }

        db.PromotionPolicies.AddRange(policies);
        return policies;
    }

    /// <summary>
    /// Picks recent deployment events across products and creates candidates in varied states:
    /// Pending (awaiting approval), Approved, Deploying, Deployed, Rejected, and Superseded.
    /// </summary>
    private static async Task SeedCandidates(
        PlatformDbContext db,
        List<PromotionPolicy> policies,
        Random rand,
        DateTimeOffset now)
    {
        // Grab recent successful staging and development deploys to create candidates from.
        var stagingDeploys = db.DeployEvents
            .Where(e => e.Environment == "staging" && e.Status == "succeeded")
            .OrderByDescending(e => e.DeployedAt)
            .Take(60)
            .ToList();

        var devDeploys = db.DeployEvents
            .Where(e => e.Environment == "development" && e.Status == "succeeded")
            .OrderByDescending(e => e.DeployedAt)
            .Take(40)
            .ToList();

        var candidates = new List<PromotionCandidate>();
        var approvals = new List<PromotionApproval>();

        // ── staging → production candidates (gated, most interesting) ─────
        foreach (var deploy in stagingDeploys.Take(30))
        {
            var policy = policies.FirstOrDefault(
                p => p.Product == deploy.Product && p.TargetEnv == "production");
            if (policy is null) continue;

            var snapshot = MakeSnapshot(policy);
            var candidateId = Guid.NewGuid();

            // Distribute statuses for a realistic mix, weighted towards Pending. Pending is the state
            // every queue, badge and approval surface reads from — at an even split there were a
            // handful of live work items in the whole database and most tabs opened on an empty state.
            var roll = rand.NextDouble();
            var (status, approvedAt, deployedAt) = roll switch
            {
                < 0.40 => (PromotionStatus.Pending, (DateTimeOffset?)null, (DateTimeOffset?)null),
                < 0.50 => (PromotionStatus.Approved, (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(1, 12)), (DateTimeOffset?)null),
                < 0.58 => (PromotionStatus.Deploying, (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(1, 6)), (DateTimeOffset?)null),
                < 0.80 => (PromotionStatus.Deployed, (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(1, 6)),
                    (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(7, 24))),
                < 0.90 => (PromotionStatus.Rejected, (DateTimeOffset?)null, (DateTimeOffset?)null),
                _ => (PromotionStatus.Superseded, (DateTimeOffset?)null, (DateTimeOffset?)null),
            };

            var candidate = new PromotionCandidate
            {
                Id = candidateId,
                Product = deploy.Product,
                Service = deploy.Service,
                SourceEnv = "staging",
                TargetEnv = "production",
                Version = deploy.Version,
                Status = status,
                PolicyId = policy.Id,
                ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                ExternalRunUrl = status is PromotionStatus.Deploying or PromotionStatus.Deployed
                    ? $"https://ci.acmetrix.com/runs/{rand.Next(10000, 99999)}"
                    : null,
                CreatedAt = deploy.DeployedAt.AddMinutes(rand.Next(1, 30)),
                ApprovedAt = approvedAt,
                DeployedAt = deployedAt,
            };
            PopulateChangeSet(db, candidate, deploy, policy.TracksWorkItems);

            candidates.Add(candidate);

            // Generate approval trail for non-Pending, non-Superseded candidates
            if (status is PromotionStatus.Rejected)
            {
                var (name, email) = PickApprover(rand);
                approvals.Add(new PromotionApproval
                {
                    Id = Guid.NewGuid(),
                    CandidateId = candidateId,
                    ApproverEmail = email,
                    ApproverName = name,
                    Decision = PromotionDecision.Rejected,
                    Comment = RejectionComments[rand.Next(RejectionComments.Length)],
                    CreatedAt = candidate.CreatedAt.AddHours(rand.Next(1, 8)),
                });
            }
            else if (status is PromotionStatus.Approved or PromotionStatus.Deploying or PromotionStatus.Deployed)
            {
                // One approval, matching the policy's MinApprovers of 1 — a second row would describe
                // a promotion that waited for an approver the policy never asked for.
                var (name, email) = PickApprover(rand);
                approvals.Add(new PromotionApproval
                {
                    Id = Guid.NewGuid(),
                    CandidateId = candidateId,
                    ApproverEmail = email,
                    ApproverName = name,
                    Decision = PromotionDecision.Approved,
                    Comment = ApprovalComments[rand.Next(ApprovalComments.Length)],
                    CreatedAt = candidate.CreatedAt.AddHours(rand.Next(1, 6)),
                });
            }
            // Pending candidates carry no approvals: one approval is all the policy asks for, so a
            // Pending row with an Approved row against it would be a state the runtime can't produce.
        }

        // ── dev → staging candidates (auto-approve, most land as Deployed) ──
        foreach (var deploy in devDeploys.Take(20))
        {
            var policy = policies.FirstOrDefault(
                p => p.Product == deploy.Product && p.TargetEnv == "staging");
            if (policy is null) continue;

            var snapshot = MakeSnapshot(policy);
            var candidateId = Guid.NewGuid();

            // Auto-approve: most are Deployed, a few still Deploying
            var roll = rand.NextDouble();
            var (status, approvedAt, deployedAt) = roll switch
            {
                < 0.15 => (PromotionStatus.Deploying,
                    (DateTimeOffset?)deploy.DeployedAt.AddMinutes(1),
                    (DateTimeOffset?)null),
                < 0.25 => (PromotionStatus.Superseded,
                    (DateTimeOffset?)deploy.DeployedAt.AddMinutes(1),
                    (DateTimeOffset?)null),
                _ => (PromotionStatus.Deployed,
                    (DateTimeOffset?)deploy.DeployedAt.AddMinutes(1),
                    (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(1, 4))),
            };

            var devCandidate = new PromotionCandidate
            {
                Id = candidateId,
                Product = deploy.Product,
                Service = deploy.Service,
                SourceEnv = "development",
                TargetEnv = "staging",
                Version = deploy.Version,
                Status = status,
                PolicyId = policy.Id,
                ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                CreatedAt = deploy.DeployedAt.AddMinutes(rand.Next(1, 10)),
                ApprovedAt = approvedAt,
                DeployedAt = deployedAt,
            };
            PopulateChangeSet(db, devCandidate, deploy, policy.TracksWorkItems);
            candidates.Add(devCandidate);

            // Auto-approve means no PromotionApproval rows — the system approved it.
        }

        // ── supersede chain (demo): 3 superseded predecessors + a fresh Pending winner ──
        // Picks one service with ≥4 succeeded staging deploys at distinct versions and a matching
        // staging→production policy. Each candidate is self-contained (its own References); supersede
        // is just a state flip (D2), so the predecessors are Superseded and point at the winner.
        SeedSupersedeChain(db, policies, candidates);

        db.PromotionCandidates.AddRange(candidates);
        db.PromotionApprovals.AddRange(approvals);

        // Work-item sign-off state, once every candidate exists.
        SeedWorkItemDecisions(db, candidates, rand);
    }

    /// <summary>
    /// Puts work items into a spread of sign-off states — approved, flagged with an issue, blocked, or
    /// still open — keyed off the state of the promotion carrying them, so the data doesn't contradict
    /// itself (see <see cref="PickOutcome"/>).
    ///
    /// <para>Who decides matters as much as what. A work item the signed-in user has already decided
    /// leaves their pending queue (see <c>WorkItemApprovalService.GetPendingForCurrentUserAsync</c>)
    /// and appears under "Decided" instead, so the local accounts get a slice of the decisions on
    /// promotions that are already through the gate and only a few on the live ones — the Decided tab
    /// has content without the pending queue being drained to fill it.</para>
    ///
    /// <para>Each decision also writes the thread entry the runtime writes alongside it — the
    /// work-item page reads the conversation from <c>WorkItemComments</c>, and a decision trail with
    /// no matching entry reads as if nothing happened.</para>
    /// </summary>
    private static void SeedWorkItemDecisions(
        PlatformDbContext db, List<PromotionCandidate> candidates, Random rand)
    {
        // A decision is keyed on (key, product, targetEnv) — not on the candidate — so the same ticket
        // carried by two promotions must not be decided twice.
        var decided = new HashSet<(string Key, string Product, string Env)>();

        // Pending first, so the spread below claims the tickets that drive the queue before a historic
        // candidate carrying the same key can blanket-approve them.
        var ordered = candidates
            .Where(c => c.TargetEnv == "production")
            .OrderBy(c => c.Status == PromotionStatus.Pending ? 0 : 1);

        foreach (var candidate in ordered)
        {
            var keys = candidate.References
                .Where(r => string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(r.Key))
                .Select(r => r.Key!)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                var tuple = (key, candidate.Product, candidate.TargetEnv);
                if (!decided.Add(tuple)) continue;

                var outcome = PickOutcome(candidate.Status, rand);
                if (outcome is null) continue; // left awaiting a decision

                var (decision, who) = outcome.Value;
                var (name, email) = who;
                var comment = decision == WorkItemDecision.Approved
                    ? WorkItemApprovalComments[rand.Next(WorkItemApprovalComments.Length)]
                    : WorkItemProblemComments[rand.Next(WorkItemProblemComments.Length)];
                var decidedAt = candidate.CreatedAt.AddHours(rand.Next(1, 20));

                db.WorkItemApprovals.Add(new WorkItemApproval
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = key,
                    Product = candidate.Product,
                    TargetEnv = candidate.TargetEnv,
                    ApproverEmail = email,
                    ApproverName = name,
                    Decision = decision,
                    Comment = comment,
                    CreatedAt = decidedAt,
                });

                db.WorkItemComments.Add(new WorkItemComment
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = key,
                    Product = candidate.Product,
                    TargetEnv = candidate.TargetEnv,
                    AuthorEmail = email,
                    AuthorName = name,
                    Decision = decision,
                    Body = DescribeDecision(decision, comment),
                    CreatedAt = decidedAt,
                });

                // A reply on some of them, so the thread is a conversation rather than a single
                // system-shaped entry — and so the edit/delete affordances have a human comment to
                // hang off (decision entries are immutable).
                if (rand.NextDouble() < 0.4)
                {
                    var (replyName, replyEmail) = PickApprover(rand, new HashSet<string> { email });
                    db.WorkItemComments.Add(new WorkItemComment
                    {
                        Id = Guid.NewGuid(),
                        WorkItemKey = key,
                        Product = candidate.Product,
                        TargetEnv = candidate.TargetEnv,
                        AuthorEmail = replyEmail,
                        AuthorName = replyName,
                        Body = ThreadReplies[rand.Next(ThreadReplies.Length)],
                        CreatedAt = decidedAt.AddHours(rand.Next(1, 6)),
                    });
                }
            }
        }
    }

    /// <summary>
    /// What happened to a work item, given the state of the promotion carrying it — or null for "nobody
    /// has decided yet". Reading the outcome off the promotion's own status is what keeps the data
    /// self-consistent: a promotion that reached production got there because its work items were
    /// signed off, and a promotion still Pending is the only place an open item makes sense.
    /// </summary>
    private static (WorkItemDecision Decision, (string Name, string Email) By)? PickOutcome(
        PromotionStatus status, Random rand)
    {
        switch (status)
        {
            // Already through the gate ⇒ every work item on it was approved. These are what fill the
            // "Decided" tab and give the work-item pages a finished trail to show.
            case PromotionStatus.Approved:
            case PromotionStatus.Deploying:
            case PromotionStatus.Deployed:
                return (WorkItemDecision.Approved, PickDecider(rand));

            // The live queue. Mostly open — deciding these would empty the tab the demo data exists to
            // fill — with enough decided to show every colour a row can take.
            case PromotionStatus.Pending:
                if (rand.NextDouble() < 0.55) return null;
                // Two independent rolls, and even thirds. Reusing the first roll to pick the decision
                // as well confined Blocked to the top slice of the same band, which on this fixed seed
                // produced exactly one blocked item in the whole database — and blocked is the state
                // whose effect on the promotion gate is the one worth seeing.
                var decision = rand.NextDouble() switch
                {
                    < 0.34 => WorkItemDecision.Approved,
                    < 0.67 => WorkItemDecision.Issue,
                    _ => WorkItemDecision.Blocked,
                };
                return (decision, PickDecider(rand));

            // Rejected or superseded: sometimes there's a block behind it, which is also what the
            // queue's orphan handling has to cope with. Mostly left open.
            default:
                if (rand.NextDouble() > 0.3) return null;
                return (
                    rand.NextDouble() < 0.6 ? WorkItemDecision.Blocked : WorkItemDecision.Issue,
                    Approvers[rand.Next(Approvers.Length)]);
        }
    }

    /// <summary>
    /// Who signed a work item off. Mostly colleagues; a slice goes to the local sign-in accounts so
    /// their "Decided" tab isn't empty.
    /// </summary>
    private static (string Name, string Email) PickDecider(Random rand)
    {
        var roll = rand.NextDouble();
        if (roll < 0.70) return Approvers[rand.Next(Approvers.Length)];
        if (roll < 0.88) return ("Regular User", "user@localhost");
        return ("Admin User", "admin@localhost");
    }

    /// <summary>
    /// The thread entry the runtime writes for a decision. Mirrors
    /// <c>WorkItemApprovalService.DescribeDecision</c> — same shape, so seeded threads and real ones
    /// read identically.
    /// </summary>
    private static string DescribeDecision(WorkItemDecision decision, string? comment)
    {
        var headline = decision switch
        {
            WorkItemDecision.Approved => "Approved this work item.",
            WorkItemDecision.Issue => "Raised an issue on this work item.",
            _ => "Blocked this work item.",
        };
        var note = (comment ?? "").Trim();
        return note.Length == 0 ? headline : $"{headline}\n\n{note}";
    }

    private static void SeedSupersedeChain(
        PlatformDbContext db,
        List<PromotionPolicy> policies,
        List<PromotionCandidate> candidates)
    {
        // Load the full set of succeeded staging deploys (not just the top-60 slice used for
        // other candidate seeding) so we have enough depth to find a service with 4 distinct
        // versions and a matching staging→production policy.
        var allStaging = db.DeployEvents
            .Where(e => e.Environment == "staging" && e.Status == "succeeded")
            .OrderByDescending(e => e.DeployedAt)
            .ToList();

        var productsWithProdPolicy = policies
            .Where(p => p.TargetEnv == "production")
            .Select(p => p.Product)
            .ToHashSet();

        // Avoid versions already used by earlier candidate seeding on the same edge so the natural
        // key (product, service, source→target, version) stays distinct.
        var reservedKeys = candidates
            .Select(c => (c.Product, c.Service, c.SourceEnv, c.TargetEnv, c.Version))
            .ToHashSet();

        var group = allStaging
            .Where(d => productsWithProdPolicy.Contains(d.Product))
            .GroupBy(d => (d.Product, d.Service))
            .Select(g => g
                .GroupBy(d => d.Version) // dedupe same-version redeploys
                .Select(vg => vg.OrderByDescending(d => d.DeployedAt).First())
                .Where(d => !reservedKeys.Contains((d.Product, d.Service, "staging", "production", d.Version)))
                .OrderByDescending(d => d.DeployedAt)
                .Take(4)
                .ToList())
            .FirstOrDefault(list => list.Count == 4);

        if (group is null) return;

        var policy = policies.First(p => p.Product == group[0].Product && p.TargetEnv == "production");
        var snapshot = MakeSnapshot(policy);

        // group[0] is newest → becomes the Pending "winner"; group[1..3] are older → Superseded.
        var fresh = group[0];
        var older = group.Skip(1).OrderBy(d => d.DeployedAt).ToList(); // oldest-first

        var freshId = Guid.NewGuid();

        foreach (var ev in older)
        {
            var pred = new PromotionCandidate
            {
                Id = Guid.NewGuid(),
                Product = ev.Product,
                Service = ev.Service,
                SourceEnv = "staging",
                TargetEnv = "production",
                Version = ev.Version,
                Status = PromotionStatus.Superseded,
                SupersededById = freshId,
                PolicyId = policy.Id,
                ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                CreatedAt = ev.DeployedAt.AddMinutes(5),
            };
            PopulateChangeSet(db, pred, ev, policy.TracksWorkItems);
            candidates.Add(pred);
        }

        var winner = new PromotionCandidate
        {
            Id = freshId,
            Product = fresh.Product,
            Service = fresh.Service,
            SourceEnv = "staging",
            TargetEnv = "production",
            Version = fresh.Version,
            Status = PromotionStatus.Pending,
            PolicyId = policy.Id,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CreatedAt = fresh.DeployedAt.AddMinutes(5),
        };
        PopulateChangeSet(db, winner, fresh, policy.TracksWorkItems);
        candidates.Add(winner);
    }

    /// <summary>
    /// Snapshots a policy the same way the runtime does. Delegates to
    /// <see cref="PromotionPolicyResolver.Project"/> rather than re-listing the fields: the local copy
    /// this replaced had gone stale, so seeded candidates were missing settings their policy declared.
    /// </summary>
    private static ResolvedPolicySnapshot MakeSnapshot(PromotionPolicy policy) =>
        PromotionPolicyResolver.Project(policy);

    /// <summary>
    /// Picks a random approver, excluding anyone already in <paramref name="exclude"/>
    /// (to avoid duplicate approvals on the same candidate).
    /// </summary>
    private static (string Name, string Email) PickApprover(Random rand, HashSet<string>? exclude = null)
    {
        var eligible = Approvers
            .Where(a => exclude is null || !exclude.Contains(a.Email))
            .ToArray();

        if (eligible.Length == 0) return Approvers[0]; // fallback — shouldn't happen with 8 approvers
        return eligible[rand.Next(eligible.Length)];
    }
}
