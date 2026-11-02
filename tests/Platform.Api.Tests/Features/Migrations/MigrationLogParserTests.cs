using Platform.Api.Features.Migrations;
using Platform.Api.Features.Migrations.Models;

namespace Platform.Api.Tests.Features.Migrations;

/// <summary>
/// The migration library's stdout, and the pipeline blocks it arrives in, as they look in practice —
/// the samples below are trimmed copies of real hook logs and failure diagnostics.
/// </summary>
public class MigrationLogParserTests
{
    private const string SchemaSuccess = """
        Reading old history table...
        History migration complete
        [.] 4640 AddOrderIndex         1m                   -
        [.] 4650 AlterAccountName      1m                   -
        [✓] 4640 AddOrderIndex         1m 2026-11-02T09:14:03
        [.] 4650 AlterAccountName      1m                   -
        [✓] 4640 AddOrderIndex         1m 2026-11-02T09:14:03
        [✓] 4650 AlterAccountName      1m 2026-11-02T09:14:05
        [✓] 4640 AddOrderIndex         1m 2026-11-02T09:14:03               1s
        [✓] 4650 AlterAccountName      1m 2026-11-02T09:14:05             1m30s
        Schema migrations completed.
        """;

    private const string SchemaFailure = """
        [.] 4640 AddOrderIndex         1m                   -
        [.] 4650 AlterAccountName      1m                   -
        [.] 4660 AddAuditTable         1m                   -
        [✓] 4640 AddOrderIndex         1m 2026-11-02T09:14:03
        [.] 4650 AlterAccountName      1m                   -
        [.] 4660 AddAuditTable         1m                   -
        [<] 4640 AddOrderIndex         1m 2026-11-02T09:14:04               1s
        [x] 4650 AlterAccountName      1m 2026-11-02T09:14:04               0s Invalid column name 'DisplayName'.
        [.] 4660 AddAuditTable         1m                   -
        The following migrations have failed:
        - AlterAccountName: Invalid column name 'DisplayName'.
        - AddOrderIndex: Migration rolled back

        The following migrations have failed:
        - AlterAccountName: Invalid column name 'DisplayName'.
        - AddOrderIndex: Migration rolled back

        """;

    [Fact]
    public void Parse_SchemaSuccess_LastRowPerIdWins_WithDurationsAndTimes()
    {
        var result = MigrationLogParser.Parse(SchemaSuccess);

        Assert.False(result.Failed);
        Assert.True(result.Completed);
        Assert.Equal(["AddOrderIndex", "AlterAccountName"], result.Items.Select(i => i.Name));
        Assert.All(result.Items, i => Assert.Equal(MigrationItemStatus.Applied, i.Status));
        Assert.Equal(4640, result.Items[0].Order);
        Assert.Equal(1000, result.Items[0].DurationMs);
        Assert.Equal(90_000, result.Items[1].DurationMs);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 14, 5, TimeSpan.Zero), result.Items[1].AppliedAt);
        // "Migrating: X" lines of the history copy are not migrations of this run.
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Parse_SchemaFailure_NamesTheFailedOneAndTheRolledBackOne()
    {
        var result = MigrationLogParser.Parse(SchemaFailure);

        Assert.True(result.Failed);
        Assert.False(result.Completed);
        var byName = result.Items.ToDictionary(i => i.Name);
        Assert.Equal(MigrationItemStatus.RolledBack, byName["AddOrderIndex"].Status);
        Assert.Equal(MigrationItemStatus.Failed, byName["AlterAccountName"].Status);
        Assert.Equal("Invalid column name 'DisplayName'.", byName["AlterAccountName"].Error);
        Assert.Equal(MigrationItemStatus.Pending, byName["AddAuditTable"].Status);
        Assert.Equal("AlterAccountName: Invalid column name 'DisplayName'.", result.FailureReason);
    }

    [Fact]
    public void Parse_DataRun_GluedRowsAndScriptOutput()
    {
        // A silent script's "[✓]" lands on the same line as its "[.]" after a carriage return; a
        // failing one prints the summary table straight after its unfinished row.
        const string output =
            "[.] 9927 MigrateLegacyPriceLists 1m                   -\r[✓] 9927 MigrateLegacyPriceLists 1m 2026-11-02T09:20:00           5s\n" +
            "[.] 9928 BackfillMultiSessionClaim      1m                   -\n" +
            "[09:20:01 INF] Starting sync of users ...\n" +
            "[✓] 9928 BackfillMultiSessionClaim      1m 2026-11-02T09:21:13           1m12s\n" +
            "[.] 9929 SyncIdentitiesToNotification      1m                   -[✓] 9928 BackfillMultiSessionClaim         1m 2026-11-02T09:21:13           1m12s\n" +
            "[x] 9929 SyncIdentitiesToNotification      1m 2026-11-02T09:21:38             24s Failed with exitCode: '1'\n" +
            "The following migrations have failed:\n" +
            "- SyncIdentitiesToNotification: Failed with exitCode: '1'\n";

        var result = MigrationLogParser.Parse(output);

        Assert.True(result.Failed);
        Assert.Equal(
            ["MigrateLegacyPriceLists", "BackfillMultiSessionClaim", "SyncIdentitiesToNotification"],
            result.Items.Select(i => i.Name));
        Assert.Equal(
            [MigrationItemStatus.Applied, MigrationItemStatus.Applied, MigrationItemStatus.Failed],
            result.Items.Select(i => i.Status));
        Assert.Equal(72_000, result.Items[1].DurationMs);
        Assert.Equal("SyncIdentitiesToNotification: Failed with exitCode: '1'", result.FailureReason);
    }

    [Theory]
    // mpt-extension-producthub, staging: a module with a console sink, nothing pending.
    [InlineData("No migrations to run.\n[09:02:06 INF] Schema migrations completed.\nSchema migrations completed.\n")]
    // mpt-extension-adobe: a different runner with its own wording.
    [InlineData("Running schema migrations...\nschema migrations applied successfully.\n")]
    public void Parse_NothingNamed_IsACompletedRunWithNoItems(string output)
    {
        var result = MigrationLogParser.Parse(output);

        Assert.False(result.Failed);
        Assert.True(result.Completed);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Parse_ErrorBeforeTheTable_IsAFailureWithTheErrorAsReason()
    {
        // mpt-export, staging, 2026-11-02: the library never got to its table.
        const string output =
            "[09:58:09 ERR] Format of the initialization string does not conform to specification starting at index 0.\n" +
            "Format of the initialization string does not conform to specification starting at index 0.\n";

        var result = MigrationLogParser.Parse(output);

        Assert.True(result.Failed);
        Assert.Empty(result.Items);
        Assert.Equal("Format of the initialization string does not conform to specification starting at index 0.", result.FailureReason);
    }

    [Fact]
    public void Parse_OldPackageRowsWithoutTimeoutAndDuration()
    {
        const string output = "[.] 10 AddExchangeRate                          -\n[✓] 10 AddExchangeRate        2025-09-02T08:10:10\n";

        var item = Assert.Single(MigrationLogParser.Parse(output).Items);

        Assert.Equal("AddExchangeRate", item.Name);
        Assert.Equal(MigrationItemStatus.Applied, item.Status);
        Assert.Null(item.DurationMs);
    }

    [Fact]
    public void Extract_HookLogs_TakesOnlyMigrationHookPods()
    {
        var hookLogs = """
            =======================Logs from hook pod/orders-migrations-lr4bs (job orders-migrations)=======================
            No migrations to run.
            Schema migrations completed.
            =======================End logs from hook pod/orders-migrations-lr4bs=======================
            =======================Logs from hook pod/orders-config-check-9x2 (job orders-config-check)=======================
            config ok
            =======================End logs from hook pod/orders-config-check-9x2=======================
            """;

        var extracted = MigrationLogParser.ExtractSchemaMigration(
            [("helm upgrade output", "Release \"orders\" has been upgraded."), ("helm hook logs", hookLogs)], "orders");

        Assert.NotNull(extracted);
        Assert.False(extracted.JobFailed);
        Assert.Contains("orders-migrations-lr4bs", extracted.Content);
        Assert.DoesNotContain("config ok", extracted.Content);
    }

    [Fact]
    public void Extract_FailureDiagnostics_KeepsThePodLogOnceAndNeverTheDescribeOutput()
    {
        // Shape of the SDP dump: the same pod under "Failed Pod Logs" and again under "Failed Job
        // Logs", with `kubectl describe` — and the container environment it prints — in between.
        var diagnostics = """
            === Pods Status for release mpt-export ===
            mpt-export-migrations-rhbwz   0/1     Error    0          6s

            === Failed Pod Logs ===

            --- Logs for pod: mpt-export-migrations-rhbwz (Phase: Failed) ---
            [09:58:09 ERR] Format of the initialization string does not conform to specification starting at index 0.

            --- Describe pod: mpt-export-migrations-rhbwz ---
            Environment:
              PYRA_ServiceBus__ConnectionString:  Endpoint=sb://example/;SharedAccessKey=not-a-real-key

            === Failed Job Logs ===

            --- Job: mpt-export-migrations (Failed: 1) ---

            --- Logs for job pod: mpt-export-migrations-rhbwz ---
            [09:58:09 ERR] Format of the initialization string does not conform to specification starting at index 0.
            """;
        var helm = "Error: UPGRADE FAILED: pre-upgrade hooks failed: 1 error occurred:\n\t* job mpt-export-migrations failed: BackoffLimitExceeded\n";

        var extracted = MigrationLogParser.ExtractSchemaMigration(
            [("helm upgrade output", helm), ("failure diagnostics", diagnostics)], "mpt-export");

        Assert.NotNull(extracted);
        Assert.True(extracted.JobFailed);
        Assert.Equal("kubectl", extracted.Source);
        Assert.DoesNotContain("SharedAccessKey", extracted.Content);
        Assert.DoesNotContain("Describe pod", extracted.Content);
        Assert.Single(extracted.Content.Split('\n'), l => l.Contains("--- Logs for"));
        Assert.Contains("* job mpt-export-migrations failed: BackoffLimitExceeded", extracted.Content);
    }

    [Fact]
    public void Extract_DataMigrationJobIsNotASchemaMigration()
    {
        var diagnostics = "--- Logs for pod: orders-orders-data-migration-abc (Phase: Failed) ---\nboom\n";

        Assert.Null(MigrationLogParser.ExtractSchemaMigration([("failure diagnostics", diagnostics)], "orders"));
    }

    [Fact]
    public void Extract_NoMigrationOutput_ReturnsNull()
    {
        Assert.Null(MigrationLogParser.ExtractSchemaMigration(
            [("helm upgrade output", "Release \"web\" has been upgraded. Happy Helming!")], "web"));
    }

    [Theory]
    [InlineData("Error: UPGRADE FAILED: pre-upgrade hooks failed: 1 error occurred:", true)]
    [InlineData("Error: failed pre-install: 1 error occurred:", true)]
    [InlineData("pod api-x2klm keeps crash-looping (restartCount=4)", false)]
    public void IsHookFailure(string reason, bool expected) =>
        Assert.Equal(expected, MigrationLogParser.IsHookFailure(reason));
}
