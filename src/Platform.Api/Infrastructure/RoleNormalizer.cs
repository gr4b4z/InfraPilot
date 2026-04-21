using System.Text.RegularExpressions;

namespace Platform.Api.Infrastructure;

/// <summary>
/// Canonicalises participant role strings so variants like "Triggered By", "triggeredBy",
/// "triggered_by", and "TRIGGERED-BY" all collapse to the same key <c>triggered-by</c>.
/// <para>
/// The canonical form is lower-kebab-case: ASCII lowercase letters + digits, separated by
/// single hyphens. Downstream subscribers (Jira mapping tables, Slack channels) can safely
/// key off this value without worrying about sender casing drift.
/// </para>
/// <para>
/// Display strings are carried on a separate <c>label</c> field on the participant so the
/// UI can show human-friendly text. When no label is provided, a humaniser (in the web app)
/// derives one from the canonical key.
/// </para>
/// </summary>
public static class RoleNormalizer
{
    private static readonly Regex CamelBoundary = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[\s_]+", RegexOptions.Compiled);
    private static readonly Regex NonAllowed = new(@"[^a-z0-9-]", RegexOptions.Compiled);
    private static readonly Regex DuplicateHyphens = new(@"-+", RegexOptions.Compiled);

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = input.Trim();
        // camelCase / PascalCase boundary: "TriggeredBy" -> "Triggered-By"
        s = CamelBoundary.Replace(s, "-");
        s = s.ToLowerInvariant();
        // whitespace + underscores -> hyphen
        s = Separators.Replace(s, "-");
        // strip anything else (dots, slashes, punctuation)
        s = NonAllowed.Replace(s, "-");
        // collapse duplicate hyphens, trim leading/trailing
        s = DuplicateHyphens.Replace(s, "-").Trim('-');
        return s;
    }
}
