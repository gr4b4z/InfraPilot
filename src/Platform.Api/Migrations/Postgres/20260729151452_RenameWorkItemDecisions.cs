using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <summary>
    /// Renames the work-item sign-off decisions: what was <c>Blocked</c> is now <c>Issue</c>, and what
    /// was <c>Rejected</c> is now <c>Blocked</c>. Data-only — the column is already a 20-char string
    /// (<c>HasConversion&lt;string&gt;</c>), so nothing about the schema changes.
    ///
    /// <para>Existing rows carry over by meaning: a reviewer who flagged a problem still has a flagged
    /// problem, a reviewer who held the item back still holds it back. Only the word changes.</para>
    ///
    /// <para><b>The two statements per table are order-dependent.</b> <c>Blocked</c> is both an old and
    /// a new value with different meanings, so the old <c>Blocked</c> rows must become <c>Issue</c>
    /// <i>before</i> the old <c>Rejected</c> rows become <c>Blocked</c>. Run them the other way round
    /// and every rejection would be swept into <c>Issue</c> on the second pass.</para>
    ///
    /// <para>Promotion-level decisions (<c>promotion_approvals</c>, <c>rollback_approvals</c>) are
    /// untouched: their <c>Rejected</c> is a veto that terminates a candidate and keeps that name.</para>
    /// </summary>
    public partial class RenameWorkItemDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters — see the class summary.
            migrationBuilder.Sql(
                """UPDATE work_item_approvals SET "Decision" = 'Issue' WHERE "Decision" = 'Blocked';""");
            migrationBuilder.Sql(
                """UPDATE work_item_approvals SET "Decision" = 'Blocked' WHERE "Decision" = 'Rejected';""");

            // The decision entries written into work-item comment threads carry the same values.
            migrationBuilder.Sql(
                """UPDATE work_item_comments SET "Decision" = 'Issue' WHERE "Decision" = 'Blocked';""");
            migrationBuilder.Sql(
                """UPDATE work_item_comments SET "Decision" = 'Blocked' WHERE "Decision" = 'Rejected';""");

            // Those same entries carry a machine-written headline derived from the decision
            // ("Blocked this work item."), with the reviewer's own note appended after a blank line.
            // The headline has to move with the decision or the thread contradicts the badge beside
            // it. Only the headline is rewritten — the note is the reviewer's words and is preserved
            // byte for byte, which is why this replaces a prefix rather than doing a blind REPLACE.
            //
            // Matching on the already-migrated Decision *and* the old prefix makes these two
            // statements unambiguous, so unlike the pair above they are order-independent. Rows with
            // a null Decision are human comments and are never touched.
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET "Body" = 'Raised an issue on this work item.'
                             || substr("Body", char_length('Blocked this work item.') + 1)
                WHERE "Decision" = 'Issue' AND "Body" LIKE 'Blocked this work item.%';
                """);
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET "Body" = 'Blocked this work item.'
                             || substr("Body", char_length('Rejected this work item.') + 1)
                WHERE "Decision" = 'Blocked' AND "Body" LIKE 'Rejected this work item.%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Headlines first, while the Decision column still holds the new values they key on.
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET "Body" = 'Rejected this work item.'
                             || substr("Body", char_length('Blocked this work item.') + 1)
                WHERE "Decision" = 'Blocked' AND "Body" LIKE 'Blocked this work item.%';
                """);
            migrationBuilder.Sql(
                """
                UPDATE work_item_comments
                SET "Body" = 'Blocked this work item.'
                             || substr("Body", char_length('Raised an issue on this work item.') + 1)
                WHERE "Decision" = 'Issue' AND "Body" LIKE 'Raised an issue on this work item.%';
                """);

            // Reverse order, for the same reason as in Up.
            migrationBuilder.Sql(
                """UPDATE work_item_approvals SET "Decision" = 'Rejected' WHERE "Decision" = 'Blocked';""");
            migrationBuilder.Sql(
                """UPDATE work_item_approvals SET "Decision" = 'Blocked' WHERE "Decision" = 'Issue';""");

            migrationBuilder.Sql(
                """UPDATE work_item_comments SET "Decision" = 'Rejected' WHERE "Decision" = 'Blocked';""");
            migrationBuilder.Sql(
                """UPDATE work_item_comments SET "Decision" = 'Blocked' WHERE "Decision" = 'Issue';""");
        }
    }
}
