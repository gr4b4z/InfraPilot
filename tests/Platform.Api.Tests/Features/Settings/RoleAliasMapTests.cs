using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;

namespace Platform.Api.Tests.Features.Settings;

/// <summary>
/// Tests for <see cref="RoleAliasMap"/> and <see cref="RoleConfigValidator"/> — the participant-role
/// twin of the environment alias machinery — and for the completeness rule reading through them.
/// </summary>
public class RoleAliasMapTests
{
    private static RoleConfigDto Role(string key, string[]? aliases = null, GroupRef[]? groups = null)
        => new(key, key, aliases is null ? null : [.. aliases], groups is null ? null : [.. groups]);

    // ── Resolution ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("qa")]
    [InlineData("QA")]
    [InlineData("Qa")]
    [InlineData("q_a")]
    [InlineData("qa-owner")]
    [InlineData("QA Owner")]
    [InlineData("qaOwner")]
    public void Resolve_KeyAndAliasReachTheCanonicalRole_HoweverTheyAreWritten(string sent)
    {
        var map = RoleAliasMap.Build([Role("qa-owner", aliases: ["qa", "q-a"])]);

        Assert.Equal("qa-owner", map.Resolve(sent));
        Assert.True(map.IsConfigured(sent));
    }

    [Fact]
    public void Resolve_UnconfiguredRole_PassesThroughCanonicalised()
    {
        var map = RoleAliasMap.Build([Role("qa-owner", aliases: ["qa"])]);

        Assert.Equal("release-captain", map.Resolve("Release Captain"));
        Assert.False(map.IsConfigured("Release Captain"));
        Assert.Equal("", map.Resolve("  "));
        Assert.False(map.IsConfigured(null));
    }

    [Fact]
    public void Resolve_AKeyAlwaysWinsOverAnotherRolesAlias()
    {
        // A hand-edited row the validator would have refused: "qa" is a role AND an alias. The role
        // keeps answering to its own name rather than vanishing into qa-owner.
        var map = RoleAliasMap.Build([Role("qa-owner", aliases: ["qa"]), Role("qa")]);

        Assert.Equal("qa", map.Resolve("qa"));
    }

    [Fact]
    public void Keys_AreTheConfiguredRolesOnly_NotTheirAliases()
    {
        var map = RoleAliasMap.Build([Role("Reviewer"), Role("qa-owner", aliases: ["qa"]), Role("reviewer")]);

        Assert.Equal(new[] { "reviewer", "qa-owner" }, map.Keys);
    }

    [Fact]
    public void AssigneeGroups_AreFoundThroughAnAlias()
    {
        var group = new GroupRef("grp-1", "QA Team");
        var map = RoleAliasMap.Build([Role("qa-owner", aliases: ["qa"], groups: [group]), Role("reviewer")]);

        Assert.Equal(group, Assert.Single(map.AssigneeGroups("qa")));
        Assert.Equal(group, Assert.Single(map.AssigneeGroups("QA Owner")));
        Assert.Empty(map.AssigneeGroups("reviewer"));
        Assert.Empty(map.AssigneeGroups("nobody"));
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void Clean_CanonicalisesAliasesAndDropsRedundancy()
    {
        var cleaned = RoleConfigValidator.Clean(new RoleConfigDto(
            " qa-owner ", " QA owner ",
            ["QA", "qa", "QA Owner", " ", "Quality Assurance"],
            [new GroupRef(" g1 ", ""), new GroupRef("G1", "dupe"), new GroupRef(" ", "blank")]));

        Assert.Equal("qa-owner", cleaned.Key);
        Assert.Equal("QA owner", cleaned.DisplayName);
        Assert.Equal(new[] { "qa", "quality-assurance" }, cleaned.Aliases);
        Assert.Equal(new GroupRef("g1", "g1"), Assert.Single(cleaned.AssigneeGroups!));
    }

    [Fact]
    public void Validate_AnAliasThatIsAlsoARole_NamesBothAndTheFix()
    {
        var errors = RoleConfigValidator.Validate([Role("qa"), Role("qa-owner", aliases: ["qa"])]);

        var error = Assert.Single(errors);
        Assert.Contains("'qa'", error);
        Assert.Contains("'qa-owner'", error);
        Assert.Contains("Remove the 'qa' role", error);
    }

    [Fact]
    public void Validate_AnAliasOnTwoRoles_IsAmbiguous()
    {
        var errors = RoleConfigValidator.Validate(
            [Role("qa-owner", aliases: ["tester"]), Role("reviewer", aliases: ["tester"])]);

        Assert.Contains("both 'qa-owner' and 'reviewer'", Assert.Single(errors));
    }

    [Fact]
    public void Validate_DistinctAliases_AreFine()
    {
        Assert.Empty(RoleConfigValidator.Validate(
            [Role("qa-owner", aliases: ["qa"]), Role("reviewer", aliases: ["code-reviewer"]), Role("author")]));
    }

    // ── Completeness reads through the map ───────────────────────────────────

    [Fact]
    public void MissingRoles_AParticipantInAnAlias_FillsTheRole()
    {
        var map = RoleAliasMap.Build([Role("qa-owner", aliases: ["qa"])]);
        var participants = new[] { new ParticipantDto("qa", "Quinn", "quinn@example.com") };

        Assert.Equal(new[] { "qa-owner" }, WorkItemRoleRequirements.MissingRoles(participants, ["qa-owner"]));
        Assert.Empty(WorkItemRoleRequirements.MissingRoles(participants, ["qa-owner"], map));
    }

    [Fact]
    public void ResolveParticipants_KeepsOnePersonPerRole_AcrossItsNames()
    {
        var map = RoleAliasMap.Build([Role("qa-owner", aliases: ["qa"])]);
        var references = new List<ReferenceDto>
        {
            new("work-item", Key: "MPT-1", Participants:
            [
                new ParticipantDto("qa", "Jira QA", "jira@example.com"),
                new ParticipantDto("qa-owner", "Owner", "owner@example.com"),
            ]),
        };
        // Promotion-level QA owner never reaches a ticket that already has one under the alias.
        var promotionLevel = new List<PromotionParticipant> { new("qa-owner", "Promo", "promo@example.com") };

        var resolved = WorkItemRoleRequirements.ResolveParticipants(references, promotionLevel, "MPT-1", map);

        Assert.Equal("jira@example.com", Assert.Single(resolved).Email);
    }
}
