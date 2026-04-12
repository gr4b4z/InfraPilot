using System.Collections.Concurrent;
using System.Text.Json;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;

namespace Platform.Api.Agent;

public class A2UIFormGenerator
{
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public string Generate(CatalogDefinition definition)
    {
        var cacheKey = $"{definition.Id}:{definition.YamlHash}";
        return _cache.GetOrAdd(cacheKey, _ => GenerateInternal(definition));
    }

    private static string GenerateInternal(CatalogDefinition definition)
    {
        var components = new List<object>();

        foreach (var input in definition.Inputs)
        {
            var component = MapToA2UI(input);
            if (component is not null)
                components.Add(component);
        }

        // Add validate button
        components.Add(new
        {
            type = "validate-button",
            id = "__validate",
            label = "Validate",
            dataKey = "__validate",
        });

        var surface = new
        {
            serviceName = definition.Name,
            serviceDescription = definition.Description,
            components,
        };

        return JsonSerializer.Serialize(surface, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static object? MapToA2UI(CatalogInput input)
    {
        var baseProps = new Dictionary<string, object?>
        {
            ["type"] = MapComponentType(input.Component),
            ["id"] = input.Id,
            ["label"] = input.Label,
            ["dataKey"] = input.Id,
            ["required"] = input.Required,
        };

        if (input.Placeholder is not null) baseProps["placeholder"] = input.Placeholder;
        if (input.Validation is not null) baseProps["pattern"] = input.Validation;
        if (input.Default is not null) baseProps["defaultValue"] = input.Default;
        if (input.Source is not null) baseProps["source"] = input.Source;
        if (input.Options is not null) baseProps["options"] = input.Options.Select(o => new { id = o.Id, label = o.Label });
        if (input.Min.HasValue) baseProps["min"] = input.Min;
        if (input.Max.HasValue) baseProps["max"] = input.Max;
        if (input.Step.HasValue) baseProps["step"] = input.Step;
        if (input.Language is not null) baseProps["language"] = input.Language;
        if (input.Accept is not null) baseProps["accept"] = input.Accept;
        if (input.MaxSizeMb.HasValue) baseProps["maxSizeMb"] = input.MaxSizeMb;
        if (input.MaxFiles.HasValue) baseProps["maxFiles"] = input.MaxFiles;

        if (input.VisibleWhen is not null)
        {
            baseProps["visibleWhen"] = new { field = input.VisibleWhen.Field, equals = input.VisibleWhen.Equals };
        }

        return baseProps;
    }

    private static string MapComponentType(string yamlComponent)
    {
        return yamlComponent switch
        {
            "TextInput" => "text-input",
            "Select" => "select",
            "MultiSelect" => "multi-select",
            "Toggle" => "toggle",
            "NumberInput" => "number-input",
            "SecretField" => "secret-field",
            "CodeBlock" => "code-block",
            "KeyValueList" => "key-value-list",
            "ResourcePicker" => "resource-picker",
            "UserPicker" => "user-picker",
            "EnvironmentSelector" => "environment-selector",
            "FileUpload" => "file-upload",
            "TextArea" => "text-area",
            _ => yamlComponent.ToLowerInvariant(),
        };
    }

    public void InvalidateCache(string slug)
    {
        foreach (var key in _cache.Keys.Where(k => k.StartsWith($"{slug}:")))
        {
            _cache.TryRemove(key, out _);
        }
    }
}
