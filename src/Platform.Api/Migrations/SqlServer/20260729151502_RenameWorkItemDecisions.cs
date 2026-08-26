using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <summary>
    /// SQL Server counterpart of the Postgres <c>RenameWorkItemDecisions</c> — see that migration for
    /// the full rationale. Renames the work-item sign-off decisions: what was <c>Blocked</c> is now
    /// <c>Issue</c>, what was <c>Rejected</c> is now <c>Blocked</c>. Data-only.
    ///
    /// <para><b>Statement order is load-bearing:</b> <c>Blocked</c> is both an old and a new value with
    /// different meanings, so the old <c>Blocked</c> rows must become <c>Issue</c> before the old
    /// <c>Rejected</c> rows become <c>Blocked</c>.</para>
    /// </summary>
    public partial class RenameWorkItemDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters — see the class summary.
            migrationBuilder.Sql(
                "UPDATE work_item_approvals SET [Decision] = 'Issue' WHERE [Decision] = 'Blocked';");
            migrationBuilder.Sql(
                "UPDATE work_item_approvals SET [Decision] = 'Blocked' WHERE [Decision] = 'Rejected';");

            // The decision entries written into work-item comment threads carry the same values.
            migrationBuilder.Sql(
                "UPDATE work_item_comments SET [Decision] = 'Issue' WHERE [Decision] = 'Blocked';");
            migrationBuilder.Sql(
                "UPDATE work_item_comments SET [Decision] = 'Blocked' WHERE [Decision] = 'Rejected';");

            // Those entries also carry a machine-written headline derived from the decision, with the
            // reviewer's own note appended after a blank line. The headline moves with the decision;
            // the note is preserved exactly, hence a prefix swap rather than a blind REPLACE. Keying
            // on the migrated Decision plus the old prefix makes these order-independent.
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET [Body] = 'Raised an issue on this work item.'
                             + SUBSTRING([Body], LEN('Blocked this work item.') + 1, LEN([Body]))
                WHERE [Decision] = 'Issue' AND [Body] LIKE 'Blocked this work item.%';
                """);
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET [Body] = 'Blocked this work item.'
                             + SUBSTRING([Body], LEN('Rejected this work item.') + 1, LEN([Body]))
                WHERE [Decision] = 'Blocked' AND [Body] LIKE 'Rejected this work item.%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Headlines first, while the Decision column still holds the new values they key on.
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET [Body] = 'Rejected this work item.'
                             + SUBSTRING([Body], LEN('Blocked this work item.') + 1, LEN([Body]))
                WHERE [Decision] = 'Blocked' AND [Body] LIKE 'Blocked this work item.%';
                """);
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET [Body] = 'Blocked this work item.'
                             + SUBSTRING([Body], LEN('Raised an issue on this work item.') + 1, LEN([Body]))
                WHERE [Decision] = 'Issue' AND [Body] LIKE 'Raised an issue on this work item.%';
                """);

            // Reverse order, for the same reason as in Up.
            migrationBuilder.Sql(
                "UPDATE work_item_approvals SET [Decision] = 'Rejected' WHERE [Decision] = 'Blocked';");
            migrationBuilder.Sql(
                "UPDATE work_item_approvals SET [Decision] = 'Blocked' WHERE [Decision] = 'Issue';");

            migrationBuilder.Sql(
                "UPDATE work_item_comments SET [Decision] = 'Rejected' WHERE [Decision] = 'Blocked';");
            migrationBuilder.Sql(
                "UPDATE work_item_comments SET [Decision] = 'Blocked' WHERE [Decision] = 'Issue';");
        }
    }
}
