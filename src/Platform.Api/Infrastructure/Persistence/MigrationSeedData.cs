using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Migrations;
using Platform.Api.Features.Migrations.Models;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Demo migrations for a local database, built on top of the seeded deployments: a few services get
/// a schema migration on every deploy (the hook output the release pipeline streams, in the MPT
/// migration library's own format) and a data migration after every successful one. Each version
/// introduces its own migrations and an environment applies them when that version reaches it, so
/// "has AddOrderIndex run on staging?" has a consistent answer. The output is written as text and
/// read back through <see cref="MigrationLogParser"/>, so the demo exercises the same parsing real
/// deployments do. Deterministic, and skipped once any migration exists.
/// </summary>
public static class MigrationSeedData
{
    private static readonly (string Product, string Service, bool Data)[] Services =
    [
        ("mpt", "orders", true),
        ("mpt", "billing", true),
        ("mpt", "checkout", false),
        ("mpt", "pricing", false),
        ("mpt-extentions", "loyalty", true),
    ];

    private static readonly string[] SchemaNames =
    [
        "AddOrderIndex", "AlterAccountName", "AddInvoiceTotals", "CreateAuditTable", "AddSubscriptionTermIndex",
        "DropLegacyPriceColumns", "AddCurrencyToOrderLines", "CreateExchangeRateTable", "AddBuyerReference",
        "AlterProductItemNameLength", "AddAgreementStatusIndex", "CreateNotificationOutbox", "AddCartExpiry",
        "RenameSellerColumns", "AddPriceListValidity", "CreateLoyaltyTierTable", "AddPointsLedgerIndex",
        "AlterDiscountPrecision", "AddCheckoutSessionTable", "AddTaxRegionToAddress",
    ];

    private static readonly string[] DataNames =
    [
        "BackfillCurrencyCodes", "SyncIdentitiesToNotification", "RecalculateInvoiceTotals", "BackfillBuyerReferences",
        "MigrateLegacyPriceLists", "RebuildSearchIndex", "BackfillLoyaltyTiers", "NormalizeTaxRegions",
        "CopyAuditHistory", "ReassignOrphanedCarts",
    ];

    public static async Task Seed(PlatformDbContext db)
    {
        if (await db.MigrationRuns.AnyAsync()) return;

        var rand = new Random(20261102);
        var schemaOrder = 4000;
        var dataOrder = 9000;
        var schemaPool = new Queue<string>(SchemaNames.OrderBy(_ => rand.Next()));
        var dataPool = new Queue<string>(DataNames.OrderBy(_ => rand.Next()));

        foreach (var (product, service, hasData) in Services)
        {
            var events = await db.DeployEvents.AsNoTracking()
                .Where(e => e.Product == product && e.Service == service && e.Status != DeployEventStatuses.Removed)
                .OrderBy(e => e.DeployedAt)
                .ToListAsync();
            if (events.Count == 0) continue;

            // Each version introduces 0–2 schema and 0–1 data migrations, in the order versions first shipped.
            var versions = events.Select(e => e.Version).Distinct().ToList();
            var schemaByVersion = versions.ToDictionary(v => v, _ => Enumerable.Range(0, rand.Next(0, 3))
                .Select(_ => (Order: schemaOrder += 10, Name: Next(schemaPool, SchemaNames, rand))).ToList());
            var dataByVersion = versions.ToDictionary(v => v, _ => rand.NextDouble() < 0.45
                ? new List<(int Order, string Name)> { (dataOrder += 1, Next(dataPool, DataNames, rand)) }
                : []);

            var schemaApplied = new Dictionary<string, int>(); // env → index of the last version whose migrations ran
            var dataQueued = new Dictionary<string, int>();
            var pendingData = new List<(MigrationRun Run, List<(int, string)> Items, int Index)>();

            foreach (var ev in events)
            {
                var idx = versions.IndexOf(ev.Version);
                var from = schemaApplied.GetValueOrDefault(ev.Environment, -1);
                var due = idx > from
                    ? versions.Skip(from + 1).Take(idx - from).SelectMany(v => schemaByVersion[v]).ToList()
                    : [];

                // A failed deploy is a failed schema migration a third of the time — the rest failed later.
                var migrationFailed = ev.Status == "failed" && rand.NextDouble() < 0.35;
                var output = SchemaOutput(due, migrationFailed, ev.DeployedAt, rand);
                db.DeployEventLogs.Add(HookLog(ev, output));
                AddRun(db, MigrationKinds.Schema, ev, MigrationService.DerivedRunKeyPrefix + ev.Id,
                    migrationFailed ? MigrationRunStatus.Failed : MigrationRunStatus.Succeeded,
                    ev.DeployedAt, output, "helm");
                if (!migrationFailed && ev.Status == "succeeded" && idx > from) schemaApplied[ev.Environment] = idx;

                if (!hasData || ev.Status != "succeeded") continue;
                var queuedFrom = dataQueued.GetValueOrDefault(ev.Environment, -1);
                if (idx <= queuedFrom) continue;
                var dataDue = versions.Skip(queuedFrom + 1).Take(idx - queuedFrom).SelectMany(v => dataByVersion[v]).ToList();
                dataQueued[ev.Environment] = idx;
                var run = AddRun(db, MigrationKinds.Data, ev, "", MigrationRunStatus.Pending, ev.DeployedAt.AddMinutes(2), null, null);
                pendingData.Add((run, dataDue, idx));
            }

            // Settle the data migrations: superseded when a newer one was queued in the same environment
            // before they ran, otherwise run (mostly fine) — except the newest per environment, which
            // is left in a mix of states so every flare has an example.
            foreach (var group in pendingData.GroupBy(p => p.Run.Environment))
            {
                var list = group.OrderBy(p => p.Run.QueuedAt).ToList();
                for (var i = 0; i < list.Count; i++)
                {
                    var (run, items, _) = list[i];
                    var newest = i == list.Count - 1;
                    if (!newest && rand.NextDouble() < 0.25)
                    {
                        run.Status = MigrationRunStatus.Superseded;
                        run.SupersededById = list[i + 1].Run.Id;
                        continue;
                    }
                    var outcome = newest
                        ? (group.Key, service) switch
                        {
                            ("production", _) => MigrationRunStatus.Pending,
                            ("staging", "billing") => MigrationRunStatus.Failed,
                            ("staging", _) => MigrationRunStatus.Pending,
                            _ => rand.NextDouble() < 0.2 ? MigrationRunStatus.Failed : MigrationRunStatus.Succeeded,
                        }
                        : rand.NextDouble() < 0.1 ? MigrationRunStatus.Failed : MigrationRunStatus.Succeeded;
                    if (outcome == MigrationRunStatus.Pending) continue;
                    // A failure that a newer version has since replaced is superseded, not still failing.
                    if (outcome == MigrationRunStatus.Failed && !newest)
                    {
                        run.Status = MigrationRunStatus.Superseded;
                        run.SupersededById = list[i + 1].Run.Id;
                    }
                    else
                    {
                        run.Status = outcome;
                    }
                    var started = run.QueuedAt.AddMinutes(rand.Next(5, 600));
                    var output = DataOutput(items, outcome == MigrationRunStatus.Failed, started, rand);
                    run.StartedAt = started;
                    run.CompletedAt = started.AddSeconds(rand.Next(20, 900));
                    run.Attempts = outcome == MigrationRunStatus.Failed ? 3 : 1;
                    run.Run = new DeployRun(
                        Provider: "github-actions", RunId: $"{rand.Next(40_000_000, 49_000_000)}0", RunNumber: rand.Next(2, 90).ToString(),
                        WorkflowName: "Data Migration", JobName: $"Migrate {service}",
                        RunUrl: "https://github.com/acmetrix/release/actions/runs/1", StartedAt: started,
                        CompletedAt: run.CompletedAt);
                    ApplyOutput(db, run, output, "kubectl", "data migration job output");
                }
            }
        }

        await db.SaveChangesAsync();
    }

    private static string Next(Queue<string> pool, string[] all, Random rand)
    {
        if (pool.Count == 0) foreach (var n in all.OrderBy(_ => rand.Next())) pool.Enqueue(n + "V2");
        return pool.Dequeue();
    }

    private static MigrationRun AddRun(
        PlatformDbContext db, string kind, DeployEvent ev, string runKey, string status,
        DateTimeOffset queuedAt, string? output, string? source)
    {
        var run = new MigrationRun
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Product = ev.Product,
            Service = ev.Service,
            Environment = ev.Environment,
            Version = ev.Version,
            RunKey = runKey,
            Status = status,
            Source = kind == MigrationKinds.Schema ? ev.Source : "github-actions",
            DeployEventId = ev.Id,
            QueuedAt = queuedAt,
            StartedAt = kind == MigrationKinds.Schema ? ev.DeployedAt : null,
            CompletedAt = kind == MigrationKinds.Schema ? ev.DeployedAt.AddSeconds(20) : null,
            Attempts = kind == MigrationKinds.Schema ? 1 : 0,
            RunJson = kind == MigrationKinds.Schema ? ev.RunJson : null,
            CreatedAt = queuedAt,
            UpdatedAt = queuedAt,
        };
        db.MigrationRuns.Add(run);
        if (output is not null) ApplyOutput(db, run, output, source ?? "helm", MigrationService.DerivedLogName);
        return run;
    }

    private static void ApplyOutput(PlatformDbContext db, MigrationRun run, string output, string source, string logName)
    {
        var parsed = MigrationLogParser.Parse(output);
        for (var i = 0; i < parsed.Items.Count; i++)
        {
            var item = parsed.Items[i];
            db.MigrationRunItems.Add(new MigrationRunItem
            {
                Id = Guid.NewGuid(), MigrationRunId = run.Id, Sequence = i, Name = item.Name, Status = item.Status,
                Order = item.Order, DurationMs = item.DurationMs, Error = item.Error, AppliedAt = item.AppliedAt,
            });
        }
        run.AppliedCount = parsed.Items.Count(i => i.Status == MigrationItemStatus.Applied);
        run.FailedCount = parsed.Items.Count(i => i.Status == MigrationItemStatus.Failed);
        run.TotalCount = parsed.Items.Count;
        run.FailureReason = run.Status is MigrationRunStatus.Failed or MigrationRunStatus.Superseded ? parsed.FailureReason : null;
        db.MigrationRunLogs.Add(new MigrationRunLog
        {
            Id = Guid.NewGuid(), MigrationRunId = run.Id, Name = logName, Source = source, Sequence = 0,
            Content = output, ByteCount = Encoding.UTF8.GetByteCount(output), LineCount = output.Count(c => c == '\n') + 1,
            OriginalByteCount = Encoding.UTF8.GetByteCount(output), CreatedAt = run.CompletedAt ?? run.QueuedAt,
        });
    }

    private static DeployEventLog HookLog(DeployEvent ev, string output)
    {
        var pod = $"{ev.Service}-migrations-{ev.Id.ToString("N")[..5]}";
        var content =
            $"=======================Logs from hook pod/{pod} (job {ev.Service}-migrations)=======================\n" +
            output + "\n" +
            $"=======================End logs from hook pod/{pod}=======================\n";
        return new DeployEventLog
        {
            Id = Guid.NewGuid(), DeployEventId = ev.Id, Name = "helm hook logs", Source = "helm", Sequence = 5,
            Content = content, ByteCount = Encoding.UTF8.GetByteCount(content), LineCount = content.Count(c => c == '\n') + 1,
            OriginalByteCount = Encoding.UTF8.GetByteCount(content), CreatedAt = ev.DeployedAt,
        };
    }

    /// <summary><c>migrate --schema</c> stdout: the table re-printed after each migration, then the outcome.</summary>
    private static string SchemaOutput(List<(int Order, string Name)> due, bool fail, DateTimeOffset at, Random rand)
    {
        var sb = new StringBuilder();
        if (due.Count == 0 && !fail)
        {
            sb.Append("No migrations to run.\nSchema migrations completed.");
            return sb.ToString();
        }
        if (due.Count == 0)
        {
            // Failed before the library got to its table — the classic bad connection string.
            var t = at.UtcDateTime.ToString("HH:mm:ss");
            sb.Append($"[{t} ERR] Format of the initialization string does not conform to specification starting at index 0.\n");
            sb.Append("Format of the initialization string does not conform to specification starting at index 0.");
            return sb.ToString();
        }

        var width = due.Max(d => d.Name.Length);
        var failAt = fail ? rand.Next(due.Count) : -1;
        var applied = new List<(int Order, string Name, DateTimeOffset At, int Secs)>();
        string Row(string mark, (int Order, string Name) d, string tail) => $"[{mark}] {d.Order} {d.Name.PadRight(width)}      1m {tail}";
        foreach (var d in due) sb.Append(Row(".", d, "                  -")).Append('\n');
        var clock = at.AddSeconds(3);
        for (var i = 0; i < due.Count; i++)
        {
            if (i == failAt)
            {
                const string error = "Invalid column name 'DisplayName'.";
                foreach (var a in applied) sb.Append(Row("<", (a.Order, a.Name), $"{a.At:yyyy-MM-ddTHH:mm:ss}")).Append('\n');
                sb.Append(Row("x", due[i], $"{clock:yyyy-MM-ddTHH:mm:ss}               0s {error}")).Append('\n');
                for (var j = i + 1; j < due.Count; j++) sb.Append(Row(".", due[j], "                  -")).Append('\n');
                var block = new StringBuilder("The following migrations have failed:\n")
                    .Append($"- {due[i].Name}: {error}\n");
                foreach (var a in applied) block.Append($"- {a.Name}: Migration rolled back\n");
                sb.Append(block).Append('\n').Append(block);
                return sb.ToString().TrimEnd();
            }
            var secs = rand.Next(0, 40);
            clock = clock.AddSeconds(secs);
            applied.Add((due[i].Order, due[i].Name, clock, secs));
            foreach (var a in applied) sb.Append(Row("✓", (a.Order, a.Name), $"{a.At:yyyy-MM-ddTHH:mm:ss}")).Append('\n');
            for (var j = i + 1; j < due.Count; j++) sb.Append(Row(".", due[j], "                  -")).Append('\n');
        }
        foreach (var a in applied)
            sb.Append(Row("✓", (a.Order, a.Name), $"{a.At:yyyy-MM-ddTHH:mm:ss}               {a.Secs}s")).Append('\n');
        sb.Append("Schema migrations completed.");
        return sb.ToString();
    }

    /// <summary><c>migrate --data</c> stdout: a row per migration as it runs, script output in between.</summary>
    private static string DataOutput(List<(int Order, string Name)> due, bool fail, DateTimeOffset at, Random rand)
    {
        if (due.Count == 0) return fail
            ? $"[{at.UtcDateTime:HH:mm:ss} ERR] Could not read history of migrations from database"
            : "Data migrations completed.";
        var sb = new StringBuilder();
        var width = due.Max(d => d.Name.Length);
        var clock = at;
        var done = new List<string>();
        for (var i = 0; i < due.Count; i++)
        {
            var d = due[i];
            sb.Append($"[.] {d.Order} {d.Name.PadRight(width)}      1m                   -\n");
            sb.Append($"[{clock.UtcDateTime:HH:mm:ss} INF] Starting {d.Name} ...\n");
            var secs = rand.Next(5, 300);
            clock = clock.AddSeconds(secs);
            if (fail && i == due.Count - 1)
            {
                sb.Append($"[{clock.UtcDateTime:HH:mm:ss} ERR] Target module notifications unavailable\n");
                foreach (var row in done) sb.Append(row).Append('\n');
                sb.Append($"[x] {d.Order} {d.Name.PadRight(width)}      1m {clock:yyyy-MM-ddTHH:mm:ss}             {secs}s Failed with exitCode: '1'\n");
                sb.Append($"The following migrations have failed:\n- {d.Name}: Failed with exitCode: '1'\n");
                return sb.ToString().TrimEnd();
            }
            var ok = $"[✓] {d.Order} {d.Name.PadRight(width)}      1m {clock:yyyy-MM-ddTHH:mm:ss}             {secs}s";
            done.Add(ok);
            sb.Append($"[{clock.UtcDateTime:HH:mm:ss} INF] {d.Name} done\n").Append(ok).Append('\n');
        }
        sb.Append("Data migrations completed.");
        return sb.ToString();
    }
}
