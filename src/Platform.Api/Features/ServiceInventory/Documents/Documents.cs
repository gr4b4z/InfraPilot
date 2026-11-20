using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Api.Features.ServiceInventory.Documents;

// The three published files, with only the fields the portal uses. Unknown fields are ignored:
// producers add fields without a new schemaVersion. Field names are the producers' (snake_case).
// Parts whose shape the portal does not interpret are kept as raw JSON and passed through.

/// <summary>Amount per ISO 4217 currency. Never summed across keys; an empty object is zero.</summary>
public sealed class Money : Dictionary<string, decimal>
{
    public Money() : base(StringComparer.Ordinal) { }
    public Money(IDictionary<string, decimal> values) : base(values, StringComparer.Ordinal) { }
}

// ── catalog.json (schemaVersion 1) ───────────────────────────────────────────────────

public sealed record CatalogDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("catalog_version")] public int? CatalogVersion { get; init; }
    [JsonPropertyName("content_hash")] public string? ContentHash { get; init; }
    [JsonPropertyName("services")] public List<CatalogService> Services { get; init; } = [];
    [JsonPropertyName("teams")] public List<CatalogTeam> Teams { get; init; } = [];
    [JsonPropertyName("products")] public List<CatalogProduct> Products { get; init; } = [];
    [JsonPropertyName("shared")] public List<CatalogShared> Shared { get; init; } = [];
}

public sealed record CatalogService
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("product")] public string? Product { get; init; }
    [JsonPropertyName("team")] public string? Team { get; init; }
    [JsonPropertyName("lifecycle")] public string? Lifecycle { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("repo")] public string? Repo { get; init; }
    [JsonPropertyName("tier")] public string? Tier { get; init; }
    [JsonPropertyName("exposure")] public string? Exposure { get; init; }
    [JsonPropertyName("customer_facing")] public bool? CustomerFacing { get; init; }
    [JsonPropertyName("replaced_by")] public string? ReplacedBy { get; init; }
    [JsonPropertyName("retired_at")] public string? RetiredAt { get; init; }
    [JsonPropertyName("environments")] public Dictionary<string, CatalogEnvironment> Environments { get; init; } = [];
    [JsonPropertyName("workloads")] public Dictionary<string, CatalogWorkload>? Workloads { get; init; }
    /// <summary>
    /// <c>postgres</c> / <c>mssql</c> lists, <c>environments.&lt;env&gt;</c> overrides and other kinds
    /// (<c>serviceBus</c>, <c>storageAccount</c>, …). Kept raw; <see cref="DatabaseBindings"/> reads
    /// the database lists.
    /// </summary>
    [JsonPropertyName("bindings")] public JsonElement? Bindings { get; init; }
    [JsonPropertyName("links")] public JsonElement? Links { get; init; }

    private static readonly JsonSerializerOptions StrictJson = new() { RespectNullableAnnotations = true };

    /// <summary>
    /// Absent while the service's own file is missing or invalid. Throws <see cref="JsonException"/>
    /// for a binding that does not match the format; <see cref="DocumentReader"/> calls it once so
    /// such a file is rejected before it is stored.
    /// </summary>
    public IReadOnlyList<DatabaseBinding> DatabaseBindings()
    {
        var list = new List<DatabaseBinding>();
        if (Bindings is not { ValueKind: JsonValueKind.Object } bindings) return list;
        foreach (var kind in DatabaseKinds)
        {
            if (!bindings.TryGetProperty(kind, out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                var binding = entry.Deserialize<DatabaseBinding>(StrictJson);
                if (binding is not null && !string.IsNullOrEmpty(binding.Database)) list.Add(binding with { Kind = kind });
            }
        }

        // bindings.environments.<env>: the same lists, for one environment. An entry there for a
        // database declared above replaces it in that environment (another server, say); any other
        // is a database only that environment has. An override this does not understand is left out
        // rather than failing the file: its shape is not part of the format the reader enforces.
        var overrides = new List<(string Environment, DatabaseBinding Binding)>();
        if (bindings.TryGetProperty("environments", out var environments) && environments.ValueKind == JsonValueKind.Object)
        {
            foreach (var environment in environments.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
            foreach (var kind in DatabaseKinds)
            {
                if (!environment.Value.TryGetProperty(kind, out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
                {
                    DatabaseBinding? binding;
                    try { binding = entry.Deserialize<DatabaseBinding>(StrictJson); }
                    catch (JsonException) { continue; }
                    if (binding is not null && !string.IsNullOrEmpty(binding.Database))
                        overrides.Add((environment.Name, binding with { Kind = kind }));
                }
            }
        }
        if (overrides.Count == 0) return list;

        static bool SameDatabase(DatabaseBinding a, DatabaseBinding b) =>
            a.Kind == b.Kind && string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase);
        static bool SameServer(DatabaseBinding a, DatabaseBinding b) =>
            string.Equals(a.Server ?? "", b.Server ?? "", StringComparison.OrdinalIgnoreCase);

        var result = list
            .Select(top => top with
            {
                NotInEnvironments = overrides.Where(o => SameDatabase(o.Binding, top) && !SameServer(o.Binding, top))
                    .Select(o => o.Environment).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .ToList();
        foreach (var group in overrides
                     .Where(o => !list.Any(top => SameDatabase(o.Binding, top) && SameServer(o.Binding, top)))
                     .GroupBy(o => (o.Binding.Kind, Database: o.Binding.Database.ToLowerInvariant(), Server: (o.Binding.Server ?? "").ToLowerInvariant())))
        {
            result.Add(group.First().Binding with
            {
                OnlyInEnvironments = group.Select(o => o.Environment).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            });
        }
        return result;
    }

    private static readonly string[] DatabaseKinds = ["postgres", "mssql"];
}

public sealed record CatalogEnvironment
{
    [JsonPropertyName("aks")] public CatalogAks? Aks { get; init; }
}

public sealed record CatalogAks
{
    [JsonPropertyName("cluster")] public string? Cluster { get; init; }
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
}

public sealed record CatalogWorkload
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
}

public sealed record DatabaseBinding
{
    [JsonIgnore] public string Kind { get; init; } = "";
    [JsonPropertyName("database")] public string Database { get; init; } = "";
    /// <summary>The id of a <c>shared[]</c> entry, or the word <c>default</c>.</summary>
    [JsonPropertyName("server")] public string? Server { get; init; }
    [JsonPropertyName("owner")] public bool Owner { get; init; }
    [JsonPropertyName("workloads")] public List<string> Workloads { get; init; } = [];
    /// <summary>Set for a database declared for some environments only; null: every environment.</summary>
    [JsonIgnore] public IReadOnlyList<string>? OnlyInEnvironments { get; init; }
    /// <summary>Environments that declare this database on another server instead.</summary>
    [JsonIgnore] public IReadOnlyList<string> NotInEnvironments { get; init; } = [];
}

public sealed record CatalogTeam
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("lead")] public string? Lead { get; init; }
    [JsonPropertyName("deputy")] public string? Deputy { get; init; }
    [JsonPropertyName("github_team")] public string? GithubTeam { get; init; }
    [JsonPropertyName("entra_group")] public string? EntraGroup { get; init; }
    [JsonPropertyName("lifecycle")] public string? Lifecycle { get; init; }
    [JsonPropertyName("products")] public List<string> Products { get; init; } = [];
}

public sealed record CatalogProduct
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("lifecycle")] public string? Lifecycle { get; init; }
    [JsonPropertyName("business_owner")] public string? BusinessOwner { get; init; }
    [JsonPropertyName("technical_owner")] public string? TechnicalOwner { get; init; }
}

public sealed record CatalogShared
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("owner")] public string? Owner { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("split")] public JsonElement? Split { get; init; }
}

// ── status.json (schemaVersion 1) ────────────────────────────────────────────────────

public sealed record StatusDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("catalog_version")] public int? CatalogVersion { get; init; }
    [JsonPropertyName("generated_at")] public DateTimeOffset? GeneratedAt { get; init; }
    [JsonPropertyName("services")] public List<StatusService> Services { get; init; } = [];
    /// <summary>Present only when Azure was scanned; kept unchanged when a later scan fails.</summary>
    [JsonPropertyName("azure")] public StatusAzure? Azure { get; init; }
    /// <summary>Candidates seen in release repositories with no catalog entry. Not services.</summary>
    [JsonPropertyName("discovered")] public JsonElement? Discovered { get; init; }
}

public sealed record StatusService
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("stale")] public bool Stale { get; init; }
    [JsonPropertyName("unverified")] public bool Unverified { get; init; }
    [JsonPropertyName("warnings")] public List<StatusWarning> Warnings { get; init; } = [];
    /// <summary><c>registered_at</c> and the onboarding steps, passed through as published.</summary>
    [JsonPropertyName("onboarding")] public JsonElement? Onboarding { get; init; }
    [JsonPropertyName("freshness")] public JsonElement? Freshness { get; init; }
}

public sealed record StatusWarning
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record StatusAzure
{
    [JsonPropertyName("observed_at")] public DateTimeOffset? ObservedAt { get; init; }
    [JsonPropertyName("subscriptions")] public List<string> Subscriptions { get; init; } = [];
    /// <summary>Resources carrying the <c>service</c> tag; the entry shape is passed through.</summary>
    [JsonPropertyName("services")] public List<JsonElement> Services { get; init; } = [];
    [JsonPropertyName("shared")] public List<StatusShared> Shared { get; init; } = [];
    [JsonPropertyName("bindings")] public List<StatusBinding> Bindings { get; init; } = [];
    [JsonPropertyName("unknown_tags")] public JsonElement? UnknownTags { get; init; }
    [JsonPropertyName("untagged")] public List<StatusUntagged> Untagged { get; init; } = [];
}

public sealed record StatusShared
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    /// <summary><c>ok</c>, <c>ambiguous</c> or <c>unresolved</c> (not observed).</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("resources")] public List<StatusResource> Resources { get; init; } = [];
}

public sealed record StatusResource
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("env")] public string? Env { get; init; }
    [JsonPropertyName("databases")] public List<StatusDatabase> Databases { get; init; } = [];
}

public sealed record StatusDatabase
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    /// <summary><c>linked</c>, <c>unowned</c>, <c>unclaimed</c> or <c>multiple_owners</c>.</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("owner")] public string? Owner { get; init; }
    [JsonPropertyName("owners")] public List<string>? Owners { get; init; }
    [JsonPropertyName("used_by")] public List<string>? UsedBy { get; init; }
}

public sealed record StatusBinding
{
    [JsonPropertyName("service")] public string? Service { get; init; }
    [JsonPropertyName("environment")] public string? Environment { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("database")] public string? Database { get; init; }
    [JsonPropertyName("server")] public string? Server { get; init; }
    /// <summary><c>present</c>, <c>absent</c>, <c>ambiguous</c> or <c>unresolved</c> (not observed).</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("resource")] public string? Resource { get; init; }
}

public sealed record StatusUntagged
{
    [JsonPropertyName("subscription")] public string? Subscription { get; init; }
    [JsonPropertyName("resource_group")] public string? ResourceGroup { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
}

// ── cost.json (schemaVersion 2) ──────────────────────────────────────────────────────

public sealed record CostDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("period")] public string Period { get; init; } = "";
    [JsonPropertyName("complete")] public bool Complete { get; init; }
    [JsonPropertyName("generated_at")] public DateTimeOffset? GeneratedAt { get; init; }
    [JsonPropertyName("catalog_version")] public int? CatalogVersion { get; init; }
    [JsonPropertyName("sources")] public List<CostSource> Sources { get; init; } = [];
    [JsonPropertyName("totals")] public CostTotals Totals { get; init; } = new();
    [JsonPropertyName("services")] public List<CostService> Services { get; init; } = [];
    [JsonPropertyName("shared")] public List<CostShared> Shared { get; init; } = [];
    [JsonPropertyName("unallocated")] public CostUnallocated Unallocated { get; init; } = new();
}

public sealed record CostSource
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("rows")] public int Rows { get; init; }
    [JsonPropertyName("scopes")] public int Scopes { get; init; }
    [JsonPropertyName("totals")] public CostTotals Totals { get; init; } = new();
}

/// <summary><c>total = direct + shared + shared_unsplit + unallocated</c>, per currency.</summary>
public sealed record CostTotals
{
    [JsonPropertyName("total")] public Money Total { get; init; } = new();
    [JsonPropertyName("direct")] public Money Direct { get; init; } = new();
    [JsonPropertyName("shared")] public Money Shared { get; init; } = new();
    [JsonPropertyName("shared_unsplit")] public Money SharedUnsplit { get; init; } = new();
    [JsonPropertyName("unallocated")] public Money Unallocated { get; init; } = new();
}

public sealed record CostService
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("product")] public string? Product { get; init; }
    [JsonPropertyName("team")] public string? Team { get; init; }
    [JsonPropertyName("direct")] public Money Direct { get; init; } = new();
    /// <summary>The service's part of shared resources. Reported separately from direct.</summary>
    [JsonPropertyName("shared")] public Money Shared { get; init; } = new();
    [JsonPropertyName("total")] public Money Total { get; init; } = new();
    [JsonPropertyName("resources")] public List<CostLine> Resources { get; init; } = [];
    [JsonPropertyName("shared_from")] public List<CostSharedFrom> SharedFrom { get; init; } = [];
}

public sealed record CostLine
{
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("cost")] public Money Cost { get; init; } = new();
    /// <summary><c>tag</c> or <c>override</c>.</summary>
    [JsonPropertyName("via")] public string? Via { get; init; }
}

public sealed record CostSharedFrom
{
    [JsonPropertyName("shared")] public string Shared { get; init; } = "";
    [JsonPropertyName("cost")] public Money Cost { get; init; } = new();
}

public sealed record CostShared
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("cost")] public Money Cost { get; init; } = new();
    [JsonPropertyName("split")] public string? Split { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("services")] public List<string> Services { get; init; } = [];
    /// <summary>Present when no service is bound: the cost stays here.</summary>
    [JsonPropertyName("unsplit")] public Money? Unsplit { get; init; }
    [JsonPropertyName("resources")] public List<CostLine> Resources { get; init; } = [];
}

public sealed record CostUnallocated
{
    [JsonPropertyName("untagged")] public Money Untagged { get; init; } = new();
    [JsonPropertyName("unknown_tag")] public Money UnknownTag { get; init; } = new();
    [JsonPropertyName("by_scope")] public List<CostScope> ByScope { get; init; } = [];
}

public sealed record CostScope
{
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("group")] public string? Group { get; init; }
    [JsonPropertyName("cost")] public Money Cost { get; init; } = new();
}
