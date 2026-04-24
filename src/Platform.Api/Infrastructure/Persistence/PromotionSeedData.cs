using System.Text.Json;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Features;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Generates deterministic demo promotion data that builds on top of
/// <see cref="DeploymentSeedData"/>. Seeds a topology, policies, candidates
/// in mixed lifecycle states, and approval trails so the Promotions UI has
/// something to display on first run.
///
/// <para>Must run <b>after</b> <see cref="DeploymentSeedData.Seed"/> so the
/// <c>DeployEvents</c> table is already populated.</para>
/// </summary>
public static class PromotionSeedData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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

    public static async Task Seed(PlatformDbContext db)
    {
        // Guard: only seed if no candidates exist yet.
        if (db.PromotionCandidates.Any()) return;

        // Guard: we need deployment events to derive candidates from.
        if (!db.DeployEvents.Any()) return;

        var rand = new Random(20260416); // deterministic, different seed from DeploymentSeedData
        var now = DateTimeOffset.UtcNow;

        // ── 1. Seed topology ──────────────────────────────────────────────
        await SeedTopology(db, now);

        // ── 2. Seed policies ──────────────────────────────────────────────
        var policies = SeedPolicies(db, now);
        await db.SaveChangesAsync();

        // ── 3. Seed candidates derived from real deploy events ────────────
        await SeedCandidates(db, policies, rand, now);

        await db.SaveChangesAsync();
    }

    private static async Task SeedTopology(PlatformDbContext db, DateTimeOffset now)
    {
        // Only seed if no topology exists yet.
        var existing = db.PlatformSettings
            .FirstOrDefault(s => s.Key == PromotionTopologyService.SettingKey);
        if (existing is not null) return;

        var topology = new PromotionTopology(
            ["development", "staging", "production"],
            [
                new PromotionEdge("development", "staging"),
                new PromotionEdge("staging", "production"),
            ]);

        db.PlatformSettings.Add(new PlatformSetting
        {
            Key = PromotionTopologyService.SettingKey,
            Value = JsonSerializer.Serialize(topology, JsonOptions),
            UpdatedAt = now.AddDays(-30),
            UpdatedBy = "system",
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates promotion policies for each product × target-env combination.
    /// dev→staging is auto-approve (no approver group); staging→production requires approval.
    /// </summary>
    private static List<PromotionPolicy> SeedPolicies(PlatformDbContext db, DateTimeOffset now)
    {
        var products = new[] { "ticketing-platform", "marketplace", "identity-platform", "observability" };
        var policies = new List<PromotionPolicy>();

        foreach (var product in products)
        {
            // dev → staging: auto-approve (no approver group)
            policies.Add(new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = product,
                TargetEnv = "staging",
                ApproverGroup = null, // auto-approve
                Strategy = PromotionStrategy.Any,
                MinApprovers = 0,
                ExcludeRole = null,
                TimeoutHours = 24,
                CreatedAt = now.AddDays(-28),
                UpdatedAt = now.AddDays(-28),
            });

            // staging → production: gated, 2-of-N approval, deployer excluded
            policies.Add(new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = product,
                TargetEnv = "production",
                ApproverGroup = "InfraPortal.Admin",
                Strategy = PromotionStrategy.NOfM,
                MinApprovers = 2,
                ExcludeRole = "triggered-by",
                TimeoutHours = 48,
                EscalationGroup = "SWO-PLT-TeamLeads",
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

            // Distribute statuses for a realistic mix
            var roll = rand.NextDouble();
            var (status, approvedAt, deployedAt) = roll switch
            {
                < 0.25 => (PromotionStatus.Pending, (DateTimeOffset?)null, (DateTimeOffset?)null),
                < 0.40 => (PromotionStatus.Approved, (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(1, 12)), (DateTimeOffset?)null),
                < 0.55 => (PromotionStatus.Deploying, (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(1, 6)), (DateTimeOffset?)null),
                < 0.80 => (PromotionStatus.Deployed, (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(1, 6)),
                    (DateTimeOffset?)deploy.DeployedAt.AddHours(rand.Next(7, 24))),
                < 0.90 => (PromotionStatus.Rejected, (DateTimeOffset?)null, (DateTimeOffset?)null),
                _ => (PromotionStatus.Superseded, (DateTimeOffset?)null, (DateTimeOffset?)null),
            };

            // Extract deployer email from participants JSON (same logic as DeploymentService)
            var deployer = ExtractDeployer(deploy.ParticipantsJson);

            var candidate = new PromotionCandidate
            {
                Id = candidateId,
                Product = deploy.Product,
                Service = deploy.Service,
                SourceEnv = "staging",
                TargetEnv = "production",
                Version = deploy.Version,
                SourceDeployEventId = deploy.Id,
                SourceDeployerName = deployer?.Name,
                SourceDeployerEmail = deployer?.Email,
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

            candidates.Add(candidate);

            // Generate approval trail for non-Pending, non-Superseded candidates
            if (status is PromotionStatus.Rejected)
            {
                var (name, email) = PickApprover(rand, deployer?.Email);
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
                // 2-of-N required — seed 2 approvals
                var usedEmails = new HashSet<string>();
                for (var i = 0; i < 2; i++)
                {
                    var (name, email) = PickApprover(rand, deployer?.Email, usedEmails);
                    usedEmails.Add(email);
                    approvals.Add(new PromotionApproval
                    {
                        Id = Guid.NewGuid(),
                        CandidateId = candidateId,
                        ApproverEmail = email,
                        ApproverName = name,
                        Decision = PromotionDecision.Approved,
                        Comment = ApprovalComments[rand.Next(ApprovalComments.Length)],
                        CreatedAt = candidate.CreatedAt.AddHours(rand.Next(1, 6) + i),
                    });
                }
            }
            else if (status is PromotionStatus.Pending)
            {
                // Some Pending candidates have 1 approval (waiting for second)
                if (rand.NextDouble() < 0.4)
                {
                    var (name, email) = PickApprover(rand, deployer?.Email);
                    approvals.Add(new PromotionApproval
                    {
                        Id = Guid.NewGuid(),
                        CandidateId = candidateId,
                        ApproverEmail = email,
                        ApproverName = name,
                        Decision = PromotionDecision.Approved,
                        Comment = ApprovalComments[rand.Next(ApprovalComments.Length)],
                        CreatedAt = candidate.CreatedAt.AddHours(rand.Next(1, 4)),
                    });
                }
            }
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

            var deployer = ExtractDeployer(deploy.ParticipantsJson);

            candidates.Add(new PromotionCandidate
            {
                Id = candidateId,
                Product = deploy.Product,
                Service = deploy.Service,
                SourceEnv = "development",
                TargetEnv = "staging",
                Version = deploy.Version,
                SourceDeployEventId = deploy.Id,
                SourceDeployerName = deployer?.Name,
                SourceDeployerEmail = deployer?.Email,
                Status = status,
                PolicyId = policy.Id,
                ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                CreatedAt = deploy.DeployedAt.AddMinutes(rand.Next(1, 10)),
                ApprovedAt = approvedAt,
                DeployedAt = deployedAt,
            });

            // Auto-approve means no PromotionApproval rows — the system approved it.
        }

        // ── supersede chain (demo): 3 predecessors superseded by a fresh Pending ──
        // Picks one service with ≥4 succeeded staging deploys at distinct versions and
        // constructs a chain on the staging→production edge. The final candidate is
        // Pending and inherits all prior source-event IDs, so the "Inherited from
        // superseded candidates" section on the detail page has something to show.
        SeedSupersedeChain(db, policies, stagingDeploys, candidates, now);

        db.PromotionCandidates.AddRange(candidates);
        db.PromotionApprovals.AddRange(approvals);
    }

    private static void SeedSupersedeChain(
        PlatformDbContext db,
        List<PromotionPolicy> policies,
        List<DeployEvent> _unused,
        List<PromotionCandidate> candidates,
        DateTimeOffset now)
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

        var reservedIds = candidates.Select(c => c.SourceDeployEventId).ToHashSet();

        var group = allStaging
            .Where(d => productsWithProdPolicy.Contains(d.Product))
            .GroupBy(d => (d.Product, d.Service))
            .Select(g => g
                .GroupBy(d => d.Version) // dedupe same-version redeploys
                .Select(vg => vg.OrderByDescending(d => d.DeployedAt).First())
                .Where(d => !reservedIds.Contains(d.Id)) // avoid events already consumed elsewhere
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
        var freshDeployer = ExtractDeployer(fresh.ParticipantsJson);

        var predecessorIds = new List<Guid>();
        foreach (var ev in older)
        {
            var deployer = ExtractDeployer(ev.ParticipantsJson);
            candidates.Add(new PromotionCandidate
            {
                Id = Guid.NewGuid(),
                Product = ev.Product,
                Service = ev.Service,
                SourceEnv = "staging",
                TargetEnv = "production",
                Version = ev.Version,
                SourceDeployEventId = ev.Id,
                SourceDeployerName = deployer?.Name,
                SourceDeployerEmail = deployer?.Email,
                Status = PromotionStatus.Superseded,
                SupersededById = freshId,
                PolicyId = policy.Id,
                ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                CreatedAt = ev.DeployedAt.AddMinutes(5),
            });
            predecessorIds.Add(ev.Id);
        }

        candidates.Add(new PromotionCandidate
        {
            Id = freshId,
            Product = fresh.Product,
            Service = fresh.Service,
            SourceEnv = "staging",
            TargetEnv = "production",
            Version = fresh.Version,
            SourceDeployEventId = fresh.Id,
            SourceDeployerName = freshDeployer?.Name,
            SourceDeployerEmail = freshDeployer?.Email,
            Status = PromotionStatus.Pending,
            PolicyId = policy.Id,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CreatedAt = fresh.DeployedAt.AddMinutes(5),
            SupersededSourceEventIds = predecessorIds,
        });
    }

    private static ResolvedPolicySnapshot MakeSnapshot(PromotionPolicy policy) =>
        new(
            PolicyId: policy.Id,
            ApproverGroup: policy.ApproverGroup,
            Strategy: policy.Strategy,
            MinApprovers: policy.MinApprovers,
            ExcludeRole: policy.ExcludeRole,
            TimeoutHours: policy.TimeoutHours,
            EscalationGroup: policy.EscalationGroup);

    /// <summary>
    /// Picks an approver that is NOT the deployer (to respect the policy's ExcludeRole
    /// semantics), and not already in the <paramref name="exclude"/> set (to avoid duplicate
    /// approvals).
    /// </summary>
    private static (string Name, string Email) PickApprover(
        Random rand, string? deployerEmail, HashSet<string>? exclude = null)
    {
        var eligible = Approvers
            .Where(a => !string.Equals(a.Email, deployerEmail, StringComparison.OrdinalIgnoreCase))
            .Where(a => exclude is null || !exclude.Contains(a.Email))
            .ToArray();

        if (eligible.Length == 0) return Approvers[0]; // fallback — shouldn't happen with 8 approvers
        return eligible[rand.Next(eligible.Length)];
    }

    private record DeployerInfo(string? Name, string? Email);

    /// <summary>
    /// Extracts the first "PR Author" participant from the deploy event's ParticipantsJson.
    /// </summary>
    private static DeployerInfo? ExtractDeployer(string? participantsJson)
    {
        if (string.IsNullOrEmpty(participantsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(participantsJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("role", out var role) &&
                    role.GetString()?.Equals("PR Author", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var name = el.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                    var email = el.TryGetProperty("email", out var em) ? em.GetString() : null;
                    return new DeployerInfo(name, email);
                }
            }
        }
        catch { /* best-effort */ }

        return null;
    }
}
