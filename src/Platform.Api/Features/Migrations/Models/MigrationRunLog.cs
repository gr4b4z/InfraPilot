namespace Platform.Api.Features.Migrations.Models;

/// <summary>
/// A block of output a migration printed — the migration job's pod log. Same shape and the same
/// reasons as <see cref="Deployments.Models.DeployEventLog"/>: its own table so list queries never
/// drag log text along, the tail kept when it is over the cap, and <see cref="Name"/> as the
/// replace-on-retry key within a run.
/// </summary>
public class MigrationRunLog
{
    public Guid Id { get; set; }
    public Guid MigrationRunId { get; set; }

    /// <summary>"migration job output", "attempt 2" — unique per run.</summary>
    public string Name { get; set; } = "";

    /// <summary>Which tool produced it — "kubectl", "helm", "pipeline".</summary>
    public string? Source { get; set; }

    public int Sequence { get; set; }

    public string Content { get; set; } = "";

    public bool Truncated { get; set; }

    public int ByteCount { get; set; }
    public int LineCount { get; set; }
    public int OriginalByteCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
