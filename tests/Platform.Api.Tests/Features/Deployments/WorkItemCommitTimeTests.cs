using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Tests.Features.Deployments;

public class WorkItemCommitTimeTests
{
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-08-05T10:00:00Z");
    private static readonly DateTimeOffset T3 = DateTimeOffset.Parse("2026-08-09T10:00:00Z");

    [Fact]
    public void DeclaredCommits_PullRequestTimestampWins()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Commits: ["abc123"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "abc123", OccurredAt: T1),
            new("pull-request", Key: "42", Revision: "abc123", OccurredAt: T2),
        };

        // PR merge time is the clock start even when the commit ref also carries a timestamp.
        Assert.Equal(T2, WorkItemCommitTime.Resolve(workItem, refs));
    }

    [Fact]
    public void DeclaredCommits_MinOverSeveralPullRequests()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Commits: ["aaa", "bbb"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("pull-request", Key: "1", Revision: "aaa", OccurredAt: T3),
            new("pull-request", Key: "2", Revision: "bbb", OccurredAt: T1),
        };

        Assert.Equal(T1, WorkItemCommitTime.Resolve(workItem, refs));
    }

    [Fact]
    public void DeclaredCommits_FallsBackToCommitRefs()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Commits: ["abc123"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "abc123", OccurredAt: T1),
            // PR exists but matches a different revision — must not be used.
            new("pull-request", Key: "42", Revision: "zzz999", OccurredAt: T2),
        };

        Assert.Equal(T1, WorkItemCommitTime.Resolve(workItem, refs));
    }

    [Fact]
    public void NoDeclaredCommits_SinglePullRequestAttributed()
    {
        // The real producer shape: one squashed PR per deploy, no commits[] on the ticket.
        var workItem = new ReferenceDto("work-item", Key: "FOO-1");
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("pull-request", Key: "42", OccurredAt: T2),
            new("commit", Key: "abc123", OccurredAt: T1),
        };

        Assert.Equal(T2, WorkItemCommitTime.Resolve(workItem, refs));
    }

    [Fact]
    public void NoDeclaredCommits_SingleCommitFallback()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1");
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "abc123", OccurredAt: T1),
        };

        Assert.Equal(T1, WorkItemCommitTime.Resolve(workItem, refs));
    }

    [Fact]
    public void NoDeclaredCommits_MultiplePullRequests_NoGuess()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1");
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("pull-request", Key: "1", OccurredAt: T1),
            new("pull-request", Key: "2", OccurredAt: T2),
        };

        // Ambiguous attribution — null beats a wrong number.
        Assert.Null(WorkItemCommitTime.Resolve(workItem, refs));
    }

    [Fact]
    public void NoTimestampsAnywhere_ReturnsNull()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Commits: ["abc123"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("pull-request", Key: "42", Revision: "abc123"),
            new("commit", Key: "abc123"),
        };

        Assert.Null(WorkItemCommitTime.Resolve(workItem, refs));
    }

    [Fact]
    public void CommitMatching_IsCaseInsensitive()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Commits: ["ABC123"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "abc123", OccurredAt: T1),
        };

        Assert.Equal(T1, WorkItemCommitTime.Resolve(workItem, refs));
    }
}
