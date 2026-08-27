using System.Text.Json;
using Platform.Api.Features.Webhooks.Models;

namespace Platform.Api.Features.Webhooks;

/// <summary>
/// The subscription-side filter dimensions — product, service, environment — each a set rather than
/// a single value, because one receiver usually cares about a handful of products or environments and
/// the alternative is a duplicate subscription per value (same URL, same template, same secret, one
/// word different).
///
/// <para><b>Empty means "any", and a dimension is only tested when the event carries it.</b> Not every
/// event has all three: a release note or a rollback is product-wide with no single service, and a
/// work-item approval names no service either. Testing a service filter against those would silently
/// mute a subscription for events that never disagreed with it, so an absent dimension passes and only
/// the dimensions the event actually states are matched.</para>
///
/// <para>Matching is exact and case-sensitive, as it was when each dimension held one value: products,
/// services and environments reach the dispatcher already canonicalised (environments through
/// <c>EnvironmentAliasResolver</c>, services through <c>ServiceProductOverrideService</c>), so a
/// mismatch in case is a mismatch in the stored key, not a near miss.</para>
/// </summary>
public static class WebhookSubscriptionFilters
{
    /// <summary>
    /// How many values one dimension may hold. High enough never to be met by a real subscription,
    /// low enough that a runaway client cannot grow the row without bound.
    /// </summary>
    public const int MaxValuesPerDimension = 100;

    /// <summary>Per-value length cap, mirroring the columns these values are matched against.</summary>
    public const int MaxValueLength = 200;

    /// <summary>
    /// Reads a stored dimension. Anything unparseable reads as "no filter" rather than throwing — a
    /// corrupt filter column must not take the whole dispatch down with it.
    /// </summary>
    public static string[] Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Normalises a dimension for storage: trims, drops blanks, and de-duplicates case-insensitively
    /// while keeping the first spelling and the caller's order, so the UI shows back what was typed.
    /// </summary>
    public static string[] Normalize(IEnumerable<string?>? values)
    {
        if (values is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values
            .Select(v => v?.Trim() ?? "")
            .Where(v => v.Length > 0 && seen.Add(v))
            .ToArray();
    }

    public static string Serialize(IEnumerable<string?>? values)
        => JsonSerializer.Serialize(Normalize(values));

    /// <summary>
    /// Rejects a dimension that is too long or too wide, naming the field so the operator knows which
    /// of the three to fix.
    /// </summary>
    public static string? Validate(string field, string[] values)
    {
        if (values.Length > MaxValuesPerDimension)
            return $"filters.{field} accepts at most {MaxValuesPerDimension} values";
        var tooLong = values.FirstOrDefault(v => v.Length > MaxValueLength);
        return tooLong is null
            ? null
            : $"filters.{field} values must be {MaxValueLength} characters or fewer";
    }

    /// <summary>
    /// Whether an event value clears one dimension: an empty filter matches everything, and an event
    /// that does not state the dimension is never blocked by it.
    /// </summary>
    public static bool Matches(string[] allowed, string? eventValue)
        => allowed.Length == 0
            || string.IsNullOrEmpty(eventValue)
            || allowed.Contains(eventValue, StringComparer.Ordinal);

    /// <summary>
    /// Whether a subscription's three dimensions all clear the event. A null
    /// <paramref name="filters"/> means the event states none of them — a subscription filter cannot
    /// narrow an event that carries no product, service or environment at all.
    /// </summary>
    public static bool Matches(WebhookSubscription subscription, WebhookEventFilters? filters)
    {
        if (filters is null) return true;
        return Matches(Parse(subscription.FilterProductsJson), filters.Product)
            && Matches(Parse(subscription.FilterServicesJson), filters.Service)
            && Matches(Parse(subscription.FilterEnvironmentsJson), filters.Environment);
    }
}
