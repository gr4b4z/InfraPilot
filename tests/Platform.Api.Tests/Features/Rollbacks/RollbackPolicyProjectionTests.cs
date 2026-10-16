using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks;
using Platform.Api.Features.Rollbacks.Models;

namespace Platform.Api.Tests.Features.Rollbacks;

/// <summary>
/// Unit tests for <see cref="RollbackPolicyResolver.Project"/> and the creator-set semantics.
///
/// <para>The distinction under test is the one the previous implementation collapsed: "no policy" and
/// "a policy that requires no approvals" both produce an empty requirement set, but only the second is
/// auto-approve. Conflating them is what let an enrolled product with no policy for its production
/// environment revert production with no human gate, so it is asserted directly here rather than only
/// through the API.</para>
/// </summary>
public class RollbackPolicyProjectionTests
{
    private static RollbackPolicy Gated() => new()
    {
        Id = Guid.NewGuid(),
        Product = "acme",
        TargetEnv = "prod",
        ApprovalSteps = new()
        {
            new ApprovalStep("Approval", new()
            {
                new ApproverRequirement("Release managers",
                    new() { new GroupRef("release-managers", "Release Managers") }, new(), 2),
            }),
        },
        EscalationGroup = "platform-leads",
    };

    [Fact]
    public void Project_NoPolicy_HasNoPolicyIdAndNoRequirements()
    {
        var snapshot = RollbackPolicyResolver.Project(null);

        Assert.Null(snapshot.PolicyId);
        Assert.Empty(snapshot.AllRequirements);
        // IsAutoApprove is true here only because there is nothing to satisfy — callers must gate on
        // PolicyId, not on this flag, which is exactly what RollbackService.CreateAsync does.
        Assert.True(snapshot.IsAutoApprove);
    }

    [Fact]
    public void Project_PolicyWithNoSteps_IsAutoApproveButCarriesAPolicyId()
    {
        var policy = new RollbackPolicy { Id = Guid.NewGuid(), Product = "acme", TargetEnv = "dev" };

        var snapshot = RollbackPolicyResolver.Project(policy);

        Assert.Equal(policy.Id, snapshot.PolicyId);
        Assert.True(snapshot.IsAutoApprove);
        // The pair (PolicyId set, IsAutoApprove) is the only combination that may skip a human.
        Assert.NotNull(snapshot.PolicyId);
    }

    [Fact]
    public void Project_CarriesRequirementTreeAndEscalationGroup()
    {
        var policy = Gated();

        var snapshot = RollbackPolicyResolver.Project(policy);

        Assert.Equal(policy.Id, snapshot.PolicyId);
        Assert.False(snapshot.IsAutoApprove);
        Assert.Equal("platform-leads", snapshot.EscalationGroup);
        var req = Assert.Single(snapshot.AllRequirements);
        Assert.Equal("Release managers", req.Name);
        Assert.Equal(2, req.MinApprovers);
        Assert.Equal("release-managers", Assert.Single(req.Groups).Id);
    }

    [Fact]
    public void ApprovalSteps_RoundTripThroughJson()
    {
        // The tree is persisted as a JSON string column, so a save/load cycle must not lose the shape.
        var policy = Gated();
        var reloaded = new RollbackPolicy { ApprovalStepsJson = policy.ApprovalStepsJson };

        var req = Assert.Single(reloaded.ApprovalSteps.SelectMany(s => s.Requirements));
        Assert.Equal("Release managers", req.Name);
        Assert.Equal(2, req.MinApprovers);
        Assert.Equal("Release Managers", Assert.Single(req.Groups).Name);
    }

    [Fact]
    public void Creators_RoundTripThroughJson()
    {
        var policy = new RollbackPolicy
        {
            Creators = new PrincipalSet(
                new() { new GroupRef("sre-oncall", "SRE On-Call") },
                new() { "alice@example.com" }),
        };

        var reloaded = new RollbackPolicy { CreatorsJson = policy.CreatorsJson }.Creators;

        Assert.Equal("sre-oncall", Assert.Single(reloaded.Groups).Id);
        Assert.Equal("SRE On-Call", reloaded.Groups[0].Name);
        Assert.Equal("alice@example.com", Assert.Single(reloaded.Users));
        Assert.False(reloaded.IsEmpty);
    }

    [Fact]
    public void Creators_DefaultIsEmpty_WhichGrantsNobody()
    {
        // A fresh or half-filled policy must not read as "everyone may create".
        Assert.True(new RollbackPolicy().Creators.IsEmpty);
        Assert.True(new RollbackPolicy { CreatorsJson = "" }.Creators.IsEmpty);
        Assert.True(new RollbackPolicy { CreatorsJson = "{}" }.Creators.IsEmpty);
    }
}
