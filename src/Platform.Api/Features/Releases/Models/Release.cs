namespace Platform.Api.Features.Releases.Models;

/// <summary>
/// A versioned release train — "5.10" — carried from the day its release branches are tagged to
/// the day QA signs production off. The release is a checklist the release manager drives
/// (<see cref="Steps"/>) laid over facts the portal already records: which services run a build of
/// the version line on staging, the staging → production promotions of those builds, their
/// production deploys and the migrations of the deployed versions. Those facts are computed at
/// read time (see <see cref="ReleaseProgressService"/>), never copied in, so a backfilled deploy or
/// a late migration report shows up on the release without anybody touching it.
///
/// <para><b>Scope.</b> Every service of the release's products ships with it — each one present on
/// <see cref="StagingEnvironment"/> or <see cref="ProductionEnvironment"/> — except those the release
/// manager left out (<see cref="ExcludedServicesJson"/>). A service's records are its builds and
/// promotions of the version line (<c>5.10</c>, <c>5.10.x</c>, <c>5.10.x-…</c>), or, for a service on a
/// version scheme of its own, whatever it deployed and promoted during the release.</para>
///
/// <para><b>Window.</b> Evidence is read from <see cref="StartedAt"/> (the version cut; the creation
/// time until then) up to <see cref="CompletedAt"/>, so a completed release keeps describing the day
/// it shipped after the next hotfix of the line goes out.</para>
/// </summary>
public class Release
{
    public Guid Id { get; set; }

    /// <summary>The version line, <c>major.minor</c> — "5.10", "6.0". Builds of the line are <c>5.10.x</c>.</summary>
    public string Version { get; set; } = "";

    /// <summary>Display name — "Release 5.10" unless the release manager calls it something else.</summary>
    public string Title { get; set; } = "";

    /// <summary>Products whose services ship with this release, as a JSON array of product names.</summary>
    public string ProductsJson { get; set; } = "[]";

    /// <summary>Where the release is tested before production. A canonical environment key.</summary>
    public string StagingEnvironment { get; set; } = "staging";

    /// <summary>Where the release ships. A canonical environment key.</summary>
    public string ProductionEnvironment { get; set; } = "prod";

    /// <summary>The planned production release date.</summary>
    public DateOnly? TargetDate { get; set; }

    /// <summary>One of <see cref="ReleaseStatuses"/>. Derived from the dates and steps on every write.</summary>
    public string Status { get; set; } = ReleaseStatuses.Planned;

    /// <summary>Free text: scope notes, the go/no-go call.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Where the release lives outside the portal — the issue-tracker version, the release plan, the
    /// retro — as a JSON array of <see cref="ReleaseLink"/>. The pipeline runs need no entry here: the
    /// portal reads them off the deploys and migrations themselves.
    /// </summary>
    public string LinksJson { get; set; } = "[]";

    /// <summary>
    /// Services of the products that do not ship with this release, as a JSON array of
    /// <c>product/service</c>. Empty by default: every service of the products ships.
    /// </summary>
    public string ExcludedServicesJson { get; set; } = "[]";

    /// <summary>When the release got going — planning or the version cut. Set by the first step that starts, or by hand.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the release manager closed the release.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public string CreatedBy { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ReleaseStep> Steps { get; set; } = [];
}

/// <summary>
/// One step of a release, in <see cref="Order"/>. A step is always progressed by a person — Start,
/// Mark done, Skip — and may carry a <see cref="Check"/>: a measurement the portal makes from its own
/// records (see <see cref="ReleaseChecks"/>) to show how far the step has got and what is holding it.
/// The check informs the decision; it does not make it, so a step is never completed behind the
/// release manager's back.
/// </summary>
public class ReleaseStep
{
    public Guid Id { get; set; }
    public Guid ReleaseId { get; set; }
    public int Order { get; set; }

    /// <summary>Stable identifier from the template ("version-cut", "production-deploys"); "custom-…" for added steps.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Who usually does it — "Release manager", "QA". A label, not a permission.</summary>
    public string? Owner { get; set; }

    /// <summary>One of <see cref="ReleaseChecks"/>, or null for a step only a person can judge.</summary>
    public string? Check { get; set; }

    /// <summary>One of <see cref="ReleaseStepStatuses"/>.</summary>
    public string Status { get; set; } = ReleaseStepStatuses.Pending;

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public string? CompletedByName { get; set; }

    /// <summary>What happened — "swo-platform needed a rebuild", "signed off in the QA channel".</summary>
    public string? Note { get; set; }

    /// <summary>Evidence, as a JSON array of <see cref="ReleaseLink"/>: pipeline runs, a test report, a tag, a ticket.</summary>
    public string LinksJson { get; set; } = "[]";

    /// <summary>Added by the release manager rather than created from the template.</summary>
    public bool Custom { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A link with an optional label; without one the page names it from the address.</summary>
public record ReleaseLink(string? Label, string Url);

public static class ReleaseStatuses
{
    public const string Planned = "planned";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Planned, InProgress, Completed, Cancelled];
}

public static class ReleaseStepStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Skipped = "skipped";

    public static readonly string[] All = [Pending, InProgress, Done, Skipped];

    /// <summary>Nothing left to do on it.</summary>
    public static bool IsClosed(string status) => status is Done or Skipped;
}

/// <summary>The measurements a step can carry. See <see cref="ReleaseProgressService"/>.</summary>
public static class ReleaseChecks
{
    /// <summary>Services running a build of the line on the staging environment.</summary>
    public const string StagingBuilds = "staging-builds";

    /// <summary>Production promotions that passed the QA gate (the "QA Review" approval step).</summary>
    public const string StagingQa = "staging-qa";

    /// <summary>Staging → production promotions of those builds, and how many are approved.</summary>
    public const string ProductionPromotions = "production-promotions";

    /// <summary>Production deploys of the line.</summary>
    public const string ProductionDeploys = "production-deploys";

    /// <summary>Schema and data migrations of the deployed versions in production.</summary>
    public const string ProductionMigrations = "production-migrations";

    public static readonly string[] All = [StagingBuilds, StagingQa, ProductionPromotions, ProductionDeploys, ProductionMigrations];

    /// <summary>
    /// The check a template step carries, by its key — for steps of releases opened before the step had
    /// one, which store none.
    /// </summary>
    public static string? ForStepKey(string key) => key switch
    {
        "staging-builds" => StagingBuilds,
        "staging-testing" => StagingQa,
        "production-promotions" => ProductionPromotions,
        "production-deploys" => ProductionDeploys,
        "production-migrations" => ProductionMigrations,
        _ => null,
    };
}
