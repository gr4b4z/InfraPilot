using System.Text.Json;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Migrations.Models;

/// <summary>
/// One database migration of a deployed version: its <b>schema</b> migration, which runs inside the
/// deployment (a Helm pre-install/pre-upgrade hook — the new pods never start on a schema they don't
/// know), or its <b>data</b> migration, which runs afterwards as a separate step once the APIs it
/// depends on are out. Read-only in the portal: producers report what ran, the portal shows it.
///
/// <para>Identity is <c>(Kind, Product, Service, Environment, Version, RunKey)</c>. <see cref="RunKey"/>
/// is empty unless the producer distinguishes executions of the same version (a schema migration
/// re-run by a redeploy); a data migration is one row per version, moving Pending → Running →
/// Succeeded / Failed, and a retry after a failure updates the same row
/// (<see cref="Attempts"/> counts them).</para>
///
/// <para>Data migrations supersede like promotions: only the newest version's data migration for a
/// (product, service, environment) is ever run, so queueing one marks every older Pending or Failed
/// one <see cref="MigrationRunStatus.Superseded"/> with <see cref="SupersededById"/> pointing at it.
/// Schema migrations never supersede — each belongs to the deployment that ran it.</para>
/// </summary>
public class MigrationRun
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public Guid Id { get; set; }

    /// <summary><see cref="MigrationKinds.Schema"/> or <see cref="MigrationKinds.Data"/>.</summary>
    public string Kind { get; set; } = MigrationKinds.Data;

    public string Product { get; set; } = "";
    public string Service { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>
    /// Producer-chosen discriminator between executions of the same version; empty when the producer
    /// sent none. Part of the upsert key, never shown.
    /// </summary>
    public string RunKey { get; set; } = "";

    public string Status { get; set; } = MigrationRunStatus.Pending;

    /// <summary>The system that reported it — "mpt-release", "azure-devops".</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The deployment this migration belongs to: for a schema migration the deploy that ran it, for a
    /// data migration the deploy that queued it. Null until a matching deploy event exists — the
    /// migration may be reported before the deployment is — and filled in when it arrives.
    /// </summary>
    public Guid? DeployEventId { get; set; }

    /// <summary>When the migration became due: the deploy finishing, for a data migration.</summary>
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>How many times it was started. 0 while it has never run.</summary>
    public int Attempts { get; set; }

    /// <summary>Why the pipeline did not run it (Skipped) — e.g. the chart renders no migration job.</summary>
    public string? SkipReason { get; set; }

    /// <summary>
    /// The one line that explains a failure: the producer's own (<c>run.failureReason</c>), else the
    /// error of the first failed migration read from the log. A column rather than a read of
    /// <see cref="RunJson"/> so the list can show it.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>The newer data migration that replaced this one. Set together with Superseded.</summary>
    public Guid? SupersededById { get; set; }

    /// <summary>Serialized <see cref="DeployRun"/> — the CI run that executed (or queued) it.</summary>
    public string? RunJson { get; set; }

    /// <summary>
    /// Counts over the run's <see cref="MigrationRunItem"/> rows, materialised so the list can say
    /// "12 applied, 1 failed" without reading them.
    /// </summary>
    public int AppliedCount { get; set; }
    public int FailedCount { get; set; }
    public int TotalCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DeployRun? Run
    {
        get => string.IsNullOrEmpty(RunJson) ? null : JsonSerializer.Deserialize<DeployRun>(RunJson, JsonOptions);
        set => RunJson = value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    }
}

/// <summary>
/// One migration inside a run, as stored: its own table rather than JSON on the run, because the
/// question people bring is by migration — "has <c>AlterAccountName</c> run on staging yet?" — across
/// every run of every service, and that has to be an indexed lookup.
/// </summary>
public class MigrationRunItem
{
    public Guid Id { get; set; }
    public Guid MigrationRunId { get; set; }

    /// <summary>Position within the run, as the output listed them.</summary>
    public int Sequence { get; set; }

    /// <summary>See <see cref="MigrationItem.Name"/>.</summary>
    public string Name { get; set; } = "";

    public string Status { get; set; } = MigrationItemStatus.Applied;
    public int? Order { get; set; }
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }

    public MigrationItem ToItem() => new(Name, Status, DurationMs, Error, Order, AppliedAt);
}

/// <summary>
/// One migration inside a run, by the id the MPT migration library prints it under — the script's
/// file name without its order prefix (<c>4650_AlterAccountName.sql</c> → <c>AlterAccountName</c>).
/// </summary>
/// <param name="Status">One of <see cref="MigrationItemStatus"/>.</param>
/// <param name="DurationMs">How long it took, when the log or the producer says.</param>
/// <param name="Error">The error it failed with — the library's message, verbatim.</param>
/// <param name="Order">The order prefix of the script's file name, which is what the library runs them by.</param>
/// <param name="AppliedAt">When the library recorded it in its history table (UTC).</param>
public record MigrationItem(
    string Name,
    string Status,
    long? DurationMs = null,
    string? Error = null,
    int? Order = null,
    DateTimeOffset? AppliedAt = null);

public static class MigrationKinds
{
    public const string Schema = "schema";
    public const string Data = "data";

    public static readonly string[] All = [Schema, Data];
}

public static class MigrationRunStatus
{
    /// <summary>Due but not run yet — the service is deployed and its data migration is waiting (or not even scheduled).</summary>
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    /// <summary>The pipeline looked and found nothing to run — no migration job for this version.</summary>
    public const string Skipped = "skipped";
    /// <summary>A newer version's data migration replaced it; it will never run.</summary>
    public const string Superseded = "superseded";

    /// <summary>What a producer may send. Superseded is the server's call, never the sender's.</summary>
    public static readonly string[] Reportable = [Pending, Running, Succeeded, Failed, Skipped];

    /// <summary>States a newer data migration replaces: it never ran, or ran and has to run again.</summary>
    public static readonly string[] Supersedable = [Pending, Failed];

    /// <summary>States that need someone's attention — the flare on a deployment.</summary>
    public static readonly string[] Attention = [Pending, Running, Failed];
}

public static class MigrationItemStatus
{
    /// <summary>Ran and was recorded (the library's <c>[✓]</c>).</summary>
    public const string Applied = "applied";
    /// <summary>The library's <c>[x]</c>.</summary>
    public const string Failed = "failed";
    /// <summary>
    /// Undone because a later migration in the same transactional batch failed (<c>[&lt;]</c>). It runs
    /// again next time.
    /// </summary>
    public const string RolledBack = "rolled-back";
    /// <summary>Still running when the output ended (<c>[r]</c>).</summary>
    public const string Running = "running";
    /// <summary>Queued in this run but never started — the run stopped at an earlier failure (<c>[.]</c>).</summary>
    public const string Pending = "pending";
    /// <summary>Listed but not executed — for producers that report their own list.</summary>
    public const string Skipped = "skipped";

    public static readonly string[] All = [Applied, Failed, RolledBack, Running, Pending, Skipped];
}
