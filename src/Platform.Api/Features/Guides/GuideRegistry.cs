namespace Platform.Api.Features.Guides;

/// <summary>Result of matching a guide against a user's question, with its relevance score.</summary>
public record GuideMatch(GuideDefinition Guide, int Score);

/// <summary>
/// In-memory index of every guide, loaded once at startup. Registered as a singleton because the
/// corpus is small, read-only, and identical for every caller — permission tailoring happens at
/// read time against the calling user rather than by holding per-user copies.
/// </summary>
public class GuideRegistry
{
    private readonly List<GuideDefinition> _guides;
    private readonly Dictionary<string, GuideDefinition> _byId;

    public GuideRegistry(GuideYamlLoader loader)
    {
        _guides = loader.LoadAll();
        _byId = _guides
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<GuideDefinition> All => _guides;

    public GuideDefinition? GetById(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _byId.GetValueOrDefault(id.Trim());

    /// <summary>
    /// Guides that apply to a page the user is currently on.
    /// </summary>
    /// <remarks>
    /// Matched deterministically rather than left to the model to infer from the path, because
    /// "what can I do here?" has an exact answer and guessing it wrong is worse than saying nothing.
    /// A guide matches when the current path is its route or sits beneath it, so
    /// <c>/promotions/{id}</c> still offers the promotion guides, and when any step navigates there,
    /// so a guide that crosses into Settings is offered on the page it ends on too.
    /// </remarks>
    public List<GuideDefinition> ForRoute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];

        var current = Normalize(path);

        return [.. _guides
            .Where(g => Covers(g.Route, current) || g.Steps.Any(s => Covers(s.Route, current)))
            // Deepest route first: on /settings/rollbacks the policy guide is a better answer than
            // one that merely mentions /settings.
            .OrderByDescending(g => Normalize(g.Route).Count(c => c == '/'))
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool Covers(string? guideRoute, string currentPath)
    {
        if (string.IsNullOrWhiteSpace(guideRoute)) return false;

        var route = Normalize(guideRoute);
        if (route.Length == 0) return false;

        return currentPath.Equals(route, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith(route + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => '/' + path.Trim().Trim('/');

    /// <summary>
    /// Keyword search over titles, aliases, summaries and step text. Deliberately not an embedding
    /// search: the corpus is tens of entries, the vocabulary is the product's own nouns, and a
    /// scored keyword match is debuggable in a way a vector store is not. Revisit if the corpus
    /// grows past a few hundred guides.
    /// </summary>
    public List<GuideMatch> Search(string query, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var terms = Tokenize(query);
        if (terms.Count == 0)
            return [];

        var matches = new List<GuideMatch>();

        foreach (var guide in _guides)
        {
            var score = 0;

            foreach (var term in terms)
            {
                // Weighted by how deliberately the author put the word there: an alias is an
                // explicit "people ask it this way", a step body is incidental.
                if (Contains(guide.Title, term)) score += 10;
                if (guide.Aliases.Any(a => Contains(a, term))) score += 8;
                if (Contains(guide.Group, term)) score += 4;
                if (Contains(guide.Summary, term)) score += 3;
                if (guide.Steps.Any(s => Contains(s.Text, term))) score += 1;
            }

            // A guide matching every term beats one matching a single term repeatedly.
            var covered = terms.Count(term =>
                Contains(guide.Title, term)
                || guide.Aliases.Any(a => Contains(a, term))
                || Contains(guide.Group, term)
                || Contains(guide.Summary, term)
                || guide.Steps.Any(s => Contains(s.Text, term)));

            if (covered == 0)
                continue;

            score += covered * 5;
            matches.Add(new GuideMatch(guide, score));
        }

        return [.. matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Guide.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)];
    }

    private static bool Contains(string? haystack, string term) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a question into meaningful terms. Stop words are dropped so that "how do I roll back
    /// a service" scores on "roll"/"back"/"service" rather than being dominated by "how"/"do"/"a",
    /// which appear in every phrasing and so separate nothing.
    /// </summary>
    private static List<string> Tokenize(string query)
    {
        var raw = query.Split([' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '"', '\'', '(', ')', '/'],
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
