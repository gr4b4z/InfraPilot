using Platform.Api.Features.Releases;
using Platform.Api.Features.Releases.Models;

namespace Platform.Api.Tests.Features.Releases;

public class ReleaseVersionLineTests
{
    [Theory]
    [InlineData("5.10", true)]
    [InlineData("5.10.0", true)]
    [InlineData("5.10.27-g425981a8", true)]
    [InlineData("v5.10.3", true)]
    [InlineData("5.10-rc1", true)]
    [InlineData("5.100.1", false)]
    [InlineData("5.1.246-gd6a6cfef", false)]
    [InlineData("15.10.1", false)]
    [InlineData("5.9.31-gf167cabc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnLine_MatchesTheLineUpToItsSeparator(string? version, bool expected) =>
        Assert.Equal(expected, ReleaseProgressService.OnLine(version, "5.10"));

    [Theory]
    [InlineData("5.10", "5.10")]
    [InlineData(" v6.0 ", "6.0")]
    [InlineData("5.10.1", null)]
    [InlineData("5", null)]
    [InlineData("five.ten", null)]
    public void NormalizeVersion_AcceptsMajorMinorOnly(string input, string? expected) =>
        Assert.Equal(expected, ReleaseService.NormalizeVersion(input));

    [Theory]
    [InlineData("https://github.com/acme/release/actions/runs/431/job/9", "https://github.com/acme/release/actions/runs/431")]
    [InlineData("https://github.com/acme/release/actions/runs/431", "https://github.com/acme/release/actions/runs/431")]
    [InlineData("https://acme.visualstudio.com/MPT/_build/results?buildId=8971132&view=logs&j=abc",
        "https://acme.visualstudio.com/MPT/_build/results?buildId=8971132")]
    [InlineData("https://ci.example.com/run/7", "https://ci.example.com/run/7")]
    public void WholeRun_FoldsJobLinksIntoTheirRun(string url, string expected) =>
        Assert.Equal(expected, ReleaseProgressService.WholeRun(url));

    [Theory]
    [InlineData("azure-devops", "8971132", "Marketplace 5.10.129 (Diff)", "marketplace-sdp-prod",
        "https://acme.visualstudio.com/MPT/_build/results?buildId=8971132", "marketplace-sdp-prod 8971132")]
    [InlineData("github-actions", "43191998647", "75", "Reconcile Prod",
        "https://github.com/acme/mpt-release/actions/runs/43191998647", "Reconcile Prod #75")]
    [InlineData(null, null, null, null, "https://github.com/acme/mpt-release/actions/runs/43209283187", "mpt-release run 43209283187")]
    [InlineData(null, null, null, null, "https://acme.visualstudio.com/MPT/_build/results?buildId=8971313", "Azure DevOps run 8971313")]
    public void RunLabel_NamesRunsTheWayPeopleQuoteThem(
        string? provider, string? runId, string? runNumber, string? workflow, string url, string expected) =>
        Assert.Equal(expected, ReleaseProgressService.RunLabel(
            new Platform.Api.Features.Deployments.Models.DeployRun(provider, runId, runNumber, WorkflowName: workflow, RunUrl: url), url));

    [Fact]
    public void DeriveStatus_FollowsDatesThenSteps()
    {
        var now = DateTimeOffset.UtcNow;
        var release = new Release { Steps = [new ReleaseStep { Status = ReleaseStepStatuses.Pending }] };
        Assert.Equal(ReleaseStatuses.Planned, ReleaseService.DeriveStatus(release, now));

        // A cut date still ahead keeps it planned; a skipped step alone doesn't start it.
        release.StartedAt = now.AddDays(3);
        release.Steps[0].Status = ReleaseStepStatuses.Skipped;
        Assert.Equal(ReleaseStatuses.Planned, ReleaseService.DeriveStatus(release, now));

        release.Steps[0].Status = ReleaseStepStatuses.InProgress;
        Assert.Equal(ReleaseStatuses.InProgress, ReleaseService.DeriveStatus(release, now));

        release.CompletedAt = now;
        Assert.Equal(ReleaseStatuses.Completed, ReleaseService.DeriveStatus(release, now));

        release.CancelledAt = now;
        Assert.Equal(ReleaseStatuses.Cancelled, ReleaseService.DeriveStatus(release, now));
    }
}
