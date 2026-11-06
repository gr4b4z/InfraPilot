using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;

namespace Platform.Api.Tests.Features.Catalog;

public class ApprovalRulesTests
{
    private const string Group = "00000000-0000-4000-8000-0000000000a1";

    /// <summary>The export item's shape: approval by default, waived for dev and test.</summary>
    private static ApprovalConfig FailClosed() => new()
    {
        Required = true,
        ApproverGroup = Group,
        TimeoutHours = 72,
        Rules =
        [
            new ApprovalRule
            {
                Name = "Dev and test need no approval",
                When = new ApprovalCondition { Field = "environment", In = ["dev", "test"] },
                Required = false,
            },
        ],
    };

    // ── Resolve ──

    [Fact]
    public void NoApprovalBlock_NotRequired()
    {
        var outcome = ApprovalRules.Resolve(null, """{"environment":"staging"}""");

        Assert.False(outcome.Required);
        Assert.Null(outcome.RuleIndex);
    }

    [Fact]
    public void NoRules_UsesTopLevel_AsBefore()
    {
        var config = new ApprovalConfig { Required = true, Strategy = "quorum", QuorumCount = 2, ApproverGroup = Group };

        var outcome = ApprovalRules.Resolve(config, """{"environment":"dev"}""");

        Assert.True(outcome.Required);
        Assert.Equal("quorum", outcome.Strategy);
        Assert.Equal(2, outcome.QuorumCount);
        Assert.Equal(Group, outcome.ApproverGroup);
        Assert.Null(outcome.RuleIndex);
    }

    [Theory]
    [InlineData("dev", false, 0)]
    [InlineData("test", false, 0)]
    [InlineData("staging", true, null)]
    [InlineData("prod", true, null)]   // a value the form never offers still needs approval
    [InlineData("Dev", true, null)]    // exact match only
    public void FailClosedRule_WaivesOnlyListedValues(string environment, bool required, int? ruleIndex)
    {
        var outcome = ApprovalRules.Resolve(FailClosed(), $$"""{"environment":"{{environment}}"}""");

        Assert.Equal(required, outcome.Required);
        Assert.Equal(ruleIndex, outcome.RuleIndex);
    }

    [Fact]
    public void MatchedRule_KeepsItsNameAndInheritsTheRest()
    {
        var outcome = ApprovalRules.Resolve(FailClosed(), """{"environment":"dev"}""");

        Assert.Equal("Dev and test need no approval", outcome.RuleName);
        Assert.Equal(Group, outcome.ApproverGroup);
        Assert.Equal(72, outcome.TimeoutHours);
    }

    [Fact]
    public void StagingRule_RoutesOnlyStagingToApproval()
    {
        // The ticket's acceptance case: an "environment in [staging]" rule.
        var config = new ApprovalConfig
        {
            Required = false,
            Rules =
            [
                new ApprovalRule
                {
                    When = new ApprovalCondition { Field = "environment", In = ["staging"] },
                    Required = true,
                    ApproverGroup = Group,
                },
            ],
        };

        Assert.True(ApprovalRules.Resolve(config, """{"environment":"staging"}""").Required);
        Assert.Equal(Group, ApprovalRules.Resolve(config, """{"environment":"staging"}""").ApproverGroup);
        Assert.False(ApprovalRules.Resolve(config, """{"environment":"dev"}""").Required);
    }

    [Fact]
    public void FirstMatchWins()
    {
        var config = new ApprovalConfig
        {
            Required = false,
            Rules =
            [
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", EqualsValue = "staging" }, Required = true, ApproverGroup = "first" },
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", EqualsValue = "staging" }, Required = true, ApproverGroup = "second" },
            ],
        };

        var outcome = ApprovalRules.Resolve(config, """{"environment":"staging"}""");

        Assert.Equal("first", outcome.ApproverGroup);
        Assert.Equal(0, outcome.RuleIndex);
    }

    [Fact]
    public void MissingField_NeverMatches_EvenNotIn()
    {
        var config = new ApprovalConfig
        {
            Required = true,
            Rules = [new ApprovalRule { When = new ApprovalCondition { Field = "environment", NotIn = ["prod"] }, Required = false }],
        };

        Assert.True(ApprovalRules.Resolve(config, "{}").Required);
        Assert.True(ApprovalRules.Resolve(config, """{"environment":""}""").Required);
        Assert.True(ApprovalRules.Resolve(config, """{"environment":null}""").Required);
        Assert.False(ApprovalRules.Resolve(config, """{"environment":"dev"}""").Required);
        Assert.True(ApprovalRules.Resolve(config, """{"environment":"prod"}""").Required);
    }

    [Fact]
    public void UnreadableInputs_FallBackToTopLevel()
    {
        Assert.True(ApprovalRules.Resolve(FailClosed(), "not json").Required);
        Assert.True(ApprovalRules.Resolve(FailClosed(), (string?)null).Required);
    }

    // ── Matches ──

    private static bool Match(ApprovalCondition c, string json) =>
        ApprovalRules.Matches(c, ApprovalRules.ReadInputs(json));

    [Fact]
    public void Equals_ComparesBooleansAndNumbersAsText()
    {
        Assert.True(Match(new ApprovalCondition { Field = "urgent", EqualsValue = "true" }, """{"urgent":true}"""));
        Assert.False(Match(new ApprovalCondition { Field = "urgent", EqualsValue = "true" }, """{"urgent":false}"""));
        Assert.True(Match(new ApprovalCondition { Field = "size", EqualsValue = "3" }, """{"size":3}"""));
    }

    [Fact]
    public void ArrayValues_MatchAnyElement_AndNotInChecksAll()
    {
        const string json = """{"regions":["westeurope","eastus"]}""";

        Assert.True(Match(new ApprovalCondition { Field = "regions", EqualsValue = "eastus" }, json));
        Assert.True(Match(new ApprovalCondition { Field = "regions", In = ["eastus", "uksouth"] }, json));
        Assert.False(Match(new ApprovalCondition { Field = "regions", NotIn = ["eastus"] }, json));
        Assert.True(Match(new ApprovalCondition { Field = "regions", NotIn = ["uksouth"] }, json));
    }

    [Fact]
    public void AllAndAny_Combine()
    {
        var both = new ApprovalCondition
        {
            All =
            [
                new ApprovalCondition { Field = "environment", EqualsValue = "staging" },
                new ApprovalCondition
                {
                    Any =
                    [
                        new ApprovalCondition { Field = "database", In = ["billing", "identity"] },
                        new ApprovalCondition { Field = "urgent", EqualsValue = "true" },
                    ],
                },
            ],
        };

        Assert.True(Match(both, """{"environment":"staging","database":"billing"}"""));
        Assert.True(Match(both, """{"environment":"staging","database":"other","urgent":true}"""));
        Assert.False(Match(both, """{"environment":"staging","database":"other"}"""));
        Assert.False(Match(both, """{"environment":"dev","database":"billing"}"""));
    }

    [Fact]
    public void EmptyAllOrAny_NeverMatch()
    {
        Assert.False(Match(new ApprovalCondition { All = [] }, """{"environment":"dev"}"""));
        Assert.False(Match(new ApprovalCondition { Any = [] }, """{"environment":"dev"}"""));
    }

    // ── Validate ──

    private static readonly string[] Inputs = ["environment", "database"];

    [Fact]
    public void Validate_NoRules_NoErrors()
    {
        Assert.Empty(ApprovalRules.Validate(null, Inputs));
        Assert.Empty(ApprovalRules.Validate(new ApprovalConfig { Required = true }, Inputs));
        Assert.Empty(ApprovalRules.Validate(FailClosed(), Inputs));
    }

    [Fact]
    public void Validate_ReportsEachProblem()
    {
        var config = new ApprovalConfig
        {
            Rules =
            [
                new ApprovalRule { Required = false },                                                          // no when
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", In = ["dev"] } },     // no required
                new ApprovalRule { When = new ApprovalCondition { Field = "region", EqualsValue = "x" }, Required = true }, // unknown field
                new ApprovalRule { When = new ApprovalCondition { Field = "environment" }, Required = true },  // no operator
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", EqualsValue = "a", In = ["b"] }, Required = true }, // two operators
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", In = [] }, Required = true },     // empty in
                new ApprovalRule { When = new ApprovalCondition { All = [] }, Required = true },                 // empty all
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", EqualsValue = "a", Any = [] }, Required = true }, // two forms
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", EqualsValue = "a" }, Required = true, Strategy = "majority" },
                new ApprovalRule { When = new ApprovalCondition { Field = "environment", EqualsValue = "a" }, Required = true, Strategy = "quorum" },
            ],
        };

        var errors = ApprovalRules.Validate(config, Inputs);

        Assert.Contains(errors, e => e.StartsWith("approval.rules[0]") && e.Contains("'when' is required"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[1]") && e.Contains("'required'"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[2].when") && e.Contains("'region'"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[3].when") && e.Contains("exactly one of 'equals'"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[4].when") && e.Contains("exactly one of 'equals'"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[5].when") && e.Contains("'in' needs at least one value"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[6].when.all") && e.Contains("at least one condition"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[7].when") && e.Contains("'all' or 'any'"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[8]") && e.Contains("'strategy'"));
        Assert.Contains(errors, e => e.StartsWith("approval.rules[9]") && e.Contains("quorum_count"));
    }

    [Fact]
    public void Validate_ChecksNestedConditions()
    {
        var config = new ApprovalConfig
        {
            Rules =
            [
                new ApprovalRule
                {
                    When = new ApprovalCondition { Any = [new ApprovalCondition { Field = "nope", EqualsValue = "x" }] },
                    Required = true,
                },
            ],
        };

        var error = Assert.Single(ApprovalRules.Validate(config, Inputs));
        Assert.StartsWith("approval.rules[0].when.any[0]", error);
    }
}
