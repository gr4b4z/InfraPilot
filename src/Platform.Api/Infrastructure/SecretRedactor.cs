using System.Text.RegularExpressions;

namespace Platform.Api.Infrastructure;

/// <summary>
/// Replaces secrets in captured pipeline output with <c>[redacted]</c> before it is stored. Deploy and
/// migration logs are shown to everyone signed in, and producers capture whatever their tools print:
/// <c>kubectl describe pod</c> prints container environment variables with their values, so a
/// connection string set as a plain env var arrives here with its key in it.
/// <para>
/// Pattern-based, so it catches the common shapes — connection-string keys
/// (<c>SharedAccessKey=</c>, <c>AccountKey=</c>, <c>Password=</c>), <c>key: value</c> / <c>key=value</c>
/// / JSON pairs whose key names a password, secret, token, API key or connection string, bearer and
/// Authorization header values, Kubernetes env entries (<c>- name: DB_PASSWORD</c> / <c>value: …</c>),
/// credentials in URLs, SAS signatures, JWTs, GitHub tokens and PEM private keys — not every secret a
/// tool could print. Producers are still expected not to print them.
/// The name stays and only the value goes, so a reader can still see which setting was involved.
/// </para>
/// <para>
/// Idempotent: a value that is already <c>[redacted]</c> is left alone, so stored logs can be scrubbed
/// again safely. Every pattern is linear in the input (bounded or unambiguous repetition); if one
/// still runs past its timeout the content is withheld rather than stored unchecked.
/// </para>
/// </summary>
public static partial class SecretRedactor
{
    public const string Marker = "[redacted]";

    /// <summary>What is stored instead of a log the patterns could not finish checking in time.</summary>
    public const string Withheld = "[This log was withheld: it could not be checked for secrets in time.]";

    private const int TimeoutMs = 5000;

    // Key words: a key ending in one of these holds a secret. Shared by the three key/value patterns.
    private const string SecretKey =
        "password|passwd|pwd|passphrase|secret|token|apikey|api_key|api-key|accesskey|access_key|accountkey"
        + "|connectionstring|connection_string|sharedaccesssignature|privatekey|private_key";

    // A value: quoted, or a run of non-space characters. Never starts with "[" (already redacted) or
    // "<" (kubectl's "<set to the key 'x' in secret 'y'>" / "<none>", which name a secret, not hold it).
    private const string QuotedValue = "\"[^\"\\r\\n]+\"|'[^'\\r\\n]+'";

    public static string Redact(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content ?? "";
        try
        {
            var s = PrivateKeyBlock().Replace(content, m =>
                $"-----BEGIN {m.Groups["kind"].Value}PRIVATE KEY-----\n{Marker}\n"
                + (m.Groups["end"].Success ? $"-----END {m.Groups["kind"].Value}PRIVATE KEY-----" : ""));
            s = Jwt().Replace(s, Marker);
            s = GitHubToken().Replace(s, Marker);
            s = BearerToken().Replace(s, $"${{scheme}} {Marker}");
            s = AuthorizationHeader().Replace(s, $"${{key}}${{scheme}}{Marker}");
            s = UrlCredentials().Replace(s, $"${{scheme}}${{user}}:{Marker}@");
            s = SasSignature().Replace(s, $"${{key}}{Marker}");
            s = KeyAssignment().Replace(s, ReplaceValue);
            s = KeyAtLineStart().Replace(s, ReplaceValue);
            s = JsonKey().Replace(s, ReplaceValue);
            s = ManifestEnvEntry().Replace(s, ReplaceValue);
            s = JsonEnvEntry().Replace(s, ReplaceValue);
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
    }

    private static string ReplaceValue(Match m)
    {
        var value = m.Groups["value"].Value;
        var quote = value.Length > 1 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[0].ToString() : "";
        return m.Groups["key"].Value + quote + Marker + quote;
    }

    [GeneratedRegex(
        @"-----BEGIN (?<kind>[A-Z0-9 ]{0,40})PRIVATE KEY-----[\s\S]*?(?:(?<end>-----END [A-Z0-9 ]{0,40}PRIVATE KEY-----)|\z)",
        RegexOptions.None, TimeoutMs)]
    private static partial Regex PrivateKeyBlock();

    // header.payload.signature, both JSON parts base64url of "{" — so both start with eyJ.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]*", RegexOptions.None, TimeoutMs)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})", RegexOptions.None, TimeoutMs)]
    private static partial Regex GitHubToken();

    // 16+ characters, so prose such as "Bearer token expired" is left alone.
    [GeneratedRegex(@"\b(?<scheme>Bearer)[ \t]+[A-Za-z0-9\-._~+/]{16,}=*", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex BearerToken();

    // The scheme group is atomic so "Authorization: Bearer [redacted]" can't be re-read as a bare value.
    [GeneratedRegex(
        @"(?<key>\bAuthorization[""']?[ \t]*[:=][ \t]*[""']?)(?<scheme>(?>(?:(?:Bearer|Basic|Token|Digest|Negotiate|NTLM)[ \t]+)?))[^\s""',;\[<][^\s""',;]*",
        RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex AuthorizationHeader();

    // scheme://user:password@host — the scheme is bounded so a long run of letters can't go quadratic.
    [GeneratedRegex(
        @"(?<scheme>\b[a-z][a-z0-9+.\-]{0,20}://)(?<user>[^:/@\s""']{1,256}):[^@/\s""'\[][^@/\s""']{0,1024}@",
        RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex UrlCredentials();

    // Azure SAS (storage URLs, Service Bus "SharedAccessSignature sr=…&sig=…").
    [GeneratedRegex(@"(?<key>[?&]sig=)[^&\s""'\[][^&\s""']*", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex SasSignature();

    // key=value anywhere: connection strings (Password=…;), query strings, CLI flags, env dumps. The
    // value ends at ";" so the rest of a connection string stays readable.
    [GeneratedRegex(
        @"(?<key>(?:" + SecretKey + @")[""']?[ \t]*=[ \t]*)(?<value>" + QuotedValue + @"|[^\s;""'<\[{=][^\s;""']*)",
        RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex KeyAssignment();

    // "key: value" opening a line — YAML, env listings, kubectl describe. Only at line start, so prose
    // like "could not acquire token: AADSTS…" keeps its error code. The value is the rest of the line.
    [GeneratedRegex(
        @"^(?<key>[ \t]*(?:-[ \t]+)?[""']?[A-Za-z0-9_\-.]{0,100}(?:" + SecretKey + @")[""']?[ \t]*:[ \t]*)(?<value>" + QuotedValue + @"|[^\s""'<\[{][^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeoutMs)]
    private static partial Regex KeyAtLineStart();

    // "key": value inside JSON, anywhere on a line.
    [GeneratedRegex(
        @"(?<key>""[A-Za-z0-9_\-.]{0,100}(?:" + SecretKey + @")""[ \t]*:[ \t]*)(?<value>" + QuotedValue + @"|[^\s,}\]""'<\[{][^\s,}\]""']*)",
        RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex JsonKey();

    // A Kubernetes env entry as manifests print it (helm --debug, kubectl get -o yaml): the secret's
    // name is on the "- name:" line and its value on the next.
    [GeneratedRegex(
        @"^(?<key>[ \t]*(?:-[ \t]+)?name:[ \t]*[""']?[A-Za-z0-9_\-.]{0,100}(?:" + SecretKey + @")[""']?[ \t]*\r?\n[ \t]*value:[ \t]*)(?<value>" + QuotedValue + @"|[^\s""'<\[{][^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeoutMs)]
    private static partial Regex ManifestEnvEntry();

    // The same entry in JSON: {"name":"DB_PASSWORD","value":"…"}.
    [GeneratedRegex(
        @"(?<key>""name""[ \t]*:[ \t]*""[A-Za-z0-9_\-.]{0,100}(?:" + SecretKey + @")""[ \t]*,[ \t]*""value""[ \t]*:[ \t]*)(?<value>" + QuotedValue + @")",
        RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex JsonEnvEntry();
}
