using System.Text.Json;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Generates deterministic demo deployment data: 40 services spread across
/// 4 products, each with 30 deployment events stretched over the last 90 days.
/// Regenerating against a clean database always produces the same output
/// (seeded Random), so dev screenshots stay consistent.
/// </summary>
public static class DeploymentSeedData
{
    private const int EventsPerService = 30;
    private const int HistoryDays = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // 40 services total, grouped by product.
    private static readonly ProductCatalog[] Catalog =
    [
        new("ticketing-platform", "https://dev.azure.com/acme/MPT", SourceStyle.AzureDevOps,
            [
                "orders", "schedule", "billing", "checkout", "inventory",
                "pricing", "promotions", "reviews", "notifications", "search",
            ]),
        new("marketplace", "https://github.com/acme", SourceStyle.GitHub,
            [
                "marketplace-api", "marketplace-ui", "vendor-portal", "catalog-sync", "payment-gateway",
                "shipping", "tax-engine", "reports", "admin-console", "mobile-app",
            ]),
        new("identity-platform", "https://dev.azure.com/acme/IDP", SourceStyle.AzureDevOps,
            [
                "auth-api", "sso-bridge", "user-service", "token-issuer", "session-manager",
                "mfa-service", "audit-log", "policy-engine", "role-admin", "profile-api",
            ]),
        new("observability", "https://github.com/acme", SourceStyle.GitHub,
            [
                "metrics-collector", "log-aggregator", "trace-pipeline", "dashboard-api", "alert-engine",
                "anomaly-detector", "synthetic-probe", "uptime-checker", "capacity-planner", "cost-tracker",
            ]),
    ];

    private static readonly string[] Environments = ["development", "staging", "production"];

    // Weighted environment pick — dev deploys happen most often, prod least.
    private static readonly (string env, int weight)[] EnvWeights =
    [
        ("development", 5),
        ("staging", 3),
        ("production", 2),
    ];

    private static readonly Person[] People =
    [
        new("Jan Kowalski", "jan.kowalski@acme.com"),
        new("Anna Kowalska", "anna.kowalska@acme.com"),
        new("Piotr Nowak", "piotr.nowak@acme.com"),
        new("Marta Wiśniewska", "marta.wisniewska@acme.com"),
        new("Sylwester Grabowski", "sylwester.grabowski@acme.com"),
        new("Tomasz Wójcik", "tomasz.wojcik@acme.com"),
        new("Katarzyna Lewandowska", "katarzyna.lewandowska@acme.com"),
        new("Michał Zieliński", "michal.zielinski@acme.com"),
        new("Agnieszka Kamińska", "agnieszka.kaminska@acme.com"),
        new("Paweł Szymański", "pawel.szymanski@acme.com"),
    ];

    private static readonly string[] WorkItemTitles =
    [
        "Fix flaky integration test for checkout", "Add pagination to history endpoint",
        "Implement rate-limit headers on public API", "Migrate cron jobs off legacy scheduler",
        "Switch to System.Text.Json for performance", "Reduce memory footprint on long polling worker",
        "Patch CVE-2026-0432 in upstream dependency", "Harden CSP on public dashboard",
        "Add webhook retry with exponential backoff", "Fix timezone handling in recurring schedules",
        "Wire OpenTelemetry through background jobs", "Rebuild search index pipeline for incremental updates",
        "Add dark mode support", "Introduce feature flag for new onboarding flow",
        "Fix N+1 query on invoice list page", "Ingest vendor catalog in chunks to avoid OOM",
        "Enable HTTP/2 on edge proxy", "Audit data retention policy for analytics exports",
        "Add soft-delete to customer records", "Introduce per-tenant quotas for API usage",
    ];

    private static readonly string[] PrTitles =
    [
        "fix: correct currency rounding for EUR/PLN pair",
        "feat: bulk import endpoint for catalog items",
        "chore: bump dotnet sdk to 10.0.3",
        "perf: stream large result sets instead of buffering",
        "refactor: split auth middleware into request + policy",
        "fix: swallow cancellation exceptions in worker",
        "feat: add structured logging with Serilog",
        "fix: race condition on concurrent webhook delivery",
        "feat: expose health endpoint for ready/live probes",
        "docs: README section on local container run",
        "test: integration tests for rollback path",
        "ci: publish OCI image on main branch",
        "fix: handle null vendor in report generator",
        "feat: rolling deployment window configuration",
        "refactor: pull DTO mapping into extension methods",
    ];

    public static async Task Seed(PlatformDbContext db)
    {
        if (db.DeployEvents.Any()) return;

        var rand = new Random(20260415); // deterministic
        var now = DateTimeOffset.UtcNow;
        var events = new List<DeployEvent>(capacity: 40 * EventsPerService);

        foreach (var product in Catalog)
        {
            foreach (var service in product.Services)
            {
                events.AddRange(GenerateServiceHistory(product, service, now, rand));
            }
        }

        db.DeployEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static IEnumerable<DeployEvent> GenerateServiceHistory(
        ProductCatalog product, string service, DateTimeOffset now, Random rand)
    {
        // 30 evenly-but-jittered timestamps across the last HistoryDays.
        var totalHours = HistoryDays * 24;
        var slot = totalHours / (double)EventsPerService;
        var timestamps = new DateTimeOffset[EventsPerService];
        for (var i = 0; i < EventsPerService; i++)
        {
            // Place each event inside its slot with a little noise so it looks organic.
            var baseOffset = i * slot;
            var jitter = (rand.NextDouble() - 0.5) * slot * 0.6;
            timestamps[i] = now.AddHours(-(totalHours - (baseOffset + jitter)));
        }

        // Start each service at a different semver base so they don't all look identical.
        var major = rand.Next(1, 5);
        var minor = rand.Next(0, 6);
        var patch = rand.Next(0, 9);

        // Track last version per environment for realistic previousVersion + rollback.
        var lastPerEnv = new Dictionary<string, string>();

        for (var i = 0; i < EventsPerService; i++)
        {
            var environment = PickEnvironment(rand);

            // Version progression: 60% patch bump, 30% minor bump, 10% major bump.
            var bump = rand.NextDouble();
            if (bump < 0.1) { major++; minor = 0; patch = 0; }
            else if (bump < 0.4) { minor++; patch = 0; }
            else { patch++; }

            var version = $"{major}.{minor}.{patch}";
            lastPerEnv.TryGetValue(environment, out var previousVersion);

            // 5% rollbacks — re-deploy the previous version and flag it.
            var isRollback = previousVersion is not null && rand.NextDouble() < 0.05;
            if (isRollback) version = previousVersion!;

            // 5% failed, 3% in_progress, rest succeeded.
            var statusRoll = rand.NextDouble();
            var status = statusRoll < 0.05 ? "failed" : statusRoll < 0.08 ? "in_progress" : "succeeded";

            var references = BuildReferences(product, service, rand);
            var participants = BuildParticipants(rand);
            var enrichment = BuildEnrichment(rand);

            yield return MakeEvent(
                product.Name, service, environment, version, previousVersion,
                SourceLabel(product.SourceStyle), timestamps[i],
                isRollback, status,
                references, participants, enrichment);

            // Only update the env tracker for successful / in-progress deploys
            // so rollbacks don't poison the "last known good" pointer.
            if (status != "failed") lastPerEnv[environment] = version;
        }
    }

    private static string PickEnvironment(Random rand)
    {
        var total = 0;
        foreach (var (_, w) in EnvWeights) total += w;
        var roll = rand.Next(total);
        foreach (var (env, w) in EnvWeights)
        {
            if (roll < w) return env;
            roll -= w;
        }
        return Environments[0];
    }

    private static List<ReferenceDto> BuildReferences(ProductCatalog product, string service, Random rand)
    {
        var refs = new List<ReferenceDto>();

        // Pipeline + PR are always present; work-item ~80%; repository ~40%.
        var buildId = rand.Next(10000, 99999);
        var prNum = rand.Next(50, 900);
        var wiKey = $"{ProductPrefix(product.Name)}-{rand.Next(100, 9999)}";

        if (product.SourceStyle == SourceStyle.AzureDevOps)
        {
            refs.Add(new ReferenceDto("pipeline", $"{product.BaseUrl}/_build/results?buildId={buildId}", "azure-devops", buildId.ToString()));
            refs.Add(new ReferenceDto("pull-request", $"{product.BaseUrl}/_git/{service}/pullrequest/{prNum}", "azure-devops", prNum.ToString()));
        }
        else
        {
            refs.Add(new ReferenceDto("pipeline", $"{product.BaseUrl}/{service}/actions/runs/{buildId}", "github", buildId.ToString()));
            refs.Add(new ReferenceDto("pull-request", $"{product.BaseUrl}/{service}/pull/{prNum}", "github", prNum.ToString()));
        }

        if (rand.NextDouble() < 0.8)
        {
            refs.Add(new ReferenceDto("work-item",
                $"https://acme.atlassian.net/browse/{wiKey}", "jira", wiKey));
        }

        if (rand.NextDouble() < 0.4)
        {
            var revision = Guid.NewGuid().ToString("N")[..12];
            if (product.SourceStyle == SourceStyle.AzureDevOps)
            {
                refs.Add(new ReferenceDto("repository",
                    $"{product.BaseUrl}/_git/{service}", "azure-devops",
                    $"{product.Name}/{service}", revision));
            }
            else
            {
                refs.Add(new ReferenceDto("repository",
                    $"{product.BaseUrl}/{service}", "github",
                    $"acme/{service}", revision));
            }
        }

        return refs;
    }

    private static List<ParticipantDto> BuildParticipants(Random rand)
    {
        // Pick 2-3 distinct people; always include PR Author.
        var shuffled = People.OrderBy(_ => rand.Next()).ToArray();
        var participants = new List<ParticipantDto>
        {
            new("PR Author", shuffled[0].Name, shuffled[0].Email),
            new("PR Reviewer", shuffled[1].Name, shuffled[1].Email),
        };
        if (rand.NextDouble() < 0.6)
        {
            participants.Add(new ParticipantDto("QA", shuffled[2].Name, shuffled[2].Email));
        }
        return participants;
    }

    private static EnrichmentData BuildEnrichment(Random rand)
    {
        var wi = WorkItemTitles[rand.Next(WorkItemTitles.Length)];
        var pr = PrTitles[rand.Next(PrTitles.Length)];
        var status = rand.NextDouble() switch
        {
            < 0.4 => "Done",
            < 0.75 => "In Review",
            _ => "In Progress",
        };
        return new EnrichmentData(new Dictionary<string, string>
        {
            ["workItemTitle"] = wi,
            ["workItemStatus"] = status,
            ["prTitle"] = pr,
        }, []);
    }

    private static string SourceLabel(SourceStyle style) =>
        style == SourceStyle.GitHub ? "github-actions" : "azure-devops";

    private static string ProductPrefix(string product) => product switch
    {
        "ticketing-platform" => "PLAT",
        "marketplace" => "MKT",
        "identity-platform" => "IDP",
        "observability" => "OBS",
        _ => "SVC",
    };

    private record ProductCatalog(string Name, string BaseUrl, SourceStyle SourceStyle, string[] Services);

    private record Person(string Name, string Email);

    private enum SourceStyle { AzureDevOps, GitHub }

    private record EnrichmentData(Dictionary<string, string> Labels, List<ParticipantDto> Participants);

    private static DeployEvent MakeEvent(
        string product, string service, string environment, string version, string? previousVersion,
        string source, DateTimeOffset deployedAt,
        bool isRollback, string status,
        List<ReferenceDto> references, List<ParticipantDto> participants,
        EnrichmentData? enrichment = null)
    {
        string? enrichmentJson = null;
        if (enrichment is not null)
        {
            enrichmentJson = JsonSerializer.Serialize(new
            {
                labels = enrichment.Labels,
                participants = enrichment.Participants,
                enrichedAt = deployedAt,
            }, JsonOptions);
        }

        return new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = environment,
            Version = version,
            PreviousVersion = previousVersion,
            IsRollback = isRollback,
            Status = status,
            Source = source,
            DeployedAt = deployedAt,
            ReferencesJson = JsonSerializer.Serialize(references, JsonOptions),
            ParticipantsJson = JsonSerializer.Serialize(participants, JsonOptions),
            EnrichmentJson = enrichmentJson,
            MetadataJson = "{}",
            CreatedAt = deployedAt,
        };
    }
}
