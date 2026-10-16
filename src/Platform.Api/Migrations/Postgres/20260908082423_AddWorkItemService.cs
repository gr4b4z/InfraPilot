using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <summary>
    /// Adds the service to a work item's identity: sign-offs, threads and the candidate-scoped ticket
    /// index are now keyed on <c>(WorkItemKey, Product, Service, TargetEnv)</c> instead of
    /// <c>(WorkItemKey, Product, TargetEnv)</c>. The same Jira ticket carried by two services of one
    /// product — <c>mpt-helpdesk/MPT-1</c> and <c>mpt-platform/MPT-1</c> — is two work items from here
    /// on, each with its own decisions, comments and assignments.
    ///
    /// <para><b>Data migration.</b> The new column is added blank, then:</para>
    /// <list type="number">
    ///   <item><c>promotion_work_items</c> copy the service from their parent candidate.</item>
    ///   <item>Every legacy approval and comment is <b>duplicated once per service</b> that carried the
    ///         ticket in that <c>(product, targetEnv)</c>, status as-is. The original row keeps its id
    ///         and is moved onto the alphabetically first service; the copies get fresh ids. A ticket
    ///         seen in one service ends up with exactly the rows it had, now tagged.</item>
    ///   <item>Rows for a ticket no candidate index row remembers (the candidate was deleted) keep a
    ///         blank service. They were unreachable before too — the detail lookup 404s without an
    ///         index row — so nothing is lost, and nothing is invented.</item>
    /// </list>
    ///
    /// <para>The duplication runs <i>before</i> the new unique index is created. It could not
    /// violate it anyway (the old unique index held one row per approver per triple, and each copy
    /// lands on a distinct service), but the order keeps the migration readable as
    /// "widen, backfill, then constrain".</para>
    ///
    /// <para><b>Down</b> has to collapse the per-service rows back to one per approver before the old
    /// unique index can be recreated: the row on the alphabetically first service survives, the
    /// others are deleted — including any decisions and comments recorded per service after this
    /// migration ran. A rollback loses that information by construction.</para>
    /// </summary>
    public partial class AddWorkItemService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_work_item_comments_WorkItemKey_Product_TargetEnv_CreatedAt",
                table: "work_item_comments");

            migrationBuilder.DropIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv",
                table: "work_item_approvals");

            migrationBuilder.DropIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv_ApproverE~",
                table: "work_item_approvals");

            migrationBuilder.DropIndex(
                name: "IX_promotion_work_items_WorkItemKey_Product_TargetEnv",
                table: "promotion_work_items");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "work_item_comments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "work_item_approvals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "promotion_work_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // ── Backfill ──────────────────────────────────────────────────────────────────────

            // 1. The ticket index rows take the service of the candidate they hang off.
            migrationBuilder.Sql(
                """
                UPDATE promotion_work_items w
                SET "Service" = c."Service"
                FROM promotion_candidates c
                WHERE c."Id" = w."CandidateId";
                """);

            // 2. Approvals: one copy per ADDITIONAL service carrying the ticket in that product/env
            //    (the alphabetically first service is served by the original row, updated below).
            //    Decision, comment and timestamps are copied as-is — "duplicate the status".
            migrationBuilder.Sql(
                """
                INSERT INTO work_item_approvals
                    ("Id", "WorkItemKey", "Product", "Service", "TargetEnv", "ApproverEmail", "ApproverName",
                     "Decision", "Comment", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), a."WorkItemKey", a."Product", s."Service", a."TargetEnv",
                       a."ApproverEmail", a."ApproverName", a."Decision", a."Comment", a."CreatedAt", a."UpdatedAt"
                FROM work_item_approvals a
                JOIN (SELECT DISTINCT "WorkItemKey", "Product", "TargetEnv", "Service"
                      FROM promotion_work_items
                      WHERE "Service" <> '') s
                  ON s."WorkItemKey" = a."WorkItemKey"
                 AND s."Product" = a."Product"
                 AND s."TargetEnv" = a."TargetEnv"
                WHERE a."Service" = ''
                  AND s."Service" <> (SELECT MIN(m."Service") FROM promotion_work_items m
                                      WHERE m."WorkItemKey" = a."WorkItemKey"
                                        AND m."Product" = a."Product"
                                        AND m."TargetEnv" = a."TargetEnv"
                                        AND m."Service" <> '');
                """);

            //    The original row becomes the first service's instance, keeping its id (audit rows
            //    point at it).
            migrationBuilder.Sql(
                """
                UPDATE work_item_approvals a
                SET "Service" = (SELECT MIN(m."Service") FROM promotion_work_items m
                                 WHERE m."WorkItemKey" = a."WorkItemKey"
                                   AND m."Product" = a."Product"
                                   AND m."TargetEnv" = a."TargetEnv"
                                   AND m."Service" <> '')
                WHERE a."Service" = ''
                  AND EXISTS (SELECT 1 FROM promotion_work_items m
                              WHERE m."WorkItemKey" = a."WorkItemKey"
                                AND m."Product" = a."Product"
                                AND m."TargetEnv" = a."TargetEnv"
                                AND m."Service" <> '');
                """);

            // 3. Comment threads, the same way: every entry — human comments, decision entries and
            //    system notes alike — is copied onto each additional service, so every per-service
            //    thread starts with the full history it shared.
            migrationBuilder.Sql(
                """
                INSERT INTO work_item_comments
                    ("Id", "WorkItemKey", "Product", "Service", "TargetEnv", "AuthorEmail", "AuthorName",
                     "Body", "Decision", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), c."WorkItemKey", c."Product", s."Service", c."TargetEnv",
                       c."AuthorEmail", c."AuthorName", c."Body", c."Decision", c."CreatedAt", c."UpdatedAt"
                FROM work_item_comments c
                JOIN (SELECT DISTINCT "WorkItemKey", "Product", "TargetEnv", "Service"
                      FROM promotion_work_items
                      WHERE "Service" <> '') s
                  ON s."WorkItemKey" = c."WorkItemKey"
                 AND s."Product" = c."Product"
                 AND s."TargetEnv" = c."TargetEnv"
                WHERE c."Service" = ''
                  AND s."Service" <> (SELECT MIN(m."Service") FROM promotion_work_items m
                                      WHERE m."WorkItemKey" = c."WorkItemKey"
                                        AND m."Product" = c."Product"
                                        AND m."TargetEnv" = c."TargetEnv"
                                        AND m."Service" <> '');
                """);

            migrationBuilder.Sql(
                """
                UPDATE work_item_comments c
                SET "Service" = (SELECT MIN(m."Service") FROM promotion_work_items m
                                 WHERE m."WorkItemKey" = c."WorkItemKey"
                                   AND m."Product" = c."Product"
                                   AND m."TargetEnv" = c."TargetEnv"
                                   AND m."Service" <> '')
                WHERE c."Service" = ''
                  AND EXISTS (SELECT 1 FROM promotion_work_items m
                              WHERE m."WorkItemKey" = c."WorkItemKey"
                                AND m."Product" = c."Product"
                                AND m."TargetEnv" = c."TargetEnv"
                                AND m."Service" <> '');
                """);

            // ── Indexes on the widened key ────────────────────────────────────────────────────

            migrationBuilder.CreateIndex(
                name: "IX_work_item_comments_WorkItemKey_Product_Service_TargetEnv_Cr~",
                table: "work_item_comments",
                columns: new[] { "WorkItemKey", "Product", "Service", "TargetEnv", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv",
                table: "work_item_approvals",
                columns: new[] { "WorkItemKey", "Product", "Service", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv_A~",
                table: "work_item_approvals",
                columns: new[] { "WorkItemKey", "Product", "Service", "TargetEnv", "ApproverEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_work_items_WorkItemKey_Product_TargetEnv_Service",
                table: "promotion_work_items",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv", "Service" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_work_item_comments_WorkItemKey_Product_Service_TargetEnv_Cr~",
                table: "work_item_comments");

            migrationBuilder.DropIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv",
                table: "work_item_approvals");

            migrationBuilder.DropIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv_A~",
                table: "work_item_approvals");

            migrationBuilder.DropIndex(
                name: "IX_promotion_work_items_WorkItemKey_Product_TargetEnv_Service",
                table: "promotion_work_items");

            // Collapse the per-service instances back to one per (ticket, product, env[, approver]):
            // the alphabetically first service's rows survive, the rest go. Without this the old
            // unique index below could not be recreated. See the class summary for what is lost.
            migrationBuilder.Sql(
                """
                DELETE FROM work_item_approvals a
                WHERE a."Service" > (SELECT MIN(b."Service") FROM work_item_approvals b
                                     WHERE b."WorkItemKey" = a."WorkItemKey"
                                       AND b."Product" = a."Product"
                                       AND b."TargetEnv" = a."TargetEnv"
                                       AND b."ApproverEmail" = a."ApproverEmail");
                """);
            migrationBuilder.Sql(
                """
                DELETE FROM work_item_comments c
                WHERE c."Service" > (SELECT MIN(b."Service") FROM work_item_comments b
                                     WHERE b."WorkItemKey" = c."WorkItemKey"
                                       AND b."Product" = c."Product"
                                       AND b."TargetEnv" = c."TargetEnv");
                """);

            migrationBuilder.DropColumn(
                name: "Service",
                table: "work_item_comments");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "work_item_approvals");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "promotion_work_items");

            migrationBuilder.CreateIndex(
                name: "IX_work_item_comments_WorkItemKey_Product_TargetEnv_CreatedAt",
                table: "work_item_comments",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv",
                table: "work_item_approvals",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv_ApproverE~",
                table: "work_item_approvals",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv", "ApproverEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_work_items_WorkItemKey_Product_TargetEnv",
                table: "promotion_work_items",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv" });
        }
    }
}
