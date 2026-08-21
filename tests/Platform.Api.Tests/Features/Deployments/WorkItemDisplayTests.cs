using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Tests.Features.Deployments;

public class WorkItemDisplayTests
{
    [Fact]
    public void TrackerSummaryIsTheTitle_EveryCommitMessageIsTheSubTitle()
    {
        // The two-line producer shape: commit subject on Title, Jira summary on SubTitle.
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "fix: send an idempotency key with the retry",
            SubTitle: "Fix retry",
            Commits: ["aaaaaaa1111", "bbbbbbb2222"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "aaaaaaa1111", Title: "fix: send an idempotency key with the retry"),
            new("commit", Key: "bbbbbbb2222", Title: "test: cover the duplicate submit"),
        };

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.Equal("Fix retry", title);
        Assert.Equal(
            "fix: send an idempotency key with the retry • test: cover the duplicate submit",
            subTitle);
    }

    [Fact]
    public void SingleNamedProducer_KeepsItsTitle_AndStillListsTheCommits()
    {
        // The one-line shape (mpt-release, and marketplace since the flip): Title is already the
        // tracker's summary, with the commit hashes alongside it.
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Fix retry",
            Commits: ["aaaaaaa1111", "bbbbbbb2222"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "aaaaaaa1111", Title: "fix: send an idempotency key with the retry"),
            new("commit", Key: "bbbbbbb2222", Title: "test: cover the duplicate submit"),
        };

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.Equal("Fix retry", title);
        Assert.Equal(
            "fix: send an idempotency key with the retry • test: cover the duplicate submit",
            subTitle);
    }

    [Fact]
    public void SquashMergeBookkeepingIsStrippedFromTheSubTitle()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Reject duplicate offboarding requests",
            Commits: ["aaaaaaa1111", "bbbbbbb2222"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "aaaaaaa1111", Title: "Merged PR 150156: reject the second request"),
            new("commit", Key: "bbbbbbb2222", Title: "merged pr 7: cover the second request"),
        };

        var (_, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.Equal("reject the second request • cover the second request", subTitle);
    }

    [Fact]
    public void CommitsAreDedupedOnHashAndOnMessage_AndFollowDeclaredOrder()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Fix retry",
            // Same hash twice, plus a squash whose subject repeats the earlier commit's.
            Commits: ["bbbbbbb2222", "aaaaaaa1111", "aaaaaaa1111", "ccccccc3333"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "aaaaaaa1111", Title: "first"),
            new("commit", Key: "bbbbbbb2222", Title: "second"),
            new("commit", Key: "ccccccc3333", Title: "first"),
        };

        var (_, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.Equal("second • first", subTitle);
    }

    [Fact]
    public void AbbreviatedHashOnEitherSideStillMatchesItsCommit()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Fix retry", Commits: ["abc1234"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "abc1234def5678", Title: "fix: the retry"),
        };

        var (_, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.Equal("fix: the retry", subTitle);
    }

    [Fact]
    public void SubTitleThatRepeatsTheTitleIsDropped()
    {
        // One commit, named the same thing as the ticket — a second line saying it again is noise.
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Fix retry", Commits: ["aaaaaaa1111"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "aaaaaaa1111", Title: "Fix retry"),
        };

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.Equal("Fix retry", title);
        Assert.Null(subTitle);
    }

    [Fact]
    public void TrimmedPayload_FallsBackToTheProducersCommitSubject()
    {
        // The `commit` references were dropped (workflow_dispatch input caps), so there is nothing to
        // hydrate — the commit subject the producer put on Title is the only message left.
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Merged PR 150156: send an idempotency key",
            SubTitle: "Fix retry",
            Commits: ["aaaaaaa1111"]);

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, [workItem]);

        Assert.Equal("Fix retry", title);
        Assert.Equal("send an idempotency key", subTitle);
    }

    [Fact]
    public void NoCommitsAtAll_LeavesASingleLine()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Title: "Fix retry");

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, [workItem]);

        Assert.Equal("Fix retry", title);
        Assert.Null(subTitle);
    }

    [Fact]
    public void EnrichmentLabelNamesAnOtherwiseUnnamedItem()
    {
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Commits: ["aaaaaaa1111"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "aaaaaaa1111", Title: "fix: the retry"),
        };

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, refs, trackerTitleFallback: "Fix retry");

        Assert.Equal("Fix retry", title);
        Assert.Equal("fix: the retry", subTitle);
    }

    [Fact]
    public void LongCommitListIsTruncatedToTheColumnWidth()
    {
        var hashes = Enumerable.Range(0, 40).Select(i => $"hash{i:D8}").ToList();
        var workItem = new ReferenceDto("work-item", Key: "FOO-1", Title: "Fix retry", Commits: hashes);
        var refs = new List<ReferenceDto> { workItem };
        refs.AddRange(hashes.Select((h, i) =>
            new ReferenceDto("commit", Key: h, Title: $"fix: the {i}th thing that had to change")));

        var (_, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.NotNull(subTitle);
        Assert.Equal(WorkItemDisplay.MaxSubTitleLength, subTitle!.Length);
        Assert.EndsWith("…", subTitle);
    }

    [Fact]
    public void ResolvingIsIdempotent_WhenTheReadPathsOwnOutputComesBackAsSubTitle()
    {
        // What a replay posts back: the tracker name on Title and this resolver's own commit line on
        // SubTitle, because that is how the API reported the candidate (mpt-release's refresh echoes a
        // candidate's work-item references when its own rebuild resolves none). Reading the line as a
        // ticket name would rename the ticket to its list of commits.
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Fix retry",
            SubTitle: "fix: send an idempotency key with the retry • test: cover the duplicate submit",
            Commits: ["aaaaaaa1111", "bbbbbbb2222"]);
        var refs = new List<ReferenceDto>
        {
            workItem,
            new("commit", Key: "aaaaaaa1111", Title: "fix: send an idempotency key with the retry"),
            new("commit", Key: "bbbbbbb2222", Title: "test: cover the duplicate submit"),
        };

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, refs);

        Assert.Equal("Fix retry", title);
        Assert.Equal(
            "fix: send an idempotency key with the retry • test: cover the duplicate submit",
            subTitle);
    }

    [Fact]
    public void EchoedCommitLineSurvives_WhenItsCommitReferencesAreNoLongerThere()
    {
        // Same replay, with the `commit` references trimmed out: there is nothing to recompute the
        // line from, and the echoed line is the only record of it left.
        var workItem = new ReferenceDto("work-item", Key: "FOO-1",
            Title: "Fix retry",
            SubTitle: "fix: send an idempotency key with the retry • test: cover the duplicate submit",
            Commits: ["aaaaaaa1111", "bbbbbbb2222"]);

        var (title, subTitle) = WorkItemDisplay.Resolve(workItem, [workItem]);

        Assert.Equal("Fix retry", title);
        Assert.Equal(
            "fix: send an idempotency key with the retry • test: cover the duplicate submit",
            subTitle);
    }

    [Fact]
    public void ApplyToReferences_RewritesWorkItemsAndLeavesEverythingElseAlone()
    {
        var refs = new List<ReferenceDto>
        {
            new("work-item", Key: "FOO-1",
                Title: "Merged PR 150156: send an idempotency key",
                SubTitle: "Fix retry",
                Commits: ["aaaaaaa1111"]),
            new("commit", Key: "aaaaaaa1111", Title: "Merged PR 150156: send an idempotency key"),
            new("pull-request", Key: "150156", Title: "Fix retry", Revision: "aaaaaaa1111"),
        };

        var applied = WorkItemDisplay.ApplyToReferences(refs);

        var workItem = applied.Single(r => r.Type == "work-item");
        Assert.Equal("Fix retry", workItem.Title);
        Assert.Equal("send an idempotency key", workItem.SubTitle);

        // A commit's title is the commit's own subject — verbatim, prefix included.
        Assert.Equal("Merged PR 150156: send an idempotency key",
            applied.Single(r => r.Type == "commit").Title);
        Assert.Equal("Fix retry", applied.Single(r => r.Type == "pull-request").Title);
    }
}
