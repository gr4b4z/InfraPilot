using System.Text;

namespace Platform.Api.Features.Migrations;

/// <summary>
/// Fuzzy matching of a typed query against migration names, for "has this migration run on staging?"
/// People rarely have the id to hand: they remember part of it ("AccountName"), its words in some
/// order ("account name"), an abbreviation ("altacc") or a misspelling ("AlterAcountName"). Each of
/// those matches, and the score ranks how literal the match was so the exact one comes first.
///
/// <para>Names are compared normalised — lower case, punctuation dropped — and split into words on
/// case changes, digits and separators, so <c>4650_AlterAccountName</c>, <c>alter-account-name</c> and
/// <c>AlterAccountName</c> read the same.</para>
/// </summary>
public static class MigrationNameMatcher
{
    /// <summary>Scores below this are not a match.</summary>
    public const int Threshold = 30;

    /// <summary>
    /// How well <paramref name="name"/> matches <paramref name="query"/>, 0–100; below
    /// <see cref="Threshold"/> is no match.
    /// </summary>
    public static int Score(string query, string name)
    {
        var q = Normalize(query);
        var n = Normalize(name);
        if (q.Length == 0 || n.Length == 0) return 0;

        if (n == q) return 100;
        if (n.StartsWith(q, StringComparison.Ordinal)) return 92;
        if (n.Contains(q, StringComparison.Ordinal)) return 85 - Math.Min(10, (n.Length - q.Length) / 4);

        // Words in any order, each word of the query a prefix of some word of the name.
        var queryWords = Words(query);
        var nameWords = Words(name);
        if (queryWords.Count > 1 && queryWords.All(qw => nameWords.Any(nw => nw.StartsWith(qw, StringComparison.Ordinal))))
            return 75;

        // Typos: the closest stretch of the name to the query, by edit distance. One mistake per four
        // characters, so a short query has to be nearly right.
        if (q.Length >= 4)
        {
            var distance = BestWindowDistance(q, n);
            var allowed = Math.Max(1, q.Length / 4);
            if (distance <= allowed) return 65 - distance * 8;
        }

        // Abbreviations: the query's letters appear in order, starting at a word boundary.
        if (q.Length >= 3 && IsWordAnchoredSubsequence(q, nameWords)) return 45;

        // Words in any order with typos in them.
        if (queryWords.Count > 1 && queryWords.All(qw => qw.Length < 4
                ? nameWords.Any(nw => nw.StartsWith(qw, StringComparison.Ordinal))
                : nameWords.Any(nw => BestWindowDistance(qw, nw) <= Math.Max(1, qw.Length / 4))))
            return 40;

        return 0;
    }

    /// <summary>Lower case, letters and digits only.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Words of a name: split on separators, case changes and letter/digit boundaries.</summary>
    public static List<string> Words(string s)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString().ToLowerInvariant());
            current.Clear();
        }
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (current.Length > 0)
            {
                var prev = s[i - 1];
                var lowerToUpper = char.IsLower(prev) && char.IsUpper(c);
                // "SQLTable" → "sql", "table": an upper letter followed by a lower one starts a word.
                var acronymEnd = char.IsUpper(prev) && char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                var digitEdge = char.IsDigit(prev) != char.IsDigit(c);
                if (lowerToUpper || acronymEnd || digitEdge) Flush();
            }
            current.Append(c);
        }
        Flush();
        return words;
    }

    /// <summary>
    /// The smallest edit distance between <paramref name="q"/> and any substring of <paramref name="n"/>
    /// (Sellers' approximate substring matching), so a typo inside a long name still counts. Swapped
    /// neighbours count as one edit (optimal string alignment) — "nmae" is one slip, not two.
    /// </summary>
    internal static int BestWindowDistance(string q, string n)
    {
        var before = new int[n.Length + 1];
        var prev = new int[n.Length + 1];
        var cur = new int[n.Length + 1];
        // Row 0 is all zeros: the match may start anywhere in the name.
        for (var i = 1; i <= q.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= n.Length; j++)
            {
                var cost = q[i - 1] == n[j - 1] ? 0 : 1;
                var d = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && q[i - 1] == n[j - 2] && q[i - 2] == n[j - 1])
                    d = Math.Min(d, before[j - 2] + 1);
                cur[j] = d;
            }
            (before, prev, cur) = (prev, cur, before);
        }
        var best = int.MaxValue;
        for (var j = 0; j <= n.Length; j++) best = Math.Min(best, prev[j]);
        return best;
    }

    private static bool IsWordAnchoredSubsequence(string q, List<string> nameWords)
    {
        if (nameWords.Count == 0 || nameWords[0][0] != q[0] && !nameWords.Any(w => w[0] == q[0])) return false;
        var joined = string.Concat(nameWords);
        // Start at the first word that begins with the query's first letter.
        var starts = new List<int>();
        var offset = 0;
        foreach (var w in nameWords)
        {
            if (w[0] == q[0]) starts.Add(offset);
            offset += w.Length;
        }
        foreach (var start in starts)
        {
            var qi = 0;
            for (var j = start; j < joined.Length && qi < q.Length; j++)
            {
                if (joined[j] == q[qi]) qi++;
            }
            if (qi == q.Length) return true;
        }
        return false;
    }
}
