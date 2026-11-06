using System.Text.Json;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Features.Catalog;

/// <summary>
/// Evaluates and validates a catalog item's <c>approval.rules</c>.
///
/// <para>Rules are tried in order and the first whose <c>when</c> matches decides; with no match
/// (or no rules) the item's own <c>approval</c> fields apply, so items without rules behave as
/// before. A rule must set <c>required</c>; any other field it leaves out is taken from the
/// top-level block.</para>
///
/// <para>Matching fails closed: a field that wasn't filled in never satisfies <c>equals</c>,
/// <c>in</c> or <c>not_in</c>, and an empty <c>all</c> / <c>any</c> never matches. Write rules
/// that relax approval for known-safe values and keep <c>required: true</c> at the top, so an
/// unexpected value falls through to approval.</para>
/// </summary>
public static class ApprovalRules
{
    private static readonly string[] Strategies = ["any", "all", "quorum"];

    public static ApprovalOutcome Resolve(ApprovalConfig? config, string? inputsJson) =>
        Resolve(config, ReadInputs(inputsJson));

    public static ApprovalOutcome Resolve(
        ApprovalConfig? config, IReadOnlyDictionary<string, IReadOnlyList<string>> inputs)
    {
        if (config is null)
            return new ApprovalOutcome { Required = false };

        var rules = config.Rules ?? [];
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.When is null || !Matches(rule.When, inputs)) continue;

            return new ApprovalOutcome
            {
                Required = rule.Required ?? config.Required,
                Strategy = rule.Strategy ?? config.Strategy,
                QuorumCount = rule.QuorumCount ?? config.QuorumCount,
                ApproverGroup = rule.ApproverGroup ?? config.ApproverGroup,
                TimeoutHours = rule.TimeoutHours ?? config.TimeoutHours,
                EscalationGroup = rule.EscalationGroup ?? config.EscalationGroup,
                RuleIndex = i,
                RuleName = rule.Name,
            };
        }

        return new ApprovalOutcome
        {
            Required = config.Required,
            Strategy = config.Strategy,
            QuorumCount = config.QuorumCount,
            ApproverGroup = config.ApproverGroup,
            TimeoutHours = config.TimeoutHours,
            EscalationGroup = config.EscalationGroup,
        };
    }

    public static bool Matches(ApprovalCondition condition, IReadOnlyDictionary<string, IReadOnlyList<string>> inputs)
    {
        if (condition.All is not null)
            return condition.All.Count > 0 && condition.All.All(c => Matches(c, inputs));
        if (condition.Any is not null)
            return condition.Any.Any(c => Matches(c, inputs));

        if (string.IsNullOrEmpty(condition.Field)
            || !inputs.TryGetValue(condition.Field, out var values)
            || values.Count == 0)
            return false;

        if (condition.EqualsValue is not null)
            return values.Contains(condition.EqualsValue, StringComparer.Ordinal);
        if (condition.In is not null)
            return values.Any(v => condition.In.Contains(v, StringComparer.Ordinal));
        if (condition.NotIn is not null)
            return values.All(v => !condition.NotIn.Contains(v, StringComparer.Ordinal));
        return false;
    }

    /// <summary>
    /// Input values by id as strings: a scalar becomes one value, an array (multi-select) one per
    /// element; null and empty strings are dropped so an unanswered field has no values.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadInputs(string? inputsJson)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(inputsJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(inputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                IEnumerable<string?> values = p.Value.ValueKind == JsonValueKind.Array
                    ? p.Value.EnumerateArray().Select(Scalar)
                    : [Scalar(p.Value)];
                result[p.Name] = values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
            }
        }
        catch (JsonException)
        {
            // Unreadable inputs match no rule, so the top-level approval applies.
        }

        return result;
    }

    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText(),
    };

    /// <summary>Validation errors for <paramref name="config"/>'s rules, for <c>/validate</c>.</summary>
    public static List<string> Validate(ApprovalConfig? config, IReadOnlyCollection<string> inputIds)
    {
        var errors = new List<string>();
        if (config?.Rules is null) return errors;

        for (var i = 0; i < config.Rules.Count; i++)
        {
            var rule = config.Rules[i];
            var path = $"approval.rules[{i}]";

            if (rule is null)
            {
                errors.Add($"{path}: a rule can't be empty");
                continue;
            }

            if (rule.When is null)
                errors.Add($"{path}: 'when' is required");
            else
                ValidateCondition(rule.When, $"{path}.when", inputIds, errors);

            if (rule.Required is null)
                errors.Add($"{path}: 'required' must be set to true or false");

            if (rule.Strategy is not null && !Strategies.Contains(rule.Strategy, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{path}: 'strategy' must be one of {string.Join(", ", Strategies)}");

            var strategy = rule.Strategy ?? config.Strategy;
            var quorum = rule.QuorumCount ?? config.QuorumCount;
            if (rule.Required == true
                && string.Equals(strategy, "quorum", StringComparison.OrdinalIgnoreCase)
                && quorum is not > 0)
                errors.Add($"{path}: the 'quorum' strategy needs a 'quorum_count' above 0");
        }

        return errors;
    }

    private static void ValidateCondition(
        ApprovalCondition c, string path, IReadOnlyCollection<string> inputIds, List<string> errors)
    {
        var hasField = !string.IsNullOrWhiteSpace(c.Field);
        var operators = new[] { c.EqualsValue is not null, c.In is not null, c.NotIn is not null }.Count(x => x);
        var forms = new[] { hasField || operators > 0, c.All is not null, c.Any is not null }.Count(x => x);

        if (forms != 1)
        {
            errors.Add($"{path}: use exactly one of 'field' (with equals, in or not_in), 'all' or 'any'");
            return;
        }

        if (c.All is not null || c.Any is not null)
        {
            var (key, list) = c.All is not null ? ("all", c.All) : ("any", c.Any!);
            if (list.Count == 0)
                errors.Add($"{path}.{key}: needs at least one condition");
            for (var j = 0; j < list.Count; j++)
            {
                if (list[j] is null) errors.Add($"{path}.{key}[{j}]: a condition can't be empty");
                else ValidateCondition(list[j], $"{path}.{key}[{j}]", inputIds, errors);
            }
            return;
        }

        if (!hasField)
            errors.Add($"{path}: 'field' is required with equals, in or not_in");
        else if (!inputIds.Contains(c.Field!))
            errors.Add($"{path}: 'field' '{c.Field}' isn't one of this item's inputs");

        if (operators != 1)
            errors.Add($"{path}: use exactly one of 'equals', 'in' or 'not_in'");
        else if (c.In is { Count: 0 })
            errors.Add($"{path}: 'in' needs at least one value");
        else if (c.NotIn is { Count: 0 })
            errors.Add($"{path}: 'not_in' needs at least one value");
    }
}
