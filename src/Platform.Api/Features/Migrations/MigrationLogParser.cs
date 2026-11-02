using System.Globalization;
using System.Text.RegularExpressions;
using Platform.Api.Features.Migrations.Models;

namespace Platform.Api.Features.Migrations;

/// <summary>
/// Reads migrations out of captured output. Two jobs:
/// <list type="number">
///   <item><see cref="ExtractSchemaMigration"/> finds a schema migration's own output inside the
///   blocks a deploy event carries — the hook pod sections of <c>helm hook logs</c> (mpt-release
///   streams them), the migration job's pod log in <c>failure diagnostics</c> (both release pipelines
///   dump it when the hook fails), a dedicated <c>schema migration</c> block (a producer that captures
///   it on purpose), and Helm's own <c>* job x-migrations failed: …</c> line.</item>
///   <item><see cref="Parse"/> turns that output into the individual migrations and how each went.</item>
/// </list>
///
/// <para>What <see cref="Parse"/> reads is the stdout of <c>migrate --schema</c> / <c>migrate --data</c>
/// from <c>Mpt.Framework.Cli.Migrations</c>. Its log events go to Application Insights only; stdout
/// carries a status table, re-printed after every migration (in a pod nothing redraws it in place,
/// so each copy is appended — the <b>last</b> row for an id is its outcome):</para>
/// <code>
/// [✓] 4640 AddOrderIndex      1m 2026-11-02T09:14:03     1s
/// [x] 4650 AlterAccountName   1m 2026-11-02T09:14:04     0s Invalid column name 'DisplayName'.
/// [&lt;] 4630 AddAuditTable      1m 2026-11-02T09:14:04
/// [.] 4660 AddOrderTotals     1m                   -
/// </code>
/// <para>…then <c>Schema migrations completed.</c> / <c>Data migrations completed.</c>, or a failure
/// block (<c>The following migrations have failed:</c> and one <c>- Id: message</c> line each), and
/// <c>No migrations to run.</c> when the schema was current. Older packages print the same rows
/// without the timeout and duration columns, and some modules add a Serilog console sink
/// (<c>[09:58:09 ERR] …</c>), whose error lines are the only trace of a run that failed before it got
/// to the table — a bad connection string, say.</para>
/// Pure functions over text, so both are pinned by unit tests against real output.
/// </summary>
public static partial class MigrationLogParser
{
    /// <param name="Items">The migrations the output names, in the order the library runs them.</param>
    /// <param name="FailureReason">The line that explains the failure, when the run failed.</param>
    /// <param name="Failed">The output shows the run failing.</param>
    /// <param name="Completed">The output shows the run finishing ("… migrations completed.").</param>
    /// <param name="NothingToRun">The library said there was nothing to run.</param>
    public record ParseResult(List<MigrationItem> Items, string? FailureReason, bool Failed, bool Completed, bool NothingToRun);

    public record ExtractedMigration(string Content, string Source, bool JobFailed);

    // A status row. The marker is ✓ < x r . ✋ — or a mangled ✓ on a non-UTF-8 console, hence the
    // looser class; the timeout and duration columns only exist on newer packages.
    [GeneratedRegex(@"^\[(?<s>[^\]\s]{1,3})\]\s+(?<order>\d+)\s+(?<id>\S+)(?:\s+(?<timeout>(?:\d+[dhms])+))?\s+(?:(?<at>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)|\.\.\.|-)(?:\s+(?<dur>(?:\d+[dhms])+)(?=\s|$))?(?:\s+(?<err>\S.*?))?\s*$")]
    private static partial Regex StatusRow();

    // Rows can be glued to the previous line: a data migration prints "[.] …" without a newline and
    // its "[✓] …" follows on the same line when the script was silent.
    [GeneratedRegex(@"(?=\[(?:✓|<|x|r|\.|✋|[^\]\s]{2,3})\] \d+ )")]
    private static partial Regex RowStart();

    [GeneratedRegex(@"^The following migrations have failed:\s*$")]
    private static partial Regex FailureBlockStart();

    [GeneratedRegex(@"^-\s+(?<id>[^:\s]+):\s*(?<msg>.*)$")]
    private static partial Regex FailureBlockLine();

    [GeneratedRegex(@"\b(?:Schema|Data) migrations completed\.?|\bschema migrations applied successfully", RegexOptions.IgnoreCase)]
    private static partial Regex CompletedLine();

    [GeneratedRegex(@"^\s*No migrations to run\.?\s*$|No (?:pending |new )?(?:schema |data )?migrations (?:to (?:run|apply)|were applied|pending)|database is already up to date", RegexOptions.IgnoreCase)]
    private static partial Regex NothingToRunLine();

    // A Serilog console line, "[09:58:09 ERR] message".
    [GeneratedRegex(@"^\[(?<time>\d\d:\d\d:\d\d)(?:\.\d+)?\s+(?<level>VRB|DBG|INF|WRN|ERR|FTL)\]\s?(?<msg>.*)$")]
    private static partial Regex SerilogLine();

    // EF Core, for a service that migrates with EF instead: "Applying migration '20261014093012_AddX'."
    [GeneratedRegex(@"\bApplying migration '(?<name>[^']+)'")]
    private static partial Regex EfApplying();

    [GeneratedRegex(@"^(?:Unhandled exception\.\s*)?(?:[\w.]+\.)?[A-Z]\w*Exception(?::|\s*\()\s*.+")]
    private static partial Regex ExceptionLine();

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    // mpt-release's hook stream: "=====Logs from hook pod/x-migrations-abcde (job x-migrations)=====".
    [GeneratedRegex(@"^=+\s*Logs from hook pod/(?<pod>\S+)\s+\(job\s+(?<job>[^)\s]+)\)\s*=+\s*$")]
    private static partial Regex HookBanner();

    [GeneratedRegex(@"^=+\s*End logs from hook pod/(?<pod>\S+)\s*=+\s*$")]
    private static partial Regex HookBannerEnd();

    // Failure diagnostics, both pipelines: "--- Logs for pod: x (Phase: Failed) ---",
    // "--- Logs for job pod: x ---".
    [GeneratedRegex(@"^---\s*Logs for [\w ]*?pod:?\s+(?<pod>[\w.\-]+)[^\n]*?---\s*$")]
    private static partial Regex DiagnosticsPodHeader();

    // Helm's own report of a failed hook job: "\t* job x-migrations failed: BackoffLimitExceeded".
    [GeneratedRegex(@"^\s*\*\s*job\s+(?<job>\S+)\s+failed:\s*(?<reason>.+)$")]
    private static partial Regex HelmJobFailed();

    [GeneratedRegex(@"(?:pre|post)-(?:install|upgrade)\S* hooks? failed|failed (?:pre|post)-(?:install|upgrade)|hooks? failed", RegexOptions.IgnoreCase)]
    private static partial Regex HookFailureReason();

    [GeneratedRegex(@"(?<n>\d+)(?<u>[dhms])")]
    private static partial Regex DurationPart();

    /// <summary>A migration job/pod by name: the charts name it <c>{chart}-migrations</c>.</summary>
    private static bool IsSchemaMigrationName(string name) =>
        name.Contains("migration", StringComparison.OrdinalIgnoreCase)
        // The data migration job is not a hook and not part of a deployment.
        && !name.Contains("data-migration", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a deploy failure reason that says a Helm hook failed.</summary>
    public static bool IsHookFailure(string? reason) => reason is not null && HookFailureReason().IsMatch(reason);

    /// <summary>
    /// The schema migration a deploy event's output contains, or null when it contains none. Pod logs
    /// are taken verbatim, each pod once however many blocks repeat it, and nothing around them — in
    /// particular no <c>kubectl describe</c> output, which prints a container's environment.
    /// </summary>
    public static ExtractedMigration? ExtractSchemaMigration(IEnumerable<(string Name, string Content)> blocks, string service)
    {
        var sections = new List<string>();
        var seenPods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = "helm";
        var jobFailed = false;
        var helmFailures = new List<string>();

        foreach (var (name, content) in blocks)
        {
            if (string.IsNullOrEmpty(content)) continue;
            var lines = SplitLines(content);

            // A block the producer captured for exactly this purpose is migration output throughout. When
            // it is split into hook pod sections (marketplace SDP writes them like mpt-release streams
            // them) they go through the loop below, so a pod the failure diagnostics dump again is kept
            // once; without sections the block is taken whole.
            var dedicated = name.StartsWith("schema migration", StringComparison.OrdinalIgnoreCase);
            if (dedicated && !lines.Any(l => HookBanner().IsMatch(l)))
            {
                sections.Add(content.TrimEnd());
                source = "kubectl";
                jobFailed |= lines.Any(l => HelmJobFailed().Match(l) is { Success: true } m && IsSchemaMigrationName(m.Groups["job"].Value));
                continue;
            }
            if (dedicated) source = "kubectl";

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                var helmFail = HelmJobFailed().Match(line);
                if (helmFail.Success && IsSchemaMigrationName(helmFail.Groups["job"].Value))
                {
                    jobFailed = true;
                    helmFailures.Add(line.Trim());
                    continue;
                }

                var banner = HookBanner().Match(line);
                if (banner.Success)
                {
                    var end = i + 1;
                    while (end < lines.Length && !HookBannerEnd().IsMatch(lines[end]) && !HookBanner().IsMatch(lines[end])) end++;
                    if (IsSchemaMigrationName(banner.Groups["job"].Value) && seenPods.Add(banner.Groups["pod"].Value))
                        sections.Add(string.Join('\n', lines[i..Math.Min(end + 1, lines.Length)]).TrimEnd());
                    i = end;
                    continue;
                }

                var podHeader = DiagnosticsPodHeader().Match(line);
                if (podHeader.Success)
                {
                    var pod = podHeader.Groups["pod"].Value;
                    var end = i + 1;
                    // A pod's log ends at the next section header of the dump.
                    while (end < lines.Length && !lines[end].StartsWith("---") && !lines[end].StartsWith("===")) end++;
                    if (IsSchemaMigrationName(pod))
                    {
                        // The dump only holds failed pods — that is what it is for — so the pod failed
                        // even when its output was already taken from another block.
                        jobFailed = true;
                        if (seenPods.Add(pod))
                        {
                            sections.Add(string.Join('\n', lines[i..end]).TrimEnd());
                            source = "kubectl";
                        }
                    }
                    i = end - 1;
                }
            }
        }

        if (sections.Count == 0 && helmFailures.Count == 0) return null;
        // Helm's line is worth keeping when no pod log survived: it names the job and why it failed.
        return new ExtractedMigration(string.Join("\n\n", sections.Concat(helmFailures.Distinct())), source, jobFailed);
    }

    /// <summary>The individual migrations in a run's output and how the run ended.</summary>
    public static ParseResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new ParseResult([], null, false, false, false);

        // Last row per id wins; first appearance keeps the order (the library prints by order id).
        var rows = new Dictionary<string, MigrationItem>(StringComparer.Ordinal);
        var order = new List<string>();
        var blockErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        string? firstError = null;
        var failed = false;
        var completed = false;
        var nothingToRun = false;
        var inFailureBlock = false;
        string? efRunning = null;

        foreach (var physical in SplitLines(AnsiEscape().Replace(content, "")))
        {
            foreach (var piece in RowStart().Split(physical))
            {
                var line = piece.TrimEnd();
                if (line.Length == 0)
                {
                    inFailureBlock = false;
                    continue;
                }

                var row = StatusRow().Match(line);
                if (row.Success)
                {
                    var item = ToItem(row);
                    if (!rows.ContainsKey(item.Name)) order.Add(item.Name);
                    rows[item.Name] = item;
                    if (item.Status == MigrationItemStatus.Failed)
                    {
                        failed = true;
                        if (item.Error is not null) firstError ??= $"{item.Name}: {item.Error}";
                    }
                    continue;
                }

                if (FailureBlockStart().IsMatch(line))
                {
                    inFailureBlock = true;
                    failed = true;
                    continue;
                }
                if (inFailureBlock)
                {
                    var entry = FailureBlockLine().Match(line);
                    if (entry.Success)
                    {
                        var id = entry.Groups["id"].Value;
                        var msg = entry.Groups["msg"].Value.Trim();
                        // "Migration rolled back" names the casualties, not the cause.
                        if (!msg.Equals("Migration rolled back", StringComparison.OrdinalIgnoreCase))
                        {
                            blockErrors.TryAdd(id, msg);
                            firstError = firstError is null || !firstError.Contains(':') ? $"{id}: {msg}" : firstError;
                        }
                    }
                    // A multi-line exception message continues until the blank line ending the block.
                    continue;
                }

                if (CompletedLine().IsMatch(line)) completed = true;
                if (NothingToRunLine().IsMatch(line)) nothingToRun = true;

                var ef = EfApplying().Match(line);
                if (ef.Success)
                {
                    efRunning = ef.Groups["name"].Value;
                    if (!rows.ContainsKey(efRunning)) order.Add(efRunning);
                    rows[efRunning] = new MigrationItem(efRunning, MigrationItemStatus.Running);
                    continue;
                }

                var serilog = SerilogLine().Match(line);
                var isError = serilog.Success
                    ? serilog.Groups["level"].Value is "ERR" or "FTL"
                    : ExceptionLine().IsMatch(line.Trim());
                if (isError)
                {
                    failed = true;
                    var text = (serilog.Success ? serilog.Groups["msg"].Value : line).Trim();
                    if (text.Length > 0) firstError ??= text;
                }
            }
        }

        var items = order.Select(name =>
        {
            var item = rows[name];
            // The failure block carries the whole first line of the message; the table cuts it at 97.
            if (item.Status == MigrationItemStatus.Failed && blockErrors.TryGetValue(name, out var full))
                item = item with { Error = Clip(full, 2000) };
            // "Running" when the output stopped: on a failed run that is where it died. EF only says
            // "Applying", so on a completed run the migration went through.
            if (item.Status == MigrationItemStatus.Running && (failed || completed))
                item = item with { Status = failed && name == efRunning ? MigrationItemStatus.Failed : MigrationItemStatus.Applied };
            return item;
        }).ToList();

        // A failure-block id with no row at all (older packages print no table on failure).
        foreach (var (id, msg) in blockErrors)
        {
            if (!rows.ContainsKey(id)) items.Add(new MigrationItem(id, MigrationItemStatus.Failed, Error: Clip(msg, 2000)));
        }

        return new ParseResult(
            items,
            failed && firstError is not null ? Clip(firstError, 500) : null,
            failed,
            completed && !failed,
            nothingToRun && items.Count == 0);
    }

    private static MigrationItem ToItem(Match row)
    {
        var status = row.Groups["s"].Value switch
        {
            "x" => MigrationItemStatus.Failed,
            "<" => MigrationItemStatus.RolledBack,
            "r" => MigrationItemStatus.Running,
            "." => MigrationItemStatus.Pending,
            // ✓, ✋ (marked applied by hand on 5.x packages), or a ✓ the console mangled: recorded.
            _ => row.Groups["at"].Success ? MigrationItemStatus.Applied : MigrationItemStatus.Pending,
        };
        DateTimeOffset? at = row.Groups["at"].Success
            && DateTime.TryParseExact(row.Groups["at"].Value, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
        var error = row.Groups["err"].Success && status == MigrationItemStatus.Failed ? row.Groups["err"].Value.Trim() : null;
        return new MigrationItem(
            row.Groups["id"].Value,
            status,
            row.Groups["dur"].Success ? ParseDurationMs(row.Groups["dur"].Value) : null,
            error,
            int.TryParse(row.Groups["order"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : null,
            at);
    }

    /// <summary>The library's readable durations: <c>0s</c>, <c>45s</c>, <c>1m30s</c>, <c>2h5m</c>.</summary>
    internal static long? ParseDurationMs(string text)
    {
        long total = 0;
        var any = false;
        foreach (Match m in DurationPart().Matches(text))
        {
            any = true;
            var n = long.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            total += m.Groups["u"].Value switch { "d" => n * 86_400_000, "h" => n * 3_600_000, "m" => n * 60_000, _ => n * 1000 };
        }
        return any ? total : null;
    }

    private static string[] SplitLines(string content) => content.Replace("\r\n", "\n").Split('\n', '\r');

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
