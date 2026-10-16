namespace Platform.Api.Features.Deployments.Models;

/// <summary>
/// A block of output captured by the pipeline that produced a deploy event — the Helm release
/// printout, the readiness diagnostics, the web upload log. Kept in its own table rather than on
/// <see cref="DeployEvent"/> because a single release can print hundreds of kilobytes, and every
/// list/history query loads whole <c>DeployEvent</c> rows; only the detail page reads logs.
///
/// <para>Content is capped at ingest (see <c>DeploymentService.LogContentLimitBytes</c>) keeping the
/// tail, because a failing deploy prints its diagnostics last. <see cref="Truncated"/> records that
/// the head was dropped so the UI can say so instead of implying the log is complete.</para>
/// </summary>
public class DeployEventLog
{
    public Guid Id { get; set; }
    public Guid DeployEventId { get; set; }

    /// <summary>
    /// Human label for the block, unique per event: "helm upgrade output", "failure diagnostics".
    /// Doubles as the idempotency key so re-posting the same event replaces rather than duplicates.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Which tool produced it — "helm", "kubectl", "pipeline". Free-form; drives an icon at most.</summary>
    public string? Source { get; set; }

    /// <summary>Display order within the event, as the producer sent them.</summary>
    public int Sequence { get; set; }

    public string Content { get; set; } = "";

    /// <summary>True when the stored content is a tail of a larger log.</summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Size and line count of the stored content, and the size the producer sent before capping.
    /// Materialised at ingest so the detail page can list and size the blocks without reading a
    /// single one of them — the whole reason this table exists.
    /// </summary>
    public int ByteCount { get; set; }
    public int LineCount { get; set; }
    public int OriginalByteCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
