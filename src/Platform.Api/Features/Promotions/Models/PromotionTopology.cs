namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Declarative description of the environment graph promotions flow through.
///
/// <para>Stored as a single JSON blob under the <c>promotions.topology</c> platform setting.
/// Environments are the nodes; edges are directed <c>from</c> → <c>to</c> pairs that define
/// which source → target promotions are even attempted when a deploy event is ingested.</para>
///
/// <para>An empty topology (the install default) means "no promotions" — the ingest hook finds
/// no downstream edges and creates no candidates, which is the safe default while the feature
/// is being bootstrapped.</para>
/// </summary>
public record PromotionTopology(
    IReadOnlyList<string> Environments,
    IReadOnlyList<PromotionEdge> Edges)
{
    public static PromotionTopology Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<PromotionEdge>());

    /// <summary>All target environments reachable from <paramref name="sourceEnv"/>.</summary>
    public IEnumerable<string> NextFrom(string sourceEnv) =>
        Edges.Where(e => string.Equals(e.From, sourceEnv, StringComparison.OrdinalIgnoreCase))
             .Select(e => e.To);
}

public record PromotionEdge(string From, string To);
