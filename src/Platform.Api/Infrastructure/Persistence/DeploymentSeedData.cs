using System.Text.Json;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Generates deterministic demo deployment data: three products of differing size, whose services
/// each get somewhere between <see cref="MinEventsPerService"/> and <see cref="MaxEventsPerService"/>
/// deployment events stretched over the last 90 days. Regenerating against a clean database always
/// produces the same output (seeded Random), so dev screenshots stay consistent.
/// </summary>
public static class DeploymentSeedData
{
    // Per-service event count is rolled rather than fixed: a uniform 30-per-service made every
    // service look equally busy, which flattens exactly the thing the deployment views are for —
    // spotting the service that ships ten times a week next to the one that shipped twice.
    private const int MinEventsPerService = 8;
    private const int MaxEventsPerService = 42;
    private const int HistoryDays = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Three products of deliberately different size — a big one, a satellite, and a small
    // odd-one-out — so the product filters and the per-product rollups have something to
    // distinguish. Two are Azure DevOps-shaped and one GitHub-shaped, which is what decides how
    // references are built further down.
    private static readonly ProductCatalog[] Catalog =
    [
        new("mpt", "https://dev.azure.com/acmetrix-pc/MPT", SourceStyle.AzureDevOps,
            [
                "orders", "schedule", "billing", "checkout", "inventory",
                "pricing", "notifications", "search",
            ]),
        new("mpt-extentions", "https://dev.azure.com/acmetrix-pc/MPT-EXT", SourceStyle.AzureDevOps,
            [
                "reviews", "loyalty", "gift-cards", "partner-api", "webhooks",
            ]),
        new("extra", "https://github.com/acmetrix", SourceStyle.GitHub,
            [
                "reports", "admin-console", "cost-tracker",
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
        new("Jan Kowalski", "jan.kowalski@acmetrix.com"),
        new("Anna Kowalska", "anna.kowalska@acmetrix.com"),
        new("Piotr Nowak", "piotr.nowak@acmetrix.com"),
        new("Marta Wiśniewska", "marta.wisniewska@acmetrix.com"),
        new("Sylwester Grabowski", "sylwester.grabowski@acmetrix.com"),
        new("Tomasz Wójcik", "tomasz.wojcik@acmetrix.com"),
        new("Katarzyna Lewandowska", "katarzyna.lewandowska@acmetrix.com"),
        new("Michał Zieliński", "michal.zielinski@acmetrix.com"),
        new("Agnieszka Kamińska", "agnieszka.kaminska@acmetrix.com"),
        new("Paweł Szymański", "pawel.szymanski@acmetrix.com"),
    ];

    /// <summary>
    /// The seeded local sign-in accounts (see <see cref="SeedData.SeedLocalUsers"/>). Work items name
    /// one of these as their <c>qa-owner</c> a good part of the time, so signing in locally lands on a
    /// work-items queue with rows in it — with a pool of fictional people only, every seeded item
    /// belongs to somebody who can never log in, and every queue reads empty.
    /// </summary>
    private static readonly Person[] LocalTesters =
    [
        new("Regular User", "user@localhost"),
        new("Admin User", "admin@localhost"),
        new("QA Engineer", "qa@localhost"),
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
        var events = new List<DeployEvent>(
            capacity: Catalog.Sum(p => p.Services.Length) * MaxEventsPerService);

        foreach (var product in Catalog)
        {
            foreach (var service in product.Services)
            {
                events.AddRange(GenerateServiceHistory(product, service, now, rand));
            }
        }

        db.DeployEvents.AddRange(events);

        // Captured pipeline output, so the detail page's log viewer and its error highlighting have
        // something to show locally. Only a slice of events carry it — a real portal has plenty of
        // deployments whose producer never sent logs, and the empty state should be visible too.
        foreach (var ev in events)
        {
            db.DeployEventLogs.AddRange(BuildLogs(ev, rand));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Synthesises the log blocks a Helm-style deploy would have printed. Failed events get the
    /// diagnostics block too, ending on the same message their <c>run.failureReason</c> carries — the
    /// point being that the highlighted error and the log agree, which is what makes the page useful.
    /// </summary>
    private static IEnumerable<DeployEventLog> BuildLogs(DeployEvent ev, Random rand)
    {
        // Every failure gets logs (that's when they matter); successes only sometimes.
        var failed = ev.Status == "failed";
        if (!failed && rand.NextDouble() > 0.6) yield break;

        var releaseOutput =
            $"Release \"{ev.Service}\" has been upgraded. Happy Helming!\n" +
            $"NAME: {ev.Service}\n" +
            $"LAST DEPLOYED: {ev.DeployedAt:ddd MMM d HH:mm:ss yyyy}\n" +
            $"NAMESPACE: {ev.Environment}\n" +
            $"STATUS: {(failed ? "failed" : "deployed")}\n" +
            $"REVISION: {rand.Next(2, 60)}\n" +
            $"CHART: {ev.Service}-{ev.Version}\n\n" +
            "=== Deployments Status ===\n" +
            $"NAME{new string(' ', 4)}READY   UP-TO-DATE   AVAILABLE\n" +
            $"{ev.Service}    {(failed ? "0/2" : "2/2")}     {(failed ? "1" : "2")}            {(failed ? "0" : "2")}\n";

        yield return MakeLog(ev, "helm upgrade output", "helm", 0, releaseOutput);

        if (!failed) yield break;

        // Read the cause off the run rather than re-rolling one: the highlighted error and the log
        // have to say the same thing, or the page teaches the reader to distrust both.
        var reason = ev.Run?.FailureReason ?? "release workloads did not become ready";
        yield return MakeLog(ev, "failure diagnostics", "kubectl", 1,
            "=== Pods Status ===\n" +
            $"NAME                        READY   STATUS             RESTARTS\n" +
            $"{ev.Service}-7d9c8b5f4-x2klm   0/1     CrashLoopBackOff   4\n\n" +
            $"--- Logs for pod: {ev.Service}-7d9c8b5f4-x2klm ---\n" +
            "info: Starting host\n" +
            "warn: Configuration key 'ConnectionStrings:Default' not found, falling back\n" +
            "fail: Microsoft.Extensions.Hosting.Internal.Host[11]\n" +
            "      Hosting failed to start\n" +
            "Unhandled exception. System.InvalidOperationException: Unable to resolve service\n" +
            "   at Microsoft.Extensions.DependencyInjection.ActivatorUtilities.GetService(...)\n\n" +
            "=== Events ===\n" +
            $"Warning   BackOff   Back-off restarting failed container in pod/{ev.Service}\n\n" +
            $"##[error]Helm deployment failed for {ev.Service} - {reason}\n");
    }

    private static DeployEventLog MakeLog(DeployEvent ev, string name, string source, int sequence, string content) =>
        new()
        {
            Id = Guid.NewGuid(),
            DeployEventId = ev.Id,
            Name = name,
            Source = source,
            Sequence = sequence,
            Content = content,
            Truncated = false,
            ByteCount = System.Text.Encoding.UTF8.GetByteCount(content),
            LineCount = content.Count(c => c == '\n') + 1,
            OriginalByteCount = System.Text.Encoding.UTF8.GetByteCount(content),
            CreatedAt = ev.DeployedAt,
        };

    /// <summary>
    /// The CI run that performed the deployment: where to click, and — when it failed — the one-line
    /// cause the pipeline identified. Modelled on the release repository's GitHub Actions workflow,
    /// which is what deploys in practice, so <c>jobUrl</c> deep-links to the matrix leg for this
    /// component rather than to the run as a whole.
    /// </summary>
    private static DeployRun BuildRun(
        ProductCatalog product, string service, string environment, string version,
        string status, DateTimeOffset deployedAt, Random rand, Person triggeredBy)
    {
        var runId = rand.NextInt64(30_000_000_000, 31_000_000_000);
        var jobId = rand.NextInt64(90_000_000_000, 91_000_000_000);
        var runUrl = $"https://github.com/acmetrix/release/actions/runs/{runId}";

        return new DeployRun(
            Provider: "github-actions",
            RunId: runId.ToString(),
            RunNumber: rand.Next(100, 999).ToString(),
            Attempt: 1,
            WorkflowName: $"Reconcile {environment}",
            JobName: $"Deploy {(product.SourceStyle == SourceStyle.GitHub ? "Web" : "Helm")} ({service}, {version})",
            RunUrl: runUrl,
            JobUrl: $"{runUrl}/job/{jobId}",
            TriggeredBy: triggeredBy.Name,
            StartedAt: deployedAt.AddMinutes(-rand.Next(2, 12)),
            CompletedAt: deployedAt,
            FailureReason: status != "failed" ? null : rand.Next(3) switch
            {
                0 => $"pod {service}-7d9c8b5f4-x2klm keeps crash-looping (restartCount=4)",
                1 => $"pod {service}-7d9c8b5f4-x2klm cannot start (container waiting reason=ImagePullBackOff)",
                _ => "release workloads did not become ready within 1800 seconds",
            });
    }

    private static IEnumerable<DeployEvent> GenerateServiceHistory(
        ProductCatalog product, string service, DateTimeOffset now, Random rand)
    {
        // How busy this particular service is. Rolled per service, so the history is lopsided the way
        // a real estate of services is.
        var eventCount = rand.Next(MinEventsPerService, MaxEventsPerService + 1);

        // Evenly-but-jittered timestamps across the last HistoryDays.
        var totalHours = HistoryDays * 24;
        var slot = totalHours / (double)eventCount;
        var timestamps = new DateTimeOffset[eventCount];
        for (var i = 0; i < eventCount; i++)
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

        for (var i = 0; i < eventCount; i++)
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

            var shuffled = People.OrderBy(_ => rand.Next()).ToArray();
            var references = BuildReferences(product, service, environment, rand, shuffled);
            var enrichment = BuildEnrichment(rand);
            var run = BuildRun(product, service, environment, version, status, timestamps[i], rand, shuffled[0]);

            yield return MakeEvent(
                product.Name, service, environment, version, previousVersion,
                SourceLabel(product.SourceStyle), timestamps[i],
                isRollback, status,
                references, enrichment, run);

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

    private static List<ReferenceDto> BuildReferences(
        ProductCatalog product, string service, string environment, Random rand, Person[] people)
    {
        var refs = new List<ReferenceDto>();

        // Pick distinct people for each role so references carry realistic participants.
        var shuffled = people.OrderBy(_ => rand.Next()).ToArray();
        var triggeredBy = shuffled[0];
        var author      = shuffled[1];
        var reviewer    = shuffled[2];
        var qa          = shuffled[3];

        var buildRunId  = rand.Next(10000, 99999);
        var deployRunId = rand.Next(10000, 99999);
        var prNum       = rand.Next(50, 900);
        // Realistic titles for tickets and PRs so release-notes / activity cards have
        // something to display instead of a bare key.
        var prTitle     = PrTitles[rand.Next(PrTitles.Length)];

        // Build pipeline — triggered by the PR author merging or a scheduled run.
        // Deploy pipeline — triggered separately (CD job, release manager, or scheduler).
        // Each carries its own triggered-by since different people/automation may initiate them.
        if (product.SourceStyle == SourceStyle.AzureDevOps)
        {
            refs.Add(new ReferenceDto("pipeline",
                $"{product.BaseUrl}/_build/results?buildId={buildRunId}", "azure-devops", $"build-{buildRunId}",
                Participants: [new("triggered-by", author.Name, author.Email)]));

            refs.Add(new ReferenceDto("pipeline",
                $"{product.BaseUrl}/_release?releaseId={deployRunId}", "azure-devops", $"deploy-{deployRunId}",
                Participants: [new("triggered-by", triggeredBy.Name, triggeredBy.Email)]));

            refs.Add(new ReferenceDto("pull-request",
                $"{product.BaseUrl}/_git/{service}/pullrequest/{prNum}", "azure-devops", prNum.ToString(),
                Title: prTitle,
                Participants: [
                    new("author",   author.Name,   author.Email),
                    new("reviewer", reviewer.Name, reviewer.Email),
                ]));
        }
        else
        {
            refs.Add(new ReferenceDto("pipeline",
                $"{product.BaseUrl}/{service}/actions/runs/{buildRunId}", "github", $"build-{buildRunId}",
                Participants: [new("triggered-by", author.Name, author.Email)]));

            refs.Add(new ReferenceDto("pipeline",
                $"{product.BaseUrl}/{service}/actions/runs/{deployRunId}", "github", $"deploy-{deployRunId}",
                Participants: [new("triggered-by", triggeredBy.Name, triggeredBy.Email)]));

            refs.Add(new ReferenceDto("pull-request",
                $"{product.BaseUrl}/{service}/pull/{prNum}", "github", prNum.ToString(),
                Title: prTitle,
                Participants: [
                    new("author",   author.Name,   author.Email),
                    new("reviewer", reviewer.Name, reviewer.Email),
                ]));
        }

        // Work items — ~80% of deploys carry at least one, and a good share carry a bundle. The bundle
        // is the case worth seeding: a promotion of several tickets is what the chip row's "+N more"
        // collapse, the "3/5 approved" progress indicator and the per-chip decision colours all exist
        // for, and an estate where every deploy carries exactly one ticket never shows any of them.
        if (rand.NextDouble() < 0.8)
        {
            var workItemCount = rand.NextDouble() switch
            {
                < 0.45 => 1,
                < 0.70 => 2,
                < 0.88 => 3,
                // 4–6. The list collapses the chip row past five, so the top of this range is what
                // makes that button appear.
                _ => 4 + rand.Next(3),
            };

            var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var n = 0; n < workItemCount; n++)
            {
                // Keys are rolled from a small space on purpose: a repeat across two promotions of the
                // same product is a real situation (one ticket blocking several releases) and it's what
                // puts the "×2" badge on a queue row.
                var wiKey = $"{ProductPrefix(product.Name)}-{rand.Next(100, 9999)}";
                if (!usedKeys.Add(wiKey)) continue;

                var wiTitle = WorkItemTitles[rand.Next(WorkItemTitles.Length)];

                // Most tickets name a `qa-owner`, which is the role the seeded production promotion
                // policy requires (see PromotionSeedData); the rest name a plain `qa`. That mix is
                // deliberate: it gives a fresh database both the satisfied case and the one the
                // work-items queue's "Not assigned" tab exists for — somebody is named on the ticket,
                // but not in the role the policy asks for, so nobody is actually answerable for it.
                var qaRole = rand.NextDouble() < 0.7 ? "qa-owner" : "qa";
                // Half of them land on a local sign-in account, weighted towards user@localhost — see
                // LocalTesters. The rest stay with the fictional pool, which is what keeps the "someone
                // else owns this" rows (and the assignee dropdown) worth looking at. Rolled per ticket,
                // so one bundle can be split across several owners — which is the case the bundle-level
                // "needs attention" badge reports on.
                var qaOwner = PickQaOwner(rand, fallback: qa);
                var wiParticipants = new List<ParticipantDto>
                {
                    new(qaRole, qaOwner.Name, qaOwner.Email),
                };
                if (rand.NextDouble() < 0.5)
                {
                    // Assignee is someone other than the QA owner
                    var assignee = shuffled.First(p => p.Email != qaOwner.Email);
                    wiParticipants.Add(new("assignee", assignee.Name, assignee.Email));
                }

                // Most seeded tickets carry a description, but not all — the detail page hides its
                // Content section entirely when there's none, and that path should show up locally too.
                var wiContent = rand.NextDouble() < 0.75 ? WorkItemBody(wiTitle, service, rand) : null;

                refs.Add(new ReferenceDto("work-item",
                    $"https://acmetrix.atlassian.net/browse/{wiKey}", "jira", wiKey,
                    Title: wiTitle,
                    Participants: wiParticipants,
                    Content: wiContent));
            }
        }

        // Build manifest — the release repository's record of exactly what this version is made of
        // (chart, images, source revision). The detail page hangs the version number off this link,
        // so it's pinned to the release-repo commit that deployed rather than to a branch tip.
        // Two GUIDs because a git sha is 40 hex characters and "N" only yields 32.
        var manifestSha = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..40];
        refs.Add(new ReferenceDto("build-manifest",
            $"https://github.com/acmetrix/release/blob/{manifestSha}/{environment}/{service}/build-metadata.yaml",
            "github",
            $"{service}/build-metadata.yaml",
            manifestSha,
            $"build-metadata.yaml @ {service}"));

        // Repository ~40% — just a pointer, no participants.
        if (rand.NextDouble() < 0.4)
        {
            var revision = Guid.NewGuid().ToString("N")[..12];
            if (product.SourceStyle == SourceStyle.AzureDevOps)
                refs.Add(new ReferenceDto("repository",
                    $"{product.BaseUrl}/_git/{service}", "azure-devops",
                    $"{product.Name}/{service}", revision));
            else
                refs.Add(new ReferenceDto("repository",
                    $"{product.BaseUrl}/{service}", "github",
                    $"acmetrix/{service}", revision));
        }

        return refs;
    }

    /// <summary>
    /// Picks who is answerable for a seeded work item. Weighted so <c>user@localhost</c> — the account
    /// the queue is usually demoed from — owns a solid slice, the other two local accounts own a few,
    /// and the rest stay with the fictional person the caller drew.
    /// </summary>
    private static Person PickQaOwner(Random rand, Person fallback)
    {
        var roll = rand.NextDouble();
        if (roll < 0.30) return LocalTesters[0]; // user@localhost
        if (roll < 0.42) return LocalTesters[1]; // admin@localhost
        if (roll < 0.50) return LocalTesters[2]; // qa@localhost
        return fallback;
    }

    /// <summary>
    /// A Jira-shaped description for a seeded work item: a line of context, then acceptance
    /// criteria. Composed from the ticket's own title rather than drawn from a parallel list so the
    /// body always matches the summary above it, and deliberately multi-paragraph so the detail
    /// page's Content section has real line breaks to render — and occasionally enough of them to
    /// exercise the "Show more" collapse.
    /// </summary>
    private static string WorkItemBody(string title, string service, Random rand)
    {
        var summary = title[..1].ToLowerInvariant() + title[1..];
        var criteria = new[]
        {
            $"- Covered by an automated test in `{service}`",
            "- No change to the public contract",
            "- Verified in staging before promotion",
            "- Rollback is a redeploy of the previous version",
            "- Dashboards and alerts updated where affected",
        };
        // 2–5 criteria: enough variance that some seeded tickets are short and some run long.
        var taken = criteria.Take(2 + rand.Next(4));

        return $"Reported by the {service} on-call rotation.\n\n"
             + $"We need to {summary}. The current behaviour has been in place since the last\n"
             + "major release and is now blocking downstream work.\n\n"
             + "Acceptance criteria:\n"
             + string.Join("\n", taken);
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
        "mpt" => "MPT",
        "mpt-extentions" => "MPTX",
        "extra" => "EXT",
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
        List<ReferenceDto> references,
        EnrichmentData? enrichment = null,
        DeployRun? run = null)
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
            ParticipantsJson = "[]",
            EnrichmentJson = enrichmentJson,
            MetadataJson = "{}",
            RunJson = run is null ? null : JsonSerializer.Serialize(run, JsonOptions),
            CreatedAt = deployedAt,
        };
    }
}
