using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.BackgroundServices;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests covering the work-item projection table. Two groups:
///   1. Live ingest path (<see cref="DeploymentService.IngestEvent"/> calling
///      <see cref="DeploymentService.SyncWorkItemsAsync"/>).
///   2. Startup backfill (<see cref="DeployEventWorkItemBackfillService"/>).
///
/// Each test owns a fresh <see cref="TestFactory"/> so the SQLite in-memory database
/// is isolated — backfill tests in particular need control over the
/// <c>platform_settings</c> flag row, and bleed-through from another test would defeat
/// that. The cost of spinning the host up per test is acceptable here (ten tests).
/// </summary>
public class DeployEventWorkItemTests
{
    private const string BackfillFlagKey = "deploy_event_work_items_backfilled_at";

    // ── Ingest tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_ExtractsWorkItemReferencesIntoJoinTable()
    {
        await using var factory = new TestFactory();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentService>();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var dto = NewDto(product: "acme", references: new List<ReferenceDto>
        {
            new("work-item",   Key: "FOO-123"),
            new("pull-request", Key: "42"),
            new("work-item",   Key: "FOO-456"),
        });

        var ev = await service.IngestEvent(dto);

        var rows = await db.DeployEventWorkItems
            .Where(w => w.DeployEventId == ev.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        var keys = rows.Select(r => r.WorkItemKey).OrderBy(k => k).ToList();
        Assert.Equal(new[] { "FOO-123", "FOO-456" }, keys);
        Assert.All(rows, r => Assert.Equal("acme", r.Product));
    }

    [Fact]
    public async Task Ingest_IsIdempotent_OnReingestOfSameEvent()
    {
        await using var factory = new TestFactory();

        // First ingest creates the event and its work items.
        Guid eventId;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DeploymentService>();
            var dto = NewDto(references: new List<ReferenceDto>
            {
                new("work-item", Key: "FOO-1"),
                new("work-item", Key: "FOO-2"),
            });
            var ev = await service.IngestEvent(dto);
            eventId = ev.Id;
        }

        // Re-run SyncWorkItemsAsync against the existing event. Calling IngestEvent
        // again would create a *new* DeployEvent (the natural-key dedup is a separate
        // admin path). The "re-ingest" semantics for SyncWorkItemsAsync is what
        // matters — it must not produce duplicate join rows for the same event.
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DeploymentService>();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var ev = await db.DeployEvents.FirstAsync(e => e.Id == eventId);

            await service.SyncWorkItemsAsync(ev);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.DeployEventWorkItems
                .Where(w => w.DeployEventId == eventId)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
        }
    }

    [Fact]
    public async Task Ingest_RemovesStaleRow_WhenReferenceIsDropped()
    {
        await using var factory = new TestFactory();

        Guid eventId;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DeploymentService>();
            var dto = NewDto(references: new List<ReferenceDto>
            {
                new("work-item", Key: "FOO-1"),
                new("work-item", Key: "FOO-2"),
            });
            var ev = await service.IngestEvent(dto);
            eventId = ev.Id;
        }

        // Re-sync with a shrunken references list: FOO-2 should be removed.
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DeploymentService>();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var ev = await db.DeployEvents.FirstAsync(e => e.Id == eventId);

            // Mutate the references payload to drop FOO-2.
            ev.References = new List<Reference>
            {
                new("work-item", Key: "FOO-1"),
            };

            await service.SyncWorkItemsAsync(ev);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.DeployEventWorkItems
                .Where(w => w.DeployEventId == eventId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.Equal("FOO-1", rows[0].WorkItemKey);
        }
    }

    [Fact]
    public async Task Ingest_SkipsWorkItemWithEmptyKey()
    {
        await using var factory = new TestFactory();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentService>();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var dto = NewDto(references: new List<ReferenceDto>
        {
            new("work-item", Key: ""),
            new("work-item", Key: "   "),
            new("work-item", Key: null),
        });

        var ev = await service.IngestEvent(dto);

        var rows = await db.DeployEventWorkItems
            .Where(w => w.DeployEventId == ev.Id)
            .ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Ingest_DedupesWithinSameEvent()
    {
        await using var factory = new TestFactory();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DeploymentService>();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var dto = NewDto(references: new List<ReferenceDto>
        {
            new("work-item", Key: "FOO-1"),
            new("work-item", Key: "foo-1"),
        });

        var ev = await service.IngestEvent(dto);

        var rows = await db.DeployEventWorkItems
            .Where(w => w.DeployEventId == ev.Id)
            .ToListAsync();
        Assert.Single(rows);
    }

    // ── Backfill tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_PopulatesMissingRowsForExistingEvents()
    {
        await using var factory = new TestFactory();

        Guid eventId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var ev = NewDeployEventEntity(
                product: "acme",
                deployedAt: DateTimeOffset.UtcNow.AddDays(-5),
                references: new[] { ("work-item", "BAR-1"), ("work-item", "BAR-2") });
            db.DeployEvents.Add(ev);
            await EnsureBackfillFlagAbsentAsync(db);
            await db.SaveChangesAsync();
            eventId = ev.Id;
        }

        await RunBackfillAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.DeployEventWorkItems
                .Where(w => w.DeployEventId == eventId)
                .OrderBy(w => w.WorkItemKey)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "BAR-1", "BAR-2" }, rows.Select(r => r.WorkItemKey).ToArray());
            Assert.All(rows, r => Assert.Equal("acme", r.Product));

            // Flag should be set after a successful run.
            var flag = await db.PlatformSettings.FindAsync(BackfillFlagKey);
            Assert.NotNull(flag);
        }
    }

    [Fact]
    public async Task Backfill_IsIdempotent_NoOpsWhenFlagSet()
    {
        await using var factory = new TestFactory();

        Guid eventId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var ev = NewDeployEventEntity(
                deployedAt: DateTimeOffset.UtcNow.AddDays(-3),
                references: new[] { ("work-item", "BAZ-1") });
            db.DeployEvents.Add(ev);

            // Pre-set the flag so backfill should no-op.
            db.PlatformSettings.Add(new PlatformSetting
            {
                Key = BackfillFlagKey,
                Value = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedBy = "system:test",
            });

            await db.SaveChangesAsync();
            eventId = ev.Id;
        }

        await RunBackfillAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.DeployEventWorkItems
                .Where(w => w.DeployEventId == eventId)
                .ToListAsync();
            Assert.Empty(rows);
        }
    }

    [Fact]
    public async Task Backfill_OnlyProcessesEventsWithinLast90Days()
    {
        await using var factory = new TestFactory();

        Guid recentId;
        Guid oldId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            var recent = NewDeployEventEntity(
                deployedAt: DateTimeOffset.UtcNow.AddDays(-60),
                references: new[] { ("work-item", "RECENT-1") });
            var old = NewDeployEventEntity(
                deployedAt: DateTimeOffset.UtcNow.AddDays(-120),
                references: new[] { ("work-item", "OLD-1") });

            db.DeployEvents.Add(recent);
            db.DeployEvents.Add(old);
            await EnsureBackfillFlagAbsentAsync(db);
            await db.SaveChangesAsync();
            recentId = recent.Id;
            oldId = old.Id;
        }

        await RunBackfillAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            var recentRows = await db.DeployEventWorkItems
                .Where(w => w.DeployEventId == recentId)
                .ToListAsync();
            Assert.Single(recentRows);
            Assert.Equal("RECENT-1", recentRows[0].WorkItemKey);

            var oldRows = await db.DeployEventWorkItems
                .Where(w => w.DeployEventId == oldId)
                .ToListAsync();
            Assert.Empty(oldRows);
        }
    }

    [Fact]
    public async Task Backfill_DoesNotOverwriteExistingRows()
    {
        await using var factory = new TestFactory();

        Guid eventId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var ev = NewDeployEventEntity(
                deployedAt: DateTimeOffset.UtcNow.AddDays(-2),
                references: new[] { ("work-item", "QUX-1") });
            db.DeployEvents.Add(ev);

            // Pre-existing row that backfill must NOT clobber.
            db.DeployEventWorkItems.Add(new DeployEventWorkItem
            {
                Id = Guid.NewGuid(),
                DeployEventId = ev.Id,
                WorkItemKey = "QUX-1",
                Product = ev.Product,
                Title = "manually-set",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });

            await EnsureBackfillFlagAbsentAsync(db);
            await db.SaveChangesAsync();
            eventId = ev.Id;
        }

        await RunBackfillAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.DeployEventWorkItems
                .Where(w => w.DeployEventId == eventId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.Equal("manually-set", rows[0].Title);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CreateDeployEventDto NewDto(
        string product = "acme",
        string service = "api",
        string environment = "staging",
        string version = "v1.0.0",
        List<ReferenceDto>? references = null)
    {
        return new CreateDeployEventDto(
            Product: product,
            Service: service,
            Environment: environment,
            Version: version,
            Source: "ci",
            DeployedAt: DateTimeOffset.UtcNow,
            References: references,
            Participants: null,
            Metadata: null);
    }

    private static DeployEvent NewDeployEventEntity(
        string product = "acme",
        string service = "api",
        string environment = "staging",
        string version = "v1.0.0",
        DateTimeOffset? deployedAt = null,
        IEnumerable<(string Type, string Key)>? references = null)
    {
        var refs = (references ?? Array.Empty<(string, string)>())
            .Select(r => new ReferenceDto(r.Type, Key: r.Key))
            .ToList();

        return new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = environment,
            Version = version,
            Source = "ci",
            Status = "succeeded",
            DeployedAt = deployedAt ?? DateTimeOffset.UtcNow,
            ReferencesJson = System.Text.Json.JsonSerializer.Serialize(refs, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }),
            ParticipantsJson = "[]",
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Drives <see cref="DeployEventWorkItemBackfillService.ExecuteAsync"/> deterministically
    /// from the test thread. Constructs a fresh service against the test host's scope factory
    /// and uses reflection to call the protected hosted-service method directly — avoids the
    /// 5-second startup delay and lets tests assert synchronously after the call returns.
    /// </summary>
    private static async Task RunBackfillAsync(TestFactory factory)
    {
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var svc = new DeployEventWorkItemBackfillService(
            scopeFactory,
            NullLogger<DeployEventWorkItemBackfillService>.Instance);

        // Use a token that's already (well past) the 5-second delay so the Task.Delay inside
        // ExecuteAsync returns immediately. We pass CancellationToken.None — the service
        // catches OperationCanceledException specifically and would skip the work; we want
        // it to run to completion so we accept the 5-second delay.
        var method = typeof(DeployEventWorkItemBackfillService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ExecuteAsync not found");

        var task = (Task)method.Invoke(svc, new object[] { CancellationToken.None })!;
        await task;
    }

    /// <summary>
    /// Make sure the backfill flag is not present so the service actually runs. EnsureCreated
    /// gives us a fresh schema per <see cref="TestFactory"/>, but a defensive delete keeps the
    /// helpers composable if anyone changes the harness later.
    /// </summary>
    private static async Task EnsureBackfillFlagAbsentAsync(PlatformDbContext db)
    {
        var flag = await db.PlatformSettings.FindAsync(BackfillFlagKey);
        if (flag is not null)
        {
            db.PlatformSettings.Remove(flag);
            await db.SaveChangesAsync();
        }
    }
}
