using Platform.Api.Infrastructure.Content;

namespace Platform.Api.Features.Diagnostics;

/// <summary>A cause whose observation flags all hold, with how specifically it matched.</summary>
/// <param name="Cause">The authored cause.</param>
/// <param name="Specificity">
/// How many flags it required. A cause demanding three conditions that all hold is a better
/// explanation than one demanding none, so this orders the answer.
/// </param>
public record MatchedCause(PlaybookCause Cause, int Specificity);

/// <summary>Loaded failure playbooks, indexed by symptom.</summary>
public class PlaybookRegistry
{
    private readonly List<Playbook> _playbooks;

    public PlaybookRegistry(IConfiguration config, ILogger<PlaybookRegistry> logger)
    {
        var path = YamlCorpusLoader.ResolvePath(config, "PLAYBOOKS_PATH", "Playbooks:Path", "playbooks");
        _playbooks = new YamlCorpusLoader(logger).Load<Playbook>(
            path,
            "failure playbook",
            p => p.Id,
            p => p.Causes.Count == 0 ? "no causes" : null);
    }

    public IReadOnlyList<Playbook> All => _playbooks;

    public Playbook? ForSymptom(string symptom) =>
        _playbooks.FirstOrDefault(p => string.Equals(p.Symptom, symptom, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Selects the causes whose flags are satisfied by <paramref name="observations"/>, most
    /// specific first. Causes requiring no flags are always included, last — they are the "if none
    /// of the above" tail, and the assistant is told to present them as such.
    /// </summary>
    public List<MatchedCause> Match(string symptom, IReadOnlySet<string> observations)
    {
        var playbook = ForSymptom(symptom);
        if (playbook is null) return [];

        return [.. playbook.Causes
            .Where(c => c.MatchesWhen.All(observations.Contains)
                     && !c.Unless.Any(observations.Contains))
            .Select(c => new MatchedCause(c, c.MatchesWhen.Count))
            .OrderByDescending(m => m.Specificity)
            .ThenBy(m => m.Cause.Title, StringComparer.OrdinalIgnoreCase)];
    }
}
