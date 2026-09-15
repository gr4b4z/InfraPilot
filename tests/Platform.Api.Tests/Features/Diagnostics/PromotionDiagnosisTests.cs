using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Diagnostics;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Diagnostics;

/// <summary>
/// The observation half of the diagnosis. These assert what the service *sees*, not which cause it
/// names — the causes are authored, and picking between them is the playbook's job. A wrong answer
/// from the assistant is therefore either a wrong flag here or a wrong write-up there, and this
/// pins down the first.
/// </summary>
public class PromotionDiagnosisTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DiagnosticsService _sut;

    public PromotionDiagnosisTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "InfraPilot.slnx")))
            repoRoot = repoRoot.Parent;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Playbooks:Path"] = Path.Combine(repoRoot!.FullName, "playbooks"),
            })
            .Build();

        _sut = new DiagnosticsService(_db, new PlaybookRegistry(config, NullLogger<PlaybookRegistry>.Instance));
    }

    public void Dispose() => _db.Dispose();

    private PromotionCandidate GivenCandidate(
        PromotionStatus status = PromotionStatus.Approved,
        TimeSpan? age = null,
        string version = "1.0.0")
    {
        var approvedAt = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromHours(3));
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "mpt-extensions",
            Service = "mpt-extension-adobe",
            SourceEnv = "dev",
            TargetEnv = "test",
            Version = version,
            Status = status,
            CreatedAt = approvedAt,
            ApprovedAt = status == PromotionStatus.Pending ? null : approvedAt,
        };
        _db.PromotionCandidates.Add(candidate);
        _db.SaveChanges();
        return candidate;
    }

    private void GivenSubscription(string events = """["promotion.approved"]""", bool active = true,
        string products = "[]", string environments = "[]")
    {
        _db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Name = "mpt-release dispatch",
            Url = "https://api.github.com/repos/x/y/dispatches",
            Active = active,
            EventsJson = events,
            FilterProductsJson = products,
            FilterServicesJson = "[]",
            FilterEnvironmentsJson = environments,
        });
        _db.SaveChanges();
    }

    private void GivenDelivery(Guid candidateId, string status, int attempts = 1, string? error = null)
    {
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            EventType = "promotion.approved",
            CancelKey = $"promotion-approved:{candidateId}",
            Status = status,
            Attempts = attempts,
            ErrorMessage = error,
            PayloadJson = $$$"""{"data":{"id":"{{{candidateId}}}"}}""",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        });
        _db.SaveChanges();
    }

    private void GivenDeployEvent(PromotionCandidate c, string version, string status = "succeeded")
    {
        _db.DeployEvents.Add(new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = c.Product,
            Service = c.Service,
            Environment = c.TargetEnv,
            Version = version,
            Status = status,
            Source = "helm-deploy",
            DeployedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Unknown_candidate_returns_null()
        => Assert.Null(await _sut.DiagnosePromotionAsync(Guid.NewGuid()));

    [Fact]
    public async Task Deployed_candidate_is_not_treated_as_stalled()
    {
        var candidate = GivenCandidate(PromotionStatus.Deployed);

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.NotNull(result);
        Assert.Empty(result.ProbableCauses);
        Assert.Contains(result.Evidence, e => e.Contains("not stalled"));
    }

    [Fact]
    public async Task Recently_approved_promotion_is_inside_the_grace_period()
    {
        var candidate = GivenCandidate(age: TimeSpan.FromMinutes(2));
        GivenSubscription();

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.WithinGracePeriod, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "still-in-grace");
    }

    [Fact]
    public async Task No_matching_subscription_is_observed()
    {
        var candidate = GivenCandidate();
        // Listens for the right event, but only for another product.
        GivenSubscription(products: """["some-other-product"]""");

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.NoActiveSubscription, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "no-subscription");
    }

    [Fact]
    public async Task An_inactive_subscription_does_not_count_as_listening()
    {
        var candidate = GivenCandidate();
        GivenSubscription(active: false);

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.NoActiveSubscription, result!.Observations);
    }

    [Fact]
    public async Task An_unfiltered_subscription_matches_every_product_and_environment()
    {
        var candidate = GivenCandidate();
        GivenSubscription(products: "[]", environments: "[]");

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.DoesNotContain(Observations.NoActiveSubscription, result!.Observations);
    }

    [Fact]
    public async Task Failed_delivery_is_observed_with_its_error()
    {
        var candidate = GivenCandidate();
        GivenSubscription();
        GivenDelivery(candidate.Id, "failed", attempts: 5, error: "404 Not Found");

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.DeliveryFailed, result!.Observations);
        Assert.Contains(result.Evidence, e => e.Contains("404 Not Found"));
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "delivery-failed");
    }

    [Fact]
    public async Task Queued_delivery_is_not_reported_as_lost()
    {
        var candidate = GivenCandidate();
        GivenSubscription();
        GivenDelivery(candidate.Id, "pending");

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.DeliveryPending, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "delivery-still-queued");
        Assert.DoesNotContain(result.ProbableCauses, c => c.Cause.Id == "delivery-failed");
    }

    /// <summary>
    /// The signature of the most common genuine stall: everything downstream worked, and reconcile
    /// correctly did nothing because the declared version was already running.
    /// </summary>
    [Fact]
    public async Task Delivered_but_no_deploy_event_points_at_the_no_diff_landing()
    {
        var candidate = GivenCandidate();
        GivenSubscription();
        GivenDelivery(candidate.Id, "delivered");

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.DeliverySucceeded, result!.Observations);
        Assert.Contains(Observations.NoDeployEventSinceApproval, result.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "no-diff-landing");
    }

    [Fact]
    public async Task A_successful_deploy_of_the_same_version_means_it_only_needs_closing()
    {
        var candidate = GivenCandidate();
        GivenSubscription();
        GivenDelivery(candidate.Id, "delivered");
        GivenDeployEvent(candidate, candidate.Version);

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.DeployEventSucceeded, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "deployed-but-not-closed");
        Assert.DoesNotContain(result.ProbableCauses, c => c.Cause.Id == "no-diff-landing");
    }

    [Fact]
    public async Task A_failed_deploy_of_the_same_version_is_distinguished_from_no_deploy()
    {
        var candidate = GivenCandidate();
        GivenSubscription();
        GivenDelivery(candidate.Id, "delivered");
        GivenDeployEvent(candidate, candidate.Version, status: "failed");

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.DeployEventFailed, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "deploy-failed");
    }

    [Fact]
    public async Task A_deploy_of_a_different_version_is_not_mistaken_for_this_one()
    {
        var candidate = GivenCandidate(version: "1.0.0");
        GivenSubscription();
        GivenDelivery(candidate.Id, "delivered");
        GivenDeployEvent(candidate, "2.0.0");

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.DeployEventOtherVersion, result!.Observations);
        Assert.DoesNotContain(Observations.DeployEventSucceeded, result.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "wrong-version-deployed");
    }

    [Fact]
    public async Task A_newer_candidate_for_the_same_target_is_observed()
    {
        var candidate = GivenCandidate(version: "1.0.0");
        GivenSubscription();
        _db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = candidate.Product,
            Service = candidate.Service,
            SourceEnv = candidate.SourceEnv,
            TargetEnv = candidate.TargetEnv,
            Version = "2.0.0",
            Status = PromotionStatus.Deployed,
            CreatedAt = candidate.CreatedAt.AddMinutes(10),
        });
        await _db.SaveChangesAsync();

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.NewerCandidateExists, result!.Observations);
        Assert.Contains(result.Evidence, e => e.Contains("2.0.0"));
    }

    [Fact]
    public async Task A_pending_promotion_is_diagnosed_against_its_gates_not_its_plumbing()
    {
        var candidate = GivenCandidate(PromotionStatus.Pending);

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.NoApprovalsRecorded, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "awaiting-approver");
        // Webhook plumbing is irrelevant before approval, so it must not be reported.
        Assert.DoesNotContain(Observations.NoDeliveryRecorded, result.Observations);
    }

    [Fact]
    public async Task Outstanding_work_items_hold_a_pending_promotion()
    {
        var candidate = GivenCandidate(PromotionStatus.Pending);
        _db.PromotionWorkItems.Add(new PromotionWorkItem
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            WorkItemKey = "MPT-1234",
            Product = candidate.Product,
            Service = candidate.Service,
            TargetEnv = candidate.TargetEnv,
        });
        await _db.SaveChangesAsync();

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.WorkItemsOutstanding, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "work-items-outstanding");
    }

    /// <summary>
    /// Sign-off is scoped to the target environment, so approving an item for staging must not make
    /// it count as approved for prod.
    /// </summary>
    [Fact]
    public async Task Work_item_signed_off_for_another_environment_does_not_count()
    {
        var candidate = GivenCandidate(PromotionStatus.Pending);
        _db.PromotionWorkItems.Add(new PromotionWorkItem
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            WorkItemKey = "MPT-1234",
            Product = candidate.Product,
            Service = candidate.Service,
            TargetEnv = candidate.TargetEnv,
        });
        _db.WorkItemApprovals.Add(new WorkItemApproval
        {
            Id = Guid.NewGuid(),
            WorkItemKey = "MPT-1234",
            Product = candidate.Product,
            Service = candidate.Service,
            TargetEnv = "prod", // candidate targets "test"
            ApproverEmail = "qa@example.com",
            ApproverName = "QA",
            Decision = WorkItemDecision.Approved,
        });
        await _db.SaveChangesAsync();

        var result = await _sut.DiagnosePromotionAsync(candidate.Id);

        Assert.Contains(Observations.WorkItemsOutstanding, result!.Observations);
    }
}
