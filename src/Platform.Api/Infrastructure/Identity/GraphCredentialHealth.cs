using System.Text.RegularExpressions;
using Azure.Identity;

namespace Platform.Api.Infrastructure.Identity;

/// <summary>
/// The Graph app failing to sign in, as the directory reads have seen it since the last one that
/// got through. <see cref="Since"/> is the first failure of this run, <see cref="LastFailedAt"/>
/// the latest. <see cref="Code"/> is Entra's <c>AADSTS…</c> error code when the message carries
/// one (<c>AADSTS7000222</c>: the client secret expired); <see cref="Message"/> is the first
/// sentence of Entra's explanation, without the trace and correlation ids. Neither ever holds the
/// secret: Entra doesn't echo it back.
/// </summary>
public sealed record GraphCredentialFailure(
    DateTimeOffset Since, DateTimeOffset LastFailedAt, string? Code, string Message);

/// <summary>
/// Whether the Graph app's credential currently works, so an admin can be told when it doesn't.
///
/// <para>Every Graph read signs in with the app's client secret first. When that fails — on
/// 2026-10-19 the secret had expired and went unnoticed for about 20 hours — every directory read
/// fails the same way, and each caller swallows it on purpose: person and group search come back
/// empty, group-membership approval checks answer "not a member", account checks answer
/// "unknown". This is where those failures meet. <see cref="EntraIdGraphService"/> records each
/// credential failure here and clears it on the next read Graph answers; the admin banner reads it
/// through <c>GET /api/settings/directory-health</c>.</para>
///
/// <para>Process memory, per replica: a restart (which rotating the secret needs anyway, since it
/// is an environment variable) starts clean, and an expired secret fails on every replica's first
/// read.</para>
/// </summary>
public sealed class GraphCredentialHealth
{
    // Entra's error code, e.g. "AADSTS7000222", followed by its explanation.
    private static readonly Regex AadstsError = new(@"(AADSTS\d+)\s*:?\s*", RegexOptions.Compiled);
    private const int MaxMessageLength = 300;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private GraphCredentialFailure? _failure;

    public GraphCredentialHealth(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The credential failure still current, or null when the last Graph read got through.</summary>
    public GraphCredentialFailure? Failure => Volatile.Read(ref _failure);

    /// <summary>
    /// Notes that the credential failed just now. Returns the failure as recorded — its
    /// <see cref="GraphCredentialFailure.Since"/> stays that of the first failure until a read
    /// succeeds.
    /// </summary>
    public GraphCredentialFailure RecordFailure(AuthenticationFailedException ex)
    {
        var now = _time.GetUtcNow();
        var (code, message) = Describe(ex);
        lock (_gate)
        {
            var failure = new GraphCredentialFailure(_failure?.Since ?? now, now, code, message);
            Volatile.Write(ref _failure, failure);
            return failure;
        }
    }

    /// <summary>
    /// Notes that Graph answered, so the credential works. Returns the failure this clears, or null
    /// when there was none.
    /// </summary>
    public GraphCredentialFailure? RecordSuccess()
    {
        if (Volatile.Read(ref _failure) is null) return null;
        lock (_gate)
        {
            var cleared = _failure;
            Volatile.Write(ref _failure, null);
            return cleared;
        }
    }

    /// <summary>
    /// The credential failure behind <paramref name="ex"/>, if that is what it is — the exception
    /// itself or one it wraps. Azure.Identity throws it before any request goes out, so it is never
    /// Graph's answer.
    /// </summary>
    public static AuthenticationFailedException? CredentialFailureIn(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationFailedException auth) return auth;
        }
        return null;
    }

    // Azure.Identity's message reads "ClientSecretCredential authentication failed: AADSTS7000222:
    // The provided client secret keys for app '…' are expired. Visit the Azure portal to create new
    // keys … Trace ID: … Correlation ID: … Timestamp: …". The code and its first sentence are what an
    // admin needs; the ids belong in the log, which still gets the whole exception.
    internal static (string? Code, string Message) Describe(Exception ex)
    {
        var text = ex.Message ?? "";
        string? code = null;
        var match = AadstsError.Match(text);
        if (match.Success)
        {
            code = match.Groups[1].Value;
            text = text[(match.Index + match.Length)..];
            var sentenceEnd = text.IndexOf(". ", StringComparison.Ordinal);
            if (sentenceEnd >= 0) text = text[..(sentenceEnd + 1)];
        }

        var firstLine = text.Split('\n', 2)[0];
        var message = Regex.Replace(firstLine, @"\s+", " ").Trim();
        if (message.Length > MaxMessageLength) message = message[..(MaxMessageLength - 1)] + "…";
        return (code, message.Length > 0 ? message : ex.GetType().Name);
    }
}
