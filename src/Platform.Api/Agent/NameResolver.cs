namespace Platform.Api.Agent;

public enum NameMatchKind
{
    /// <summary>Nothing resembles what was asked for.</summary>
    None,
    /// <summary>The name as given, modulo case and separators.</summary>
    Exact,
    /// <summary>A misspelling of a known name — the user should be told what was assumed.</summary>
    Corrected,
    /// <summary>A fragment that several (or one) known names contain — "adobe" for every adobe service.</summary>
    Partial,
}

/// <summary>
/// What a user-typed product or service name resolved to. <see cref="Matches"/> is ordered
/// best-first; it holds one entry for an exact or unambiguous match and several when a fragment
/// or a typo fits more than one name.
/// </summary>
public sealed record NameResolution(string Asked, NameMatchKind Kind, List<string> Matches)
{
    public string? Single => Matches.Count == 1 ? Matches[0] : null;
    public bool Found => Matches.Count > 0;

    /// <summary>
    /// A sentence for the model to pass on, or null when the name was used as typed. Corrections
    /// are worth saying out loud — a silently substituted name is how someone reads the wrong
    /// service's version with full confidence.
    /// </summary>
    public string? Note(string what) => Kind switch
    {
        NameMatchKind.Corrected when Single is not null =>
            $"'{Asked}' is not a known {what}; assumed you meant '{Single}' (likely a typo). Tell the user.",
        NameMatchKind.Corrected =>
            $"'{Asked}' is not a known {what}; it could be any of: {string.Join(", ", Matches)}. Answer for all of them and say so, or ask which.",
        NameMatchKind.Partial when Single is not null =>
            $"'{Asked}' matched the {what} '{Single}'.",
        NameMatchKind.Partial =>
            $"'{Asked}' matches {Matches.Count} {what}s: {string.Join(", ", Matches)}. Answer for all of them and say which is which.",
        _ => null,
    };
}

/// <summary>
/// Resolves the names people type to the names the data uses. A chat is not a form: "mpt-extentions-adobe"
/// means mpt-extension-adobe, "identity platform" means identity-platform, and "adobe" means every
/// service with adobe in its name. Answering "no such service" to any of these is a failure the
/// user has to repair by hand, so this tries, in order: the name as given, a near miss by edit
/// distance, then a fragment contained in one or more names.
/// </summary>
public static class NameResolver
{
    /// <summary>Longest fragment list returned for a partial match; more than this is a search, not an answer.</summary>
    public const int MaxPartialMatches = 6;

    /// <summary>Fragments shorter than this match too much to mean anything.</summary>
    private const int MinFragmentLength = 3;

    public static NameResolution Resolve(string? raw, IReadOnlyCollection<string> candidates)
    {
        var asked = raw?.Trim() ?? "";
        if (asked.Length == 0 || candidates.Count == 0)
            return new NameResolution(asked, NameMatchKind.None, []);

        var wanted = Normalize(asked);

        // 1. As typed — the common case, and the only one that needs no explanation.
        var exact = candidates.Where(c => Normalize(c) == wanted).ToList();
        if (exact.Count > 0)
            return new NameResolution(asked, NameMatchKind.Exact, exact.Take(1).ToList());

        // 2. A near miss. The allowance scales with length: one slip in a short name, a few in a
        //    long one, never so many that unrelated names start to qualify.
        var allowance = Math.Max(1, wanted.Length / 6);
        var nearest = candidates
            .Select(c => (Name: c, Distance: Distance(wanted, Normalize(c))))
            .Where(x => x.Distance <= allowance)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        if (nearest.Count > 0)
        {
            var best = nearest[0].Distance;
            var tied = nearest.Where(x => x.Distance == best).Select(x => x.Name).ToList();
            return new NameResolution(asked, NameMatchKind.Corrected, tied);
        }

        // 3. A fragment. Every token the user typed has to appear (or nearly appear) in the name,
        //    so "adobe" finds the adobe services and "mpt adobe" narrows to those under mpt.
        if (wanted.Length >= MinFragmentLength)
        {
            var tokens = wanted.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var partial = candidates
                .Where(c => ContainsAllTokens(Normalize(c), tokens))
                .OrderBy(c => c.Length)
                .ThenBy(c => c, StringComparer.Ordinal)
                .Take(MaxPartialMatches)
                .ToList();

            if (partial.Count > 0)
                return new NameResolution(asked, NameMatchKind.Partial, partial);
        }

        return new NameResolution(asked, NameMatchKind.None, []);
    }

    /// <summary>Lower-case, hyphen-separated: the shape the data uses, whatever the user typed.</summary>
    public static string Normalize(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => ch is ' ' or '_' or '.' or '/' ? '-' : ch)
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--")) collapsed = collapsed.Replace("--", "-");
        return collapsed.Trim('-');
    }

    private static bool ContainsAllTokens(string name, string[] tokens)
    {
        var nameTokens = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(t =>
            name.Contains(t, StringComparison.Ordinal)
            || nameTokens.Any(nt => t.Length >= 4 && Distance(t, nt) <= 1));
    }

    /// <summary>Damerau-Levenshtein: insert, delete, substitute or swap adjacent characters, each costing one.</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }

        return d[a.Length, b.Length];
    }
}
