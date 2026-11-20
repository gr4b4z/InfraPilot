using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Features.ServiceInventory.Documents;
using Platform.Api.Features.ServiceInventory.Sources;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ServiceInventory;

/// <summary>
/// Read-only API of the service inventory, under <c>/api/service-inventory</c>. A service is found
/// by its portal name: the catalog <c>id</c> is the service name. Answers 404 while the feature
/// flag is off. Nothing here returns a credential or a query string.
/// </summary>
public static class ServiceInventoryEndpoints
{
    public static RouteGroupBuilder MapServiceInventoryEndpoints(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var flags = context.HttpContext.RequestServices.GetRequiredService<IFeatureFlags>();
            if (!await flags.IsEnabled(FeatureFlagKeys.ServiceInventory, context.HttpContext.RequestAborted))
                return Results.NotFound(new { error = "The service inventory is not enabled on this platform" });
            return await next(context);
        });

        group.MapGet("/services/{serviceName}", GetServiceAsync);
        group.MapGet("/services/{serviceName}/observed", GetObservedAsync);
        group.MapGet("/services/{serviceName}/cost", GetCostAsync);
        group.MapInventoryCostEndpoints();
        group.MapGet("/feeds", GetFeedsAsync).RequireAuthorization(AuthorizationPolicies.CatalogAdmin);
        group.MapPost("/rebuild", async (InventoryRefresher refresher, CancellationToken ct) =>
            Results.Ok(new { rebuilt = await refresher.RebuildAsync(force: true, ct) }))
            .RequireAuthorization(AuthorizationPolicies.CatalogAdmin);
        return group;
    }

    private static string Key(string serviceName) => serviceName.Trim().ToLowerInvariant();

    private static SortedDictionary<string, decimal> Money(IEnumerable<(string Currency, decimal Amount)> amounts) =>
        new(amounts.ToDictionary(a => a.Currency, a => a.Amount), StringComparer.Ordinal);

    private static async Task<IResult> GetServiceAsync(
        string serviceName, string? product, InventoryReadService read, ICurrentUser user, CancellationToken ct)
    {
        var feed = await read.FeedAsync(InventoryFeedTypes.Catalog, ct: ct);
        var current = await read.DocumentAsync<CatalogDocument>(feed, ct);
        var freshness = InventoryReadService.Freshness(feed, current?.Snapshot, InventoryFeedTypes.Catalog, user.IsAdmin);
        var id = Key(serviceName);
        var entry = current?.Document.Services.FirstOrDefault(s => s.Id.Trim().ToLowerInvariant() == id);
        if (current is null || entry is null)
            return Results.NotFound(new { error = $"No service inventory entry has the id '{id}'.", freshness });

        var catalog = current.Value.Document;
        var team = catalog.Teams.FirstOrDefault(t => t.Id == entry.Team);
        var catalogProduct = catalog.Products.FirstOrDefault(p => p.Id == entry.Product);

        return Results.Ok(new
        {
            service = new
            {
                id = entry.Id,
                entry.Description,
                entry.Product,
                entry.Team,
                entry.Lifecycle,
                entry.Type,
                entry.Repo,
                entry.Tier,
                entry.Exposure,
                entry.CustomerFacing,
                entry.ReplacedBy,
                entry.RetiredAt,
                environments = entry.Environments
                    .Select(e => new { environment = e.Key, cluster = e.Value.Aks?.Cluster, @namespace = e.Value.Aks?.Namespace }),
                workloads = entry.Workloads?.Select(w => new { name = w.Key, kind = w.Value.Kind }),
                databases = entry.DatabaseBindings()
                    .Select(b => new { b.Kind, b.Database, b.Server, b.Owner, b.Workloads }),
                bindings = entry.Bindings,
            },
            team = team is null ? null : new { team.Id, team.Lead, team.Deputy, team.GithubTeam, team.EntraGroup, team.Lifecycle },
            product = catalogProduct is null ? null : new { catalogProduct.Id, catalogProduct.Name, catalogProduct.BusinessOwner, catalogProduct.TechnicalOwner, catalogProduct.Lifecycle },
            // The caller says which portal product it is looking at (?product=, the service page's
            // own); whether it differs from the catalog's is then a comparison, not a search through
            // every deploy event for the name. Deploys may differ while they are brought in line.
            portal = string.IsNullOrWhiteSpace(product) ? null : new
            {
                product = product.Trim(),
                productDiffers = !string.Equals(product.Trim(), entry.Product, StringComparison.OrdinalIgnoreCase),
            },
            catalogVersion = catalog.CatalogVersion,
            freshness,
        });
    }

    private static async Task<IResult> GetObservedAsync(
        string serviceName, InventoryReadService read, ICurrentUser user, CancellationToken ct)
    {
        var id = Key(serviceName);
        var statusFeed = await read.FeedAsync(InventoryFeedTypes.Status, ct: ct);
        var status = await read.DocumentAsync<StatusDocument>(statusFeed, ct);
        var catalog = await read.DocumentAsync<CatalogDocument>(await read.FeedAsync(InventoryFeedTypes.Catalog, ct: ct), ct);
        var freshness = InventoryReadService.Freshness(statusFeed, status?.Snapshot, InventoryFeedTypes.Status, user.IsAdmin);

        var entry = status?.Document.Services.FirstOrDefault(s => s.Id.Trim().ToLowerInvariant() == id);
        if (status is null || entry is null)
            return Results.NotFound(new { error = $"The observed state has no service '{id}'.", freshness });

        var doc = status.Value.Document;
        var azure = doc.Azure;
        var catalogEntry = catalog?.Document.Services.FirstOrDefault(s => s.Id.Trim().ToLowerInvariant() == id);
        var boundServers = catalogEntry?.DatabaseBindings().Select(b => b.Server).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        bool Mentions(StatusDatabase d) =>
            string.Equals(d.Owner, id, StringComparison.OrdinalIgnoreCase)
            || d.Owners?.Any(o => string.Equals(o, id, StringComparison.OrdinalIgnoreCase)) == true
            || d.UsedBy?.Any(u => string.Equals(u, id, StringComparison.OrdinalIgnoreCase)) == true;

        return Results.Ok(new
        {
            serviceId = entry.Id,
            entry.Stale,
            entry.Unverified,
            entry.Warnings,
            entry.Onboarding,
            // When each source was last read for this service, as the status file says — not to be
            // confused with `freshness` below, the age of the portal's copy.
            sourceFreshness = entry.Freshness,
            generatedAt = doc.GeneratedAt,
            catalogVersion = doc.CatalogVersion,
            azure = azure is null ? null : new
            {
                observedAt = azure.ObservedAt,
                bindings = azure.Bindings.Where(b => string.Equals(b.Service, id, StringComparison.OrdinalIgnoreCase)),
                shared = azure.Shared.Where(s => boundServers.Contains(s.Id)
                    || s.Resources.Any(r => r.Databases.Any(Mentions))),
                // Entries carrying this service's tag, as published.
                taggedResources = azure.Services.Where(e =>
                    e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String
                    && string.Equals(v.GetString(), id, StringComparison.OrdinalIgnoreCase)),
            },
            freshness,
        });
    }

    private static async Task<IResult> GetCostAsync(
        string serviceName, string? period, InventoryReadService read, PlatformDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var id = Key(serviceName);
        if (period is not null && !ServiceInventoryOptionsValidator.PeriodPattern().IsMatch(period))
            return Results.BadRequest(new { error = "period must be YYYY-MM" });

        var requested = period is not null;
        var feedRow = await read.FeedAsync(InventoryFeedTypes.Cost, ct: ct);
        if (period is null && feedRow?.CurrentSnapshotId is { } latest)
            period = await db.InventorySnapshots.AsNoTracking().Where(s => s.Id == latest).Select(s => s.Period).FirstOrDefaultAsync(ct);

        var monthRow = period is null ? null : await read.FeedAsync(InventoryFeedTypes.Cost, period, ct);
        var month = await read.DocumentAsync<CostDocument>(monthRow, ct);
        // The month the latest file is for is refreshed through the feed itself, so its state is the
        // feed's, however it was asked for — unless only the month's own row recorded a failure. Any
        // other month asked for by name reports its own state (a closed month that failed says so).
        var isLatestMonth = monthRow is not null && monthRow.CurrentSnapshotId == feedRow?.CurrentSnapshotId;
        var stateRow = requested && !isLatestMonth
            ? monthRow
            : feedRow?.LastError is null && monthRow?.LastError is not null ? monthRow : feedRow;
        var freshness = InventoryReadService.Freshness(stateRow, month?.Snapshot, InventoryFeedTypes.Cost, user.IsAdmin);
        if (month is null)
            return Results.NotFound(new { error = period is null ? "No cost has been read yet." : $"No cost has been read for {period}.", freshness });

        var snapshotId = month.Value.Snapshot.Id;
        var amounts = await db.InventoryCostServices.AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && r.ServiceId == id)
            .Select(r => new { r.Currency, r.Direct, r.Shared, r.Total })
            .ToListAsync(ct);
        if (amounts.Count == 0)
        {
            // A refresh can replace this month's snapshot, and retention remove the old one's rows,
            // between reading the pointer and reading the rows. The rows are a pure projection of the
            // stored file the document came from, so project them from it instead of answering empty.
            amounts = InventoryProjector.Project(snapshotId, InventoryFeedTypes.Cost, month.Value.Document).CostServices
                .Where(r => r.ServiceId == id)
                .Select(r => new { r.Currency, r.Direct, r.Shared, r.Total })
                .ToList();
        }
        var line = month.Value.Document.Services.FirstOrDefault(s => s.Id.Trim().ToLowerInvariant() == id);
        if (line is null && amounts.Count == 0)
            return Results.NotFound(new { error = $"The cost of {period} has no service '{id}'.", freshness });

        var doc = month.Value.Document;
        return Results.Ok(new
        {
            serviceId = id,
            period = doc.Period,
            complete = doc.Complete,
            generatedAt = doc.GeneratedAt,
            catalogVersion = doc.CatalogVersion,
            // Amounts per currency, in the shape the published file uses everywhere ({"USD": 1.5}).
            // Direct and shared stay separate; currencies are never added together.
            direct = Money(amounts.Select(a => (a.Currency, a.Direct))),
            shared = Money(amounts.Select(a => (a.Currency, a.Shared))),
            total = Money(amounts.Select(a => (a.Currency, a.Total))),
            resources = line?.Resources ?? [],
            sharedFrom = line?.SharedFrom ?? [],
            freshness,
        });
    }

    private static async Task<IResult> GetFeedsAsync(
        PlatformDbContext db, IOptionsMonitor<ServiceInventoryOptions> options, IArtifactSourceFactory sources, CancellationToken ct)
    {
        var configuration = options.CurrentValue;
        var errors = ServiceInventoryOptionsValidator.Validate(configuration);
        var rows = await db.InventoryFeeds.AsNoTracking().OrderBy(f => f.FeedKey).ToListAsync(ct);
        var snapshotIds = rows.Where(r => r.CurrentSnapshotId != null).Select(r => r.CurrentSnapshotId!.Value).ToList();
        var snapshots = await db.InventorySnapshots.AsNoTracking()
            .Where(s => snapshotIds.Contains(s.Id))
            .Select(s => new { s.Id, s.FeedType, s.Period, s.DocumentVersion, s.GeneratedAt, s.Complete, s.FetchedAt, s.SourceLocation })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            configurationErrors = errors,
            feeds = configuration.Feeds.Select(f => new
            {
                name = f.Name,
                type = f.Type,
                sourceKind = f.Source.Kind,
                // Location from the source itself: host and path, never a query string.
                location = errors.Count == 0 ? sources.Create(f).Location : null,
                pollMinutes = f.PollMinutes,
                closedMonthsFrom = f.Source.ClosedMonths?.From,
            }),
            state = rows.Select(r =>
            {
                var s = snapshots.FirstOrDefault(x => x.Id == r.CurrentSnapshotId);
                return new
                {
                    feed = r.FeedName,
                    // False for rows a renamed or removed feed left behind; nothing reads them.
                    configured = configuration.Feeds.Any(f => string.Equals(f.Name, r.FeedName, StringComparison.OrdinalIgnoreCase)),
                    period = r.Period == "" ? null : r.Period,
                    type = r.FeedType,
                    stored = s is null ? null : new
                    {
                        s.SourceLocation,
                        version = InventoryReadService.VersionLabel(new Models.InventorySnapshot
                        {
                            FeedType = s.FeedType, Period = s.Period, DocumentVersion = s.DocumentVersion,
                            GeneratedAt = s.GeneratedAt, Complete = s.Complete,
                        }),
                        s.FetchedAt,
                    },
                    r.LastSuccessAt,
                    r.LastAttemptAt,
                    r.LastChangedAt,
                    r.LastError,
                    r.ConsecutiveFailures,
                };
            }),
        });
    }
}
