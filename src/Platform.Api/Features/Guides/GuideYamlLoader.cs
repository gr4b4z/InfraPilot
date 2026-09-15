using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Platform.Api.Features.Guides;

/// <summary>
/// Reads guide definitions from YAML on disk. Mirrors <see cref="Catalog.CatalogYamlLoader"/> —
/// same resolution order, same tolerance for a missing directory, same per-file error isolation so
/// one malformed guide cannot take the portal down.
/// </summary>
public class GuideYamlLoader
{
    private readonly string _guidesPath;
    private readonly ILogger<GuideYamlLoader> _logger;
    private readonly IDeserializer _deserializer;

    public GuideYamlLoader(IConfiguration config, ILogger<GuideYamlLoader> logger)
    {
        // Priority: GUIDES_PATH env var → Guides:Path config → default.
        _guidesPath = Environment.GetEnvironmentVariable("GUIDES_PATH")
            ?? config["Guides:Path"]
            ?? "guides";
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        logger.LogInformation("Guides path resolved to: {GuidesPath} (absolute: {AbsolutePath})",
            _guidesPath, Path.GetFullPath(_guidesPath));
    }

    public List<GuideDefinition> LoadAll()
    {
        var guides = new List<GuideDefinition>();
        var guidesDir = Path.GetFullPath(_guidesPath);

        if (!Directory.Exists(guidesDir))
        {
            _logger.LogWarning("Guides directory not found: {Path} — the assistant will have no walkthroughs", guidesDir);
            return guides;
        }

        foreach (var file in Directory.EnumerateFiles(guidesDir, "*.yaml", SearchOption.AllDirectories))
        {
            try
            {
                var guide = _deserializer.Deserialize<GuideDefinition>(File.ReadAllText(file));
                if (guide is null || string.IsNullOrWhiteSpace(guide.Id))
                {
                    _logger.LogError("Guide file has no id, skipping: {Path}", file);
                    continue;
                }

                if (guide.Steps.Count == 0)
                {
                    _logger.LogError("Guide '{Id}' has no steps, skipping: {Path}", guide.Id, file);
                    continue;
                }

                guide.SourcePath = file;
                guides.Add(guide);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse guide YAML: {Path}", file);
            }
        }

        var duplicates = guides.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var dup in duplicates)
            _logger.LogError("Duplicate guide id '{Id}' — only the first will be reachable by start_guide", dup);

        _logger.LogInformation("Loaded {Count} guide(s) from {Path}", guides.Count, guidesDir);
        return guides;
    }
}
