using System.Text.Json;
using System.Text.RegularExpressions;

namespace Platform.Api.Features.ServiceInventory.Documents;

/// <summary>A parsed file, or why it was rejected. Never a partial document.</summary>
public sealed record ReadResult(
    object? Document,
    string? Error,
    int SchemaVersion,
    string Period,
    int? DocumentVersion,
    DateTimeOffset? GeneratedAt,
    bool? Complete)
{
    public bool Ok => Document is not null;

    public static ReadResult Rejected(string error, int schemaVersion = 0) =>
        new(null, error, schemaVersion, "", null, null, null);
}

/// <summary>
/// Reads one published file. A file whose <c>schemaVersion</c> is not supported, or that is not
/// valid, is rejected whole; unknown fields are ignored.
/// </summary>
public static partial class DocumentReader
{
    /// <summary>Supported <c>schemaVersion</c> per feed type.</summary>
    public static readonly IReadOnlyDictionary<string, int> SupportedVersions = new Dictionary<string, int>
    {
        [InventoryFeedTypes.Catalog] = 1,
        [InventoryFeedTypes.Status] = 1,
        [InventoryFeedTypes.Cost] = 2,
    };

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        // An explicit null for a field the model says cannot be null rejects the file instead of
        // being stored and failing later on every request that reads it.
        RespectNullableAnnotations = true,
    };

    public static ReadResult Read(string feedType, byte[] content)
    {
        var type = feedType.Trim().ToLowerInvariant();
        if (!SupportedVersions.TryGetValue(type, out var supported))
            return ReadResult.Rejected($"unknown feed type '{feedType}'");

        int schemaVersion;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ReadResult.Rejected("the file is not a JSON object");
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var v)
                || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out schemaVersion))
                return ReadResult.Rejected("the file has no numeric schemaVersion");
        }
        catch (JsonException)
        {
            return ReadResult.Rejected("the file is not valid JSON");
        }

        if (schemaVersion != supported)
            return ReadResult.Rejected($"unsupported schemaVersion {schemaVersion} (this portal reads {supported})", schemaVersion);

        try
        {
            return type switch
            {
                InventoryFeedTypes.Catalog => ReadCatalog(content, schemaVersion),
                InventoryFeedTypes.Status => ReadStatus(content, schemaVersion),
                _ => ReadCost(content, schemaVersion),
            };
        }
        catch (JsonException ex)
        {
            // Position only: the message of a JsonException may quote the content.
            return ReadResult.Rejected($"the file does not match the format (line {ex.LineNumber + 1})", schemaVersion);
        }
    }

    private static ReadResult ReadCatalog(byte[] content, int schemaVersion)
    {
        if (MissingArray(content, "services") is { } missing) return ReadResult.Rejected(missing, schemaVersion);
        var doc = JsonSerializer.Deserialize<CatalogDocument>(content, Json)!;
        if (HasNull(doc.Services) || HasNull(doc.Teams) || HasNull(doc.Products) || HasNull(doc.Shared))
            return ReadResult.Rejected("a list holds a null entry", schemaVersion);
        if (doc.Services.Any(s => string.IsNullOrWhiteSpace(s.Id)))
            return ReadResult.Rejected("a service has no id", schemaVersion);
        foreach (var service in doc.Services)
        {
            if (service.Environments.Values.Any(e => e is null) || service.Workloads?.Values.Any(w => w is null) == true)
                return ReadResult.Rejected($"service '{Clip(service.Id)}' has a null environment or workload", schemaVersion);
            // The pages read the database bindings: a binding they cannot read rejects the file now
            // (a JsonException, caught by the caller) instead of failing every later request.
            _ = service.DatabaseBindings();
        }
        return new ReadResult(doc, null, schemaVersion, "", doc.CatalogVersion, null, null);
    }

    private static ReadResult ReadStatus(byte[] content, int schemaVersion)
    {
        if (MissingArray(content, "services") is { } missing) return ReadResult.Rejected(missing, schemaVersion);
        var doc = JsonSerializer.Deserialize<StatusDocument>(content, Json)!;
        if (doc.GeneratedAt is null) return ReadResult.Rejected("the file has no generated_at", schemaVersion);
        var azure = doc.Azure;
        if (HasNull(doc.Services)
            || doc.Services.Any(s => HasNull(s.Warnings))
            || (azure is not null && (HasNull(azure.Shared) || HasNull(azure.Bindings) || HasNull(azure.Untagged)
                || azure.Shared.Any(sh => HasNull(sh.Resources) || sh.Resources.Any(r => HasNull(r.Databases))))))
            return ReadResult.Rejected("a list holds a null entry", schemaVersion);
        if (doc.Services.Any(s => string.IsNullOrWhiteSpace(s.Id)))
            return ReadResult.Rejected("a service has no id", schemaVersion);
        return new ReadResult(doc, null, schemaVersion, "", doc.CatalogVersion, doc.GeneratedAt, null);
    }

    private static ReadResult ReadCost(byte[] content, int schemaVersion)
    {
        using (var raw = JsonDocument.Parse(content))
        {
            foreach (var field in new[] { "period", "complete", "generated_at", "catalog_version", "sources", "totals", "services", "shared", "unallocated" })
                if (!raw.RootElement.TryGetProperty(field, out _))
                    return ReadResult.Rejected($"the file has no {field}", schemaVersion);
        }

        var doc = JsonSerializer.Deserialize<CostDocument>(content, Json)!;
        if (HasNull(doc.Sources) || HasNull(doc.Services) || HasNull(doc.Shared) || HasNull(doc.Unallocated.ByScope)
            || doc.Services.Any(s => HasNull(s.Resources) || HasNull(s.SharedFrom))
            || doc.Shared.Any(s => HasNull(s.Resources)))
            return ReadResult.Rejected("a list holds a null entry", schemaVersion);
        if (doc.Services.Any(s => string.IsNullOrWhiteSpace(s.Id)))
            return ReadResult.Rejected("a service has no id", schemaVersion);
        if (!ServiceInventoryOptionsValidator.PeriodPattern().IsMatch(doc.Period))
            return ReadResult.Rejected("period is not YYYY-MM", schemaVersion);
        if (AllMoney(doc).SelectMany(m => m.Keys).FirstOrDefault(k => !CurrencyCode().IsMatch(k)) is { } bad)
            return ReadResult.Rejected($"'{Clip(bad)}' is not a currency code", schemaVersion);
        return new ReadResult(doc, null, schemaVersion, doc.Period, doc.CatalogVersion, doc.GeneratedAt, doc.Complete);
    }

    /// <summary>
    /// A field the file must carry. The models default a missing list to empty, so without this a file
    /// without <c>services</c> would replace the last good copy with an empty inventory. An explicit
    /// <c>[]</c> is accepted.
    /// </summary>
    private static string? MissingArray(byte[] content, string field)
    {
        using var raw = JsonDocument.Parse(content);
        return raw.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? null
            : $"the file has no {field} list";
    }

    /// <summary>Nullable annotations do not cover list elements: <c>[null]</c> is checked here.</summary>
    private static bool HasNull<T>(IEnumerable<T?> items) => items.Any(i => i is null);

    private static string Clip(string value) => value.Length > 12 ? value[..12] + "…" : value;

    private static IEnumerable<Money> AllMoney(CostDocument doc)
    {
        IEnumerable<Money> Totals(CostTotals t) => [t.Total, t.Direct, t.Shared, t.SharedUnsplit, t.Unallocated];
        foreach (var m in Totals(doc.Totals)) yield return m;
        foreach (var s in doc.Sources) foreach (var m in Totals(s.Totals)) yield return m;
        foreach (var s in doc.Services)
        {
            yield return s.Direct; yield return s.Shared; yield return s.Total;
            foreach (var r in s.Resources) yield return r.Cost;
            foreach (var f in s.SharedFrom) yield return f.Cost;
        }
        foreach (var s in doc.Shared)
        {
            yield return s.Cost;
            if (s.Unsplit is not null) yield return s.Unsplit;
            foreach (var r in s.Resources) yield return r.Cost;
        }
        yield return doc.Unallocated.Untagged;
        yield return doc.Unallocated.UnknownTag;
        foreach (var s in doc.Unallocated.ByScope) yield return s.Cost;
    }
}
