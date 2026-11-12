using Platform.Api.Features.Releases.Models;

namespace Platform.Api.Features.Releases;

/// <summary>
/// The checklist a new release starts with — the steps the 5.10 release actually went through
/// (see <c>notes/releases-api.md</c>): plan it, cut the line, build and freeze it on staging, test
/// it, call go/no-go, approve the production promotions in one sitting, watch the deploys and
/// migrations land, and have QA sanity test production. Every release gets its own copy, so
/// changing this list never rewrites a release that already happened; the release manager adds or
/// skips steps on the release itself.
/// </summary>
public static class ReleaseStepTemplate
{
    public sealed record StepDefinition(string Key, string Title, string Description, string Owner, string? Check);

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
            $"Build and deploy {version} to staging",
            $"Each service is rebuilt from its release branch and promoted to staging. The portal counts the services running a {version} build there.",
            "Developers", ReleaseChecks.StagingBuilds),
        new("code-freeze",
            "Code freeze",
            $"From here on only fixes for the release go into the release branches, each as a new {version}.x build that replaces the service's pending production promotion.",
            "Release manager", null),
        new("staging-testing",
            "Test on staging",
            "QA runs the regression and acceptance tests on staging and gives the green light for production.",
            "QA", null),
        new("go-no-go",
            "Go / no-go",
            "Confirm the scope, whether production needs downtime, and who runs the deployment.",
            "Release manager", null),
        new("production-promotions",
            "Approve the production promotions",
            "On release day, approve every service's promotion to production, usually in one go with Mass approve on the Promotions page.",
            "Release manager", ReleaseChecks.ProductionPromotions),
        new("production-deploys",
            "Deploy to production",
            "The release pipelines deploy the approved promotions. The portal follows each service's production deployment and flags the ones that failed.",
            "DevOps", ReleaseChecks.ProductionDeploys),
        new("production-migrations",
            "Run schema and data migrations",
            "Schema migrations run inside each deployment and data migrations after it. The portal checks that every migration of the deployed versions finished.",
            "DevOps", ReleaseChecks.ProductionMigrations),
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
