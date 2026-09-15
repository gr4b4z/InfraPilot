using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Platform.Api.Infrastructure.Content;

/// <summary>
/// Reads a directory of authored YAML documents into typed entries. Shared by the assistant's three
/// authored corpora — guides, platform knowledge and failure playbooks — which differ only in their
/// shape and their default directory.
/// </summary>
/// <remarks>
/// Deliberately tolerant: a missing directory logs and yields nothing, and one malformed file is
/// skipped rather than taking the whole corpus (and with it the portal) down. An installation that
/// has not authored any content should still boot.
/// </remarks>
public class YamlCorpusLoader
{
    private readonly ILogger _logger;
    private readonly IDeserializer _deserializer;

    public YamlCorpusLoader(ILogger logger)
    {
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Resolves a corpus directory from, in order: an environment variable, a configuration key, and
    /// a fallback. Mirrors how the catalog path is resolved so operators only learn one rule.
    /// </summary>
    public static string ResolvePath(IConfiguration config, string envVar, string configKey, string fallback)
        => Environment.GetEnvironmentVariable(envVar) ?? config[configKey] ?? fallback;

    /// <summary>
    /// Loads every <c>*.yaml</c> under <paramref name="path"/>, recursively.
    /// </summary>
    /// <param name="label">Human name for the corpus, used in log messages ("guide", "knowledge topic").</param>
    /// <param name="identify">Returns an entry's id, for duplicate detection and error messages.</param>
    /// <param name="validate">Returns an error string when an entry is unusable, or null when it is fine.</param>
    public List<T> Load<T>(
        string path,
        string label,
        Func<T, string?> identify,
        Func<T, string?>? validate = null) where T : class
    {
        var entries = new List<T>();
        var dir = Path.GetFullPath(path);

        _logger.LogInformation("{Label} path resolved to: {Path} (absolute: {AbsolutePath})", label, path, dir);

        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("{Label} directory not found: {Path} — the assistant will have none", label, dir);
            return entries;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
        {
            try
            {
                var entry = _deserializer.Deserialize<T>(File.ReadAllText(file));
                if (entry is null)
                {
                    _logger.LogError("Empty {Label} file, skipping: {Path}", label, file);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(identify(entry)))
                {
                    _logger.LogError("{Label} file has no id, skipping: {Path}", label, file);
                    continue;
                }

                var problem = validate?.Invoke(entry);
                if (problem is not null)
                {
                    _logger.LogError("{Label} '{Id}' is invalid ({Problem}), skipping: {Path}",
                        label, identify(entry), problem, file);
                    continue;
                }

                entries.Add(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse {Label} YAML: {Path}", label, file);
            }
        }

        foreach (var dup in entries.GroupBy(identify, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            _logger.LogError("Duplicate {Label} id '{Id}' — only the first is reachable", label, dup.Key);

        _logger.LogInformation("Loaded {Count} {Label}(s) from {Path}", entries.Count, label, dir);
        return entries;
    }
}
