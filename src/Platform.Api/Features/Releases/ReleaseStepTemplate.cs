using Platform.Api.Features.Releases.Models;

namespace Platform.Api.Features.Releases;

/// <summary>
/// The checklist a new release starts with — the steps the 5.10 release actually went through
/// (see <c>notes/releases-api.md</c>): plan it, cut the line, build and freeze it on staging, test
/// it, call go/no-go, approve the production promotions in one sitting, watch the deploys and
/// migrations land, and have QA sanity test production. Every release gets its own copy, so
/// changing this list never rewrites a release that already happened; the release manager adds or
/// skips steps on the release itself.
///
/// Optional steps — the maintenance page around the production deploy — are only for a release that
/// needs downtime. They are skipped when it doesn't, and don't hold the release while nobody has
/// started them (see <see cref="IsOptional"/>).
/// </summary>
public static class ReleaseStepTemplate
{
    public sealed record StepDefinition(
        string Key, string Title, string Description, string Owner, string? Check, bool Optional = false);

    public const string MaintenanceOn = "maintenance-on";
    public const string MaintenanceOff = "maintenance-off";

    private static readonly HashSet<string> OptionalKeys =
        Default("0.0").Where(d => d.Optional).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a standard step with this key is optional. Steps a release manager adds never are.</summary>
    public static bool IsOptional(string key) => OptionalKeys.Contains(key);

    public static IReadOnlyList<StepDefinition> Default(string version) =>
    [
        new("plan",
            "Plan the scope and release date",
            "Agree what goes into the release and when it reaches production, and set up the release version in the issue tracker.",
            "Release manager", null),
        new("version-cut",
            $"Start the {version} release line",
            $"Tag the release branches with {version}.0, so every build from them comes out as {version}.x.",
            "Release manager", null),
        new("staging-builds",
            "Build and Staging",
            $"Each service is built from its release branch, deployed to staging and promoted towards production. The portal follows each service's {version} build, staging deploy and production promotion.",
            "Developers", ReleaseChecks.StagingBuilds),
        new("code-freeze",
            "Code freeze",
            $"From here on only fixes for the release go into the release branches, each as a new {version}.x build that replaces the service's pending production promotion.",
            "Release manager", null),
        new("staging-testing",
            "Test on staging",
            "QA tests the release on staging, signs off the work items and approves each production promotion's QA Review gate. Done when every promotion has passed QA Review.",
            "QA", ReleaseChecks.StagingQa),
        new("go-no-go",
            "Go / no-go",
            "Confirm the scope, whether production needs downtime, and who runs the deployment.",
            "Release manager", null),
        new("production-promotions",
            "Approve the production promotions",
            "On release day, approve every service's promotion to production, usually in one go with Mass approve on the Promotions page. Done when every promotion is approved.",
            "Release manager", ReleaseChecks.ProductionPromotions),
        new(MaintenanceOn,
            "Enable maintenance page",
            "If the release needs downtime, switch production to the maintenance page before the deployments start. Skip it when the release goes out without downtime.",
            "DevOps", null, Optional: true),
        new("production-deploys",
            "Deploy to production",
            "The release pipelines deploy the approved promotions. The portal follows each service's production deployment and flags the ones that failed.",
            "DevOps", ReleaseChecks.ProductionDeploys),
        new("production-migrations",
            "Run schema and data migrations",
            "Schema migrations run inside each deployment and data migrations after it. The portal checks that every migration of the deployed versions finished.",
            "DevOps", ReleaseChecks.ProductionMigrations),
        new(MaintenanceOff,
            "Disable maintenance page",
            "Once the deployments and migrations are done, switch the maintenance page off so people can use production again. Skip it if the maintenance page wasn't switched on.",
            "DevOps", null, Optional: true),
        new("production-sanity",
            "Sanity test in production",
            "QA sanity tests production and signs the release off.",
            "QA", null),
    ];

    public static List<ReleaseStep> Build(Guid releaseId, string version, DateTimeOffset now) =>
        Default(version)
            .Select((d, i) => new ReleaseStep
            {
                Id = Guid.NewGuid(),
                ReleaseId = releaseId,
                Order = (i + 1) * 10,
                Key = d.Key,
                Title = d.Title,
                Description = d.Description,
                Owner = d.Owner,
                Check = d.Check,
                Status = ReleaseStepStatuses.Pending,
                UpdatedAt = now,
            })
            .ToList();
}
