using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Builds.Models;
using Platform.Api.Features.Deployments;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Builds;

public class BuildService
{
    private readonly PlatformDbContext _db;
    private readonly IBuildIngestHook _ingestHook;
    private readonly ServiceProductOverrideService _productOverrides;
    private readonly ILogger<BuildService> _logger;

    public BuildService(
        PlatformDbContext db,
        IBuildIngestHook ingestHook,
        ServiceProductOverrideService productOverrides,
        ILogger<BuildService> logger)
    {
        _db = db;
        _ingestHook = ingestHook;
        _productOverrides = productOverrides;
        _logger = logger;
    }

    /// <summary>The registry's unique triple — the key a build and its deploy events share.</summary>
    private record struct BuildKey(string Product, string Service, string Version);

    /// <summary>Outcome of a registration: the stored build plus whether it replaced an existing row.</summary>
    public record RegisterResult(Build Build, bool Replayed);

    /// <summary>
    /// Registers a build idempotently on the natural key <c>(Product, Service, Version)</c>. A
    /// re-POST for an existing key updates the row in place (<c>Replayed=true</c>) rather than
    /// failing or duplicating — the publish stage is fail-loud (D11), so a pipeline retry after a
    /// partial failure must be able to repeat the whole stage safely. Unlike deploy-event ingest,
    /// the natural key IS backed by a unique index, so the check-then-insert race resolves by
    /// catching the constraint violation and updating the winning row.
    /// </summary>
    public async Task<RegisterResult> RegisterAsync(RegisterBuildDto dto, CancellationToken ct = default)
    {
        var manifestJson = ExtractManifestJson(dto.Manifest);

        // An admin override for the service overrules the product the publish pipeline sent — the same
        // resolution deploy ingest performs, so a build and the deploy event for the same version cannot
        // end up filed under different products. Resolved before the natural-key probe: it is part of
        // that key, and a replay must find the row the first attempt wrote.
        var product = await _productOverrides.ResolveProductAsync(dto.Product, dto.Service, ct);

        var existing = await FindByNaturalKey(product, dto, ct);
        if (existing is not null)
        {
            Apply(existing, dto, manifestJson);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Replayed build registration {Id}: {Product}/{Service} v{Version} already registered; updated in place",
                existing.Id, LogSanitizer.Clean(existing.Product), LogSanitizer.Clean(existing.Service),
                LogSanitizer.Clean(existing.Version));

            // A replay still runs the promotion hook — same rationale as deploy-ingest replays: the
            // first POST could only match policies that existed then, and one stranded by a hook
            // failure is repaired by the retry. Candidate creation is idempotent (natural-key reuse).
            await _ingestHook.OnRegisteredAsync(existing, ct);

            return new RegisterResult(existing, true);
        }

        var build = new Build
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = dto.Service,
            Version = dto.Version,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Apply(build, dto, manifestJson);
        build.UpdatedAt = null; // Apply stamps it; a first registration isn't an update.
        _db.Builds.Add(build);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent duplicate lost the insert race — the unique index guarantees exactly one
            // row exists now. Treat as a replay of that row.
            _db.Entry(build).State = EntityState.Detached;
            var winner = await FindByNaturalKey(product, dto, ct)
                ?? throw new InvalidOperationException(
                    $"Insert of build {product}/{dto.Service} v{dto.Version} failed but no existing row was found.");
            Apply(winner, dto, manifestJson);
            await _db.SaveChangesAsync(ct);
            await _ingestHook.OnRegisteredAsync(winner, ct);
            return new RegisterResult(winner, true);
        }

        _logger.LogInformation(
            "Registered build {Id}: {Product}/{Service} v{Version} from {Branch}",
            build.Id, LogSanitizer.Clean(build.Product), LogSanitizer.Clean(build.Service),
            LogSanitizer.Clean(build.Version), LogSanitizer.Clean(build.Branch));

        // After the save, so the build row exists whatever the promotion machinery does with it.
        await _ingestHook.OnRegisteredAsync(build, ct);

        return new RegisterResult(build, false);
    }

    private Task<Build?> FindByNaturalKey(string product, RegisterBuildDto dto, CancellationToken ct) =>
        _db.Builds.FirstOrDefaultAsync(
            b => b.Product == product && b.Service == dto.Service && b.Version == dto.Version, ct);

    // A replay overwrites provenance with the caller's latest picture. The retry is the fuller
    // report (e.g. the first attempt died before the ORAS push resolved a digest) — except the
    // manifest, which is only replaced when the caller actually sent one.
    private static void Apply(Build build, RegisterBuildDto dto, string? manifestJson)
    {
        build.Branch = dto.Branch;
        build.CommitSha = dto.CommitSha;
        build.BuildId = dto.BuildId;
        build.BuildUrl = dto.BuildUrl;
        build.ArtifactRef = dto.ArtifactRef;
        build.ArtifactDigest = dto.ArtifactDigest;
        if (manifestJson is not null) build.ManifestJson = manifestJson;
        build.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? ExtractManifestJson(JsonElement? manifest) =>
        manifest is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } m
            ? m.GetRawText()
            : null;

    /// <summary>
    /// Newest-first list for the UI picker and the read API. Every narrowing lives in
    /// <see cref="BuildQuery"/>, which documents why each field matches the way it does.
    ///
    /// Each row carries where it was deployed (see <see cref="DeploymentsForAsync"/>) — the one
    /// question a registry row cannot answer about itself, and the reason the list can link a build
    /// to the deploy events that shipped it.
    /// </summary>
    public async Task<List<BuildSummaryDto>> ListAsync(
        BuildQuery query, int limit, CancellationToken ct = default)
    {
        // Projected rather than materialised as entities: the manifest is large and no caller of the
        // list wants it.
        var rows = await Filter(_db.Builds.AsNoTracking(), query)
            .OrderByDescending(b => b.CreatedAt)
            .Take(limit)
            .Select(b => new
            {
                b.Id, b.Product, b.Service, b.Version, b.Branch, b.CommitSha, b.BuildId, b.BuildUrl,
                b.ArtifactRef, b.ArtifactDigest, b.CreatedAt, b.UpdatedAt,
            })
            .ToListAsync(ct);

        var deployments = await DeploymentsForAsync(
            rows.Select(r => new BuildKey(r.Product, r.Service, r.Version)).ToList(), ct);

        return rows
            .Select(r => new BuildSummaryDto(
                r.Id, r.Product, r.Service, r.Version, r.Branch, r.CommitSha, r.BuildId, r.BuildUrl,
                r.ArtifactRef, r.ArtifactDigest, r.CreatedAt, r.UpdatedAt,
                deployments.TryGetValue(new BuildKey(r.Product, r.Service, r.Version), out var d) ? d : []))
            .ToList();
    }

    /// <summary>
    /// The deploy events that shipped each of the given builds, keyed by the registry's natural
    /// triple and reduced to the newest deploy per environment. Each entry says whether it is still
    /// that environment's current deploy for the service, or history a newer version has replaced.
    ///
    /// One query for the whole page, not one per row: the three value sets are sent as separate IN
    /// lists (so the database can use the (Product, Service, …) index) and the exact triples are
    /// re-checked here, because the cross product of the three lists is wider than the set of builds
    /// actually asked about.
    ///
    /// Matching is exact on all three fields, deliberately: a build and the deploy event for the same
    /// version resolve their product through the same <see cref="ServiceProductOverrideService"/>, so
    /// a case-insensitive match would buy nothing and cost the index.
    /// </summary>
    private async Task<Dictionary<BuildKey, IReadOnlyList<BuildDeploymentDto>>> DeploymentsForAsync(
        IReadOnlyList<BuildKey> builds, CancellationToken ct)
    {
        if (builds.Count == 0) return [];

        var products = builds.Select(b => b.Product).Distinct().ToList();
        var services = builds.Select(b => b.Service).Distinct().ToList();
        var versions = builds.Select(b => b.Version).Distinct().ToList();
        var wanted = builds.ToHashSet();

        var events = await _db.DeployEvents.AsNoTracking()
            .Where(d => products.Contains(d.Product)
                && services.Contains(d.Service)
                && versions.Contains(d.Version))
            .Select(d => new
            {
                d.Id, d.Product, d.Service, d.Version, d.Environment, d.Status, d.IsRollback, d.DeployedAt,
            })
            .ToListAsync(ct);

        // The newest event per (product, service, environment) across ALL versions — the same
        // "current" the state matrix (GetState) shows. An entry above whose event isn't in this set
        // is history: the environment has since moved on to some other version. Not filtered by
        // version, deliberately — the version that superseded a listed build is usually one the
        // page isn't showing.
        var currentEventIds = (await _db.DeployEvents.AsNoTracking()
                .Where(d => products.Contains(d.Product) && services.Contains(d.Service))
                .GroupBy(d => new { d.Product, d.Service, d.Environment })
                .Select(g => g.OrderByDescending(e => e.DeployedAt).Select(e => e.Id).First())
                .ToListAsync(ct))
            .ToHashSet();

        return events
            .Where(e => wanted.Contains(new BuildKey(e.Product, e.Service, e.Version)))
            .GroupBy(e => new BuildKey(e.Product, e.Service, e.Version))
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<BuildDeploymentDto> (g) => g
                    // One entry per environment — a version redeployed to staging five times is still
                    // "in staging", and the newest of those is the one worth linking to.
                    .GroupBy(e => e.Environment)
                    .Select(byEnv => byEnv.OrderByDescending(e => e.DeployedAt).First())
                    .OrderByDescending(e => e.DeployedAt)
                    .Select(e => new BuildDeploymentDto(
                        e.Id, e.Environment, e.Status, e.IsRollback, e.DeployedAt,
                        IsCurrent: currentEventIds.Contains(e.Id)))
                    .ToList());
    }

    /// <summary>
    /// The pick lists behind the registry's filter combo boxes: which products, services and
    /// branches actually have builds in the current view, and how many each would show. Counting
    /// server-side (rather than deriving the lists from the returned page) is what makes the
    /// filters honest — the page is capped, so a value whose builds fall past the cap would
    /// otherwise be un-pickable.
    /// </summary>
    public async Task<BuildFacetsDto> FacetsAsync(
        BuildQuery query, int limit, CancellationToken ct = default) =>
        new(
            // Each facet is counted with its OWN field dropped: a combo box has to keep offering
            // the values you could switch to, or picking one product leaves the product list
            // holding that single product and no route back.
            await CountBy(query with { Product = null }, b => b.Product, limit, ct),
            await CountBy(query with { Service = null }, b => b.Service, limit, ct),
            await CountBy(query with { Branch = null }, b => b.Branch, limit, ct));

    private async Task<List<BuildFacetValueDto>> CountBy(
        BuildQuery query, Expression<Func<Build, string>> field, int limit, CancellationToken ct)
    {
        var rows = await Filter(_db.Builds.AsNoTracking(), query)
            .GroupBy(field)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            // Busiest first, ties alphabetical: `limit` has to cut something on a registry with
            // thousands of feature branches, and the value a reader wants is usually the one with
            // the most builds behind it.
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Value)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(r => new BuildFacetValueDto(r.Value, r.Count)).ToList();
    }

    /// <summary>
    /// Applies a <see cref="BuildQuery"/> to a build queryable. Shared by the list and the facet
    /// counts so the counts always describe the list the same filters would return.
    /// </summary>
    private static IQueryable<Build> Filter(IQueryable<Build> builds, BuildQuery query)
    {
        // Identity fields, case-insensitively exact: they are filled from a facet pick or a link,
        // and "the product named X" must not also mean "the product whose name contains X".
        if (!string.IsNullOrWhiteSpace(query.Product))
        {
            var product = query.Product.Trim().ToLower();
            builds = builds.Where(b => b.Product.ToLower() == product);
        }
        if (!string.IsNullOrWhiteSpace(query.Service))
        {
            var service = query.Service.Trim().ToLower();
            builds = builds.Where(b => b.Service.ToLower() == service);
        }
        if (!string.IsNullOrWhiteSpace(query.Branch))
        {
            var branch = query.Branch.Trim().ToLower();
            builds = builds.Where(b => b.Branch.ToLower().Contains(branch));
        }
        // Exact, unlike the branch substring: a version filter exists to identify one build, so
        // "7.0.1" must not drag "7.0.10" along with it.
        if (!string.IsNullOrWhiteSpace(query.Version))
            builds = builds.Where(b => b.Version == query.Version);

        // The free-text box. One needle across every column a person might half-remember, so
        // searching "aws" finds swo-extension-aws whether the word sits in the product, the
        // service or the branch, and a commit sha off a PR finds the build it came from.
        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            var needle = query.Query.Trim().ToLower();
            builds = builds.Where(b =>
                b.Product.ToLower().Contains(needle) ||
                b.Service.ToLower().Contains(needle) ||
                b.Version.ToLower().Contains(needle) ||
                b.Branch.ToLower().Contains(needle) ||
                (b.CommitSha != null && b.CommitSha.ToLower().Contains(needle)) ||
                (b.BuildId != null && b.BuildId.ToLower().Contains(needle)));
        }

        // Registration window: Since inclusive, Until exclusive, so "the 14th" is
        // [14th 00:00, 15th 00:00) and two adjacent days never both claim the same build.
        if (query.Since is { } since) builds = builds.Where(b => b.CreatedAt >= since);
        if (query.Until is { } until) builds = builds.Where(b => b.CreatedAt < until);

        return builds;
    }

    public async Task<BuildDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var build = await _db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (build is null) return null;

        JsonElement? manifest = build.ManifestJson is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(build.ManifestJson);
        return new BuildDetailDto(
            build.Id, build.Product, build.Service, build.Version, build.Branch, build.CommitSha,
            build.BuildId, build.BuildUrl, build.ArtifactRef, build.ArtifactDigest,
            build.CreatedAt, build.UpdatedAt, manifest);
    }
}
