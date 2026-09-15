using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.Diagnostics;
using Platform.Api.Features.Guides;
using Platform.Api.Features.Knowledge;

namespace Platform.Api.Tests.Features.Diagnostics;

/// <summary>
/// Guards the two authored corpora the assistant reasons from.
///
/// The load-bearing test is <see cref="Every_Playbook_Flag_Is_One_The_Service_Can_Emit"/>. A cause
/// keyed on a flag nothing produces is dead weight — it would simply never be offered, with no
/// error anywhere and no sign in the answer that a case is missing. Exactly the failure mode the
/// guide anchors have, and it needs the same build-time check.
/// </summary>
public class DiagnosticsCorpusTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InfraPilot.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root");
        }
    }

    private static IConfiguration ConfigFor(string key, string dir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = Path.Combine(RepoRoot, dir) })
            .Build();

    private static readonly Lazy<PlaybookRegistry> Playbooks = new(() =>
        new PlaybookRegistry(ConfigFor("Playbooks:Path", "playbooks"), NullLogger<PlaybookRegistry>.Instance));

    private static readonly Lazy<KnowledgeRegistry> Knowledge = new(() =>
        new KnowledgeRegistry(ConfigFor("Knowledge:Path", "knowledge"), NullLogger<KnowledgeRegistry>.Instance));

    private static readonly Lazy<GuideRegistry> Guides = new(() =>
        new GuideRegistry(new GuideYamlLoader(
            ConfigFor("Guides:Path", "guides"), NullLogger<GuideYamlLoader>.Instance)));

    // ── Playbooks ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_Symptom_Has_A_Playbook()
    {
        string[] symptoms =
        [
            Symptoms.PromotionStuckApproved,
            Symptoms.PromotionStuckPending,
            Symptoms.DeploymentFailed,
        ];

        var missing = symptoms.Where(s => Playbooks.Value.ForSymptom(s) is null).ToList();

        Assert.True(missing.Count == 0,
            $"The diagnostics can produce these symptoms but nothing explains them: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_Playbook_Flag_Is_One_The_Service_Can_Emit()
    {
        var unknown = Playbooks.Value.All
            .SelectMany(p => p.Causes.SelectMany(c =>
                c.MatchesWhen.Concat(c.Unless).Select(flag => (Playbook: p.Id, Cause: c.Id, Flag: flag))))
            .Where(x => !Observations.All.Contains(x.Flag))
            .Select(x => $"{x.Playbook}/{x.Cause} → '{x.Flag}'")
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0,
            "Playbook causes are keyed on observation flags DiagnosticsService never emits, so they can "
            + $"never be offered. Add the flag to Observations and emit it, or fix the spelling: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void Every_Emitted_Flag_Is_Used_By_Some_Cause()
    {
        var used = Playbooks.Value.All
            .SelectMany(p => p.Causes.SelectMany(c => c.MatchesWhen.Concat(c.Unless)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Not every flag has to drive a cause — some are pure context for the model to cite — but an
        // unused one is worth noticing, because it usually means a case was observed and never written up.
        var unused = Observations.All.Where(f => !used.Contains(f)).ToList();

        Assert.True(unused.Count <= 6,
            $"Too many observation flags drive no playbook cause; write them up or remove them: {string.Join(", ", unused)}");
    }

    [Fact]
    public void Playbook_Causes_Are_Complete_And_Uniquely_Identified()
    {
        foreach (var playbook in Playbooks.Value.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(playbook.Symptom), $"{playbook.Id}: no symptom");
            Assert.False(string.IsNullOrWhiteSpace(playbook.Summary), $"{playbook.Id}: no summary");

            var duplicates = playbook.Causes.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0, $"{playbook.Id}: duplicate cause ids {string.Join(", ", duplicates)}");

            foreach (var cause in playbook.Causes)
            {
                Assert.False(string.IsNullOrWhiteSpace(cause.Id), $"{playbook.Id}: a cause has no id");
                Assert.False(string.IsNullOrWhiteSpace(cause.Title), $"{playbook.Id}/{cause.Id}: no title");
                Assert.False(string.IsNullOrWhiteSpace(cause.Explanation), $"{playbook.Id}/{cause.Id}: no explanation");
            }
        }
    }

    [Fact]
    public void Playbook_FixGuides_All_Exist()
    {
        var missing = Playbooks.Value.All
            .SelectMany(p => p.Causes
                .Where(c => !string.IsNullOrWhiteSpace(c.FixGuide))
                .Select(c => (Playbook: p.Id, Cause: c.Id, Guide: c.FixGuide!)))
            .Where(x => Guides.Value.GetById(x.Guide) is null)
            .Select(x => $"{x.Playbook}/{x.Cause} → '{x.Guide}'")
            .ToList();

        Assert.True(missing.Count == 0, $"Playbook causes point at guides that do not exist: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// A cause that requires no flags is the "if none of the above" tail. More than a couple per
    /// playbook and the diagnosis stops discriminating — it just lists everything.
    /// </summary>
    [Fact]
    public void Playbooks_Are_Mostly_Evidence_Driven()
    {
        foreach (var playbook in Playbooks.Value.All)
        {
            var unconditional = playbook.Causes.Count(c => c.MatchesWhen.Count == 0);
            Assert.True(unconditional <= 2,
                $"{playbook.Id}: {unconditional} causes match unconditionally — the diagnosis would not discriminate.");
        }
    }

    // ── Knowledge ───────────────────────────────────────────────────────────────

    [Fact]
    public void Knowledge_Corpus_Is_Not_Empty() => Assert.NotEmpty(Knowledge.Value.All);

    [Fact]
    public void Every_Topic_Is_Complete_And_Attributed()
    {
        foreach (var topic in Knowledge.Value.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Title), $"{topic.Id}: no title");
            Assert.False(string.IsNullOrWhiteSpace(topic.Summary), $"{topic.Id}: no summary");
            Assert.False(string.IsNullOrWhiteSpace(topic.Body), $"{topic.Id}: no body");

            // These topics describe systems this repository does not own, so an unattributed or
            // undated claim cannot be checked when a pipeline changes underneath it.
            Assert.False(string.IsNullOrWhiteSpace(topic.Source), $"{topic.Id}: no source");
            Assert.False(string.IsNullOrWhiteSpace(topic.AsOf), $"{topic.Id}: no as_of date");
            Assert.True(DateOnly.TryParse(topic.AsOf, out _), $"{topic.Id}: as_of '{topic.AsOf}' is not a date");
        }
    }

    [Fact]
    public void Topic_Ids_Are_Unique()
    {
        var duplicates = Knowledge.Value.All
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate topic ids: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Topic_Cross_References_All_Resolve()
    {
        var missingTopics = Knowledge.Value.All
            .SelectMany(t => t.Related.Select(r => (t.Id, Ref: r)))
            .Where(x => Knowledge.Value.GetById(x.Ref) is null)
            .Select(x => $"{x.Id} → topic '{x.Ref}'");

        var missingGuides = Knowledge.Value.All
            .SelectMany(t => t.RelatedGuides.Select(r => (t.Id, Ref: r)))
            .Where(x => Guides.Value.GetById(x.Ref) is null)
            .Select(x => $"{x.Id} → guide '{x.Ref}'");

        var missing = missingTopics.Concat(missingGuides).ToList();
        Assert.True(missing.Count == 0, $"Knowledge cross-references do not resolve: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The corpus is only useful if a plainly-worded question reaches the right topic. These are the
    /// questions this work was commissioned to answer.
    /// </summary>
    [Theory]
    [InlineData("what does the promotion approved webhook do", "webhook-catalogue")]
    [InlineData("which webhook triggers the deployment", "webhook-catalogue")]
    [InlineData("why does prod need work item sign off", "promotion-policies")]
    [InlineData("who approves a promotion to production", "promotion-policies")]
    [InlineData("how does a change get to production", "delivery-tracks")]
    [InlineData("what service is deployed where", "service-topology")]
    [InlineData("why can't I promote test to stable", "environments-and-tracks")]
    [InlineData("what does release-track source mean", "deploy-event-sources")]
    [InlineData("how does reconcile work", "reconcile-and-rollback")]
    [InlineData("why was my build rejected source_deploy_missing", "build-registry")]
    public void Question_Finds_The_Right_Topic(string question, string expectedTopicId)
    {
        var matches = Knowledge.Value.Search(question);

        Assert.True(matches.Count > 0, $"'{question}' matched no topic at all");
        Assert.True(
            matches.Take(2).Any(m => m.Item.Id == expectedTopicId),
            $"'{question}' should surface '{expectedTopicId}' in the top two; got: "
            + string.Join(", ", matches.Select(m => $"{m.Item.Id}({m.Score})")));
    }
}
