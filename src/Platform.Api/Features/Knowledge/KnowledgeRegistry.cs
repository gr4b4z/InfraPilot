using Platform.Api.Infrastructure.Content;

namespace Platform.Api.Features.Knowledge;

/// <summary>
/// In-memory index of the platform knowledge corpus, loaded once at startup. Singleton: the corpus
/// is read-only and identical for every caller.
/// </summary>
public class KnowledgeRegistry
{
    private readonly List<KnowledgeTopic> _topics;
    private readonly Dictionary<string, KnowledgeTopic> _byId;

    private static readonly IReadOnlyList<KeywordSearch.Field<KnowledgeTopic>> Fields =
    [
        KeywordSearch.On<KnowledgeTopic>(t => t.Title, 10),
        KeywordSearch.OnMany<KnowledgeTopic>(t => t.Aliases, 8),
        KeywordSearch.OnMany<KnowledgeTopic>(t => t.Tags, 7),
        KeywordSearch.On<KnowledgeTopic>(t => t.Group, 4),
        KeywordSearch.On<KnowledgeTopic>(t => t.Summary, 3),
        // The body is long, so a hit there is weak evidence on its own — it breaks ties rather than
        // deciding them, which stops a topic that merely mentions "prod" from outranking one about it.
        KeywordSearch.On<KnowledgeTopic>(t => t.Body, 1),
    ];

    public KnowledgeRegistry(IConfiguration config, ILogger<KnowledgeRegistry> logger)
    {
        var path = YamlCorpusLoader.ResolvePath(config, "KNOWLEDGE_PATH", "Knowledge:Path", "knowledge");
        _topics = new YamlCorpusLoader(logger).Load<KnowledgeTopic>(
            path,
            "knowledge topic",
            t => t.Id,
            t => string.IsNullOrWhiteSpace(t.Body) ? "no body" : null);

        _byId = _topics
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<KnowledgeTopic> All => _topics;

    public KnowledgeTopic? GetById(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _byId.GetValueOrDefault(id.Trim());

    public List<ScoredMatch<KnowledgeTopic>> Search(string query, int limit = 4) =>
        KeywordSearch.Rank(_topics, query, Fields, t => t.Title, limit);
}
