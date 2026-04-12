using System.Security.Cryptography;
using System.Text;
using Platform.Api.Features.Catalog.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Platform.Api.Features.Catalog;

public class CatalogYamlLoader
{
    private readonly string _catalogPath;
    private readonly ILogger<CatalogYamlLoader> _logger;
    private readonly IDeserializer _deserializer;

    public CatalogYamlLoader(IConfiguration config, ILogger<CatalogYamlLoader> logger)
    {
        // Priority: CATALOG_PATH env var → Catalog:Path config → legacy CatalogPath → default
        _catalogPath = Environment.GetEnvironmentVariable("CATALOG_PATH")
            ?? config["Catalog:Path"]
            ?? config["CatalogPath"]
            ?? "catalog/examples";
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        logger.LogInformation("Catalog path resolved to: {CatalogPath} (absolute: {AbsolutePath})",
            _catalogPath, Path.GetFullPath(_catalogPath));
    }

    public List<CatalogDefinition> LoadAll()
    {
        var definitions = new List<CatalogDefinition>();
        var catalogDir = Path.GetFullPath(_catalogPath);

        if (!Directory.Exists(catalogDir))
        {
            _logger.LogWarning("Catalog directory not found: {Path}", catalogDir);
            return definitions;
        }

        foreach (var file in Directory.EnumerateFiles(catalogDir, "*.yaml", SearchOption.AllDirectories))
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var def = _deserializer.Deserialize<CatalogDefinition>(yaml);
                def.YamlContent = yaml;
                def.YamlHash = ComputeHash(yaml);
                def.SourcePath = file;
                definitions.Add(def);
                _logger.LogInformation("Loaded catalog definition: {Id} from {Path}", def.Id, file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse YAML file: {Path}", file);
            }
        }

        return definitions;
    }

    public CatalogDefinition? Load(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var yaml = File.ReadAllText(filePath);
        var def = _deserializer.Deserialize<CatalogDefinition>(yaml);
        def.YamlContent = yaml;
        def.YamlHash = ComputeHash(yaml);
        def.SourcePath = filePath;
        return def;
    }

    /// <summary>
    /// Deserialize a YAML string into a CatalogDefinition. Useful for re-hydrating
    /// executor config from stored YAML content.
    /// </summary>
    public CatalogDefinition? DeserializeDefinition(string yaml)
    {
        try
        {
            return _deserializer.Deserialize<CatalogDefinition>(yaml);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize YAML definition");
            return null;
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// Intermediate model for YAML deserialization (matches YAML structure directly).
/// </summary>
public class CatalogDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Icon { get; set; }
    public List<CatalogInput> Inputs { get; set; } = [];
    public List<CatalogValidation> Validations { get; set; } = [];
    public ApprovalConfig? Approval { get; set; }
    public ExecutorConfig? Executor { get; set; }

    // Set by loader, not from YAML
    public string YamlContent { get; set; } = "";
    public string YamlHash { get; set; } = "";
    public string SourcePath { get; set; } = "";
}
