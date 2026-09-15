namespace Platform.Api.Infrastructure.Content;

/// <summary>A scored match from <see cref="KeywordSearch"/>.</summary>
public record ScoredMatch<T>(T Item, int Score);

/// <summary>
/// Weighted keyword search over authored content.
/// </summary>
/// <remarks>
/// Deliberately not an embedding search. The corpora are tens of entries, the vocabulary is the
/// platform's own nouns (service names, environment names, event types), and a scored keyword match
/// is debuggable in a way a vector store is not — when the assistant surfaces the wrong topic you
/// can see exactly which term pulled it up. Revisit if a corpus passes a few hundred entries.
/// </remarks>
public static class KeywordSearch
{
    /// <summary>One searchable field of an entry, and how much a hit in it is worth.</summary>
    public record Field<T>(Func<T, IEnumerable<string?>> Select, int Weight);

    public static Field<T> On<T>(Func<T, string?> select, int weight) =>
        new(item => [select(item)], weight);

    public static Field<T> OnMany<T>(Func<T, IEnumerable<string?>> select, int weight) =>
        new(select, weight);

    /// <summary>
    /// Ranks <paramref name="items"/> against <paramref name="query"/>. Entries matching no term at
    /// all are dropped rather than returned with score zero.
    /// </summary>
    public static List<ScoredMatch<T>> Rank<T>(
        IEnumerable<T> items,
        string query,
        IReadOnlyList<Field<T>> fields,
        Func<T, string> tieBreak,
        int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var terms = Tokenize(query);
        if (terms.Count == 0)
            return [];

        var matches = new List<ScoredMatch<T>>();

        foreach (var item in items)
        {
            var score = 0;
            var covered = 0;

            foreach (var term in terms)
            {
                var hitAnyField = false;

                foreach (var field in fields)
                {
                    if (field.Select(item).Any(v => Contains(v, term)))
                    {
                        score += field.Weight;
                        hitAnyField = true;
                    }
                }

                if (hitAnyField) covered++;
            }

            if (covered == 0) continue;

            // An entry matching every term beats one matching a single term in many fields.
            score += covered * 5;
            matches.Add(new ScoredMatch<T>(item, score));
        }

        return [.. matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => tieBreak(m.Item), StringComparer.OrdinalIgnoreCase)
            .Take(limit)];
    }

    private static bool Contains(string? haystack, string term) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a question into meaningful terms. Stop words are dropped so "how do I roll back a
    /// service" scores on "roll"/"back"/"service" rather than on "how"/"do"/"a", which appear in
    /// every phrasing and so separate nothing.
    /// </summary>
    public static List<string> Tokenize(string query)
    {
        var raw = query.Split(
            [' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '"', '\'', '(', ')', '/'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [.. raw
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct()];
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "how", "do", "does", "did", "the", "and", "for", "you", "can", "what", "where", "when",
        "why", "who", "with", "from", "this", "that", "there", "here", "its", "it's", "our",
        "your", "please", "help", "want", "need", "would", "could", "should", "will", "shall",
        "are", "was", "were", "been", "being", "have", "has", "had", "get", "got", "into", "onto",
        "about", "then", "than", "but", "not", "any", "all", "some", "one", "two", "use", "using",
    };
}
