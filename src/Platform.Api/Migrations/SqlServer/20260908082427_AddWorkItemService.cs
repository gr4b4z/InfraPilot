using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <summary>
    /// SQL Server counterpart of the Postgres <c>AddWorkItemService</c> — see that migration for the
    /// full rationale. Adds the service to a work item's identity
    /// (<c>(WorkItemKey, Product, Service, TargetEnv)</c>), backfills the ticket index from the parent
    /// candidate, and duplicates every legacy approval and comment once per service that carried the
    /// ticket, status as-is. The original row keeps its id on the alphabetically first service; copies
    /// get fresh ids. Rows no index row remembers keep a blank service.
    ///
    /// <para><b>Down</b> collapses the per-service rows back to the first service's before recreating
    /// the old unique index, losing anything recorded per service after this ran.</para>
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
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv_ApproverEmail",
                table: "work_item_approvals");

            migrationBuilder.DropIndex(
                name: "IX_promotion_work_items_WorkItemKey_Product_TargetEnv",
                table: "promotion_work_items");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "work_item_comments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "work_item_approvals",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "promotion_work_items",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // ── Backfill ──────────────────────────────────────────────────────────────────────

            // 1. The ticket index rows take the service of the candidate they hang off.
            migrationBuilder.Sql(
                """
                UPDATE w
                SET w.[Service] = c.[Service]
                FROM promotion_work_items w
                JOIN promotion_candidates c ON c.[Id] = w.[CandidateId];
                """);

            // 2. Approvals: one copy per ADDITIONAL service carrying the ticket in that product/env;
            //    the alphabetically first service is served by the original row, updated below.
            migrationBuilder.Sql(
                """
                INSERT INTO work_item_approvals
                    ([Id], [WorkItemKey], [Product], [Service], [TargetEnv], [ApproverEmail], [ApproverName],
                     [Decision], [Comment], [CreatedAt], [UpdatedAt])
                SELECT NEWID(), a.[WorkItemKey], a.[Product], s.[Service], a.[TargetEnv],
                       a.[ApproverEmail], a.[ApproverName], a.[Decision], a.[Comment], a.[CreatedAt], a.[UpdatedAt]
                FROM work_item_approvals a
                JOIN (SELECT DISTINCT [WorkItemKey], [Product], [TargetEnv], [Service]
                      FROM promotion_work_items
                      WHERE [Service] <> '') s
                  ON s.[WorkItemKey] = a.[WorkItemKey]
                 AND s.[Product] = a.[Product]
                 AND s.[TargetEnv] = a.[TargetEnv]
                WHERE a.[Service] = ''
                  AND s.[Service] <> (SELECT MIN(m.[Service]) FROM promotion_work_items m
                                      WHERE m.[WorkItemKey] = a.[WorkItemKey]
                                        AND m.[Product] = a.[Product]
                                        AND m.[TargetEnv] = a.[TargetEnv]
                                        AND m.[Service] <> '');
                """);

            migrationBuilder.Sql(
                """
                UPDATE a
                SET a.[Service] = (SELECT MIN(m.[Service]) FROM promotion_work_items m
                                   WHERE m.[WorkItemKey] = a.[WorkItemKey]
                                     AND m.[Product] = a.[Product]
                                     AND m.[TargetEnv] = a.[TargetEnv]
                                     AND m.[Service] <> '')
                FROM work_item_approvals a
                WHERE a.[Service] = ''
                  AND EXISTS (SELECT 1 FROM promotion_work_items m
                              WHERE m.[WorkItemKey] = a.[WorkItemKey]
                                AND m.[Product] = a.[Product]
                                AND m.[TargetEnv] = a.[TargetEnv]
                                AND m.[Service] <> '');
                """);

            // 3. Comment threads, the same way.
            migrationBuilder.Sql(
                """
                INSERT INTO work_item_comments
                    ([Id], [WorkItemKey], [Product], [Service], [TargetEnv], [AuthorEmail], [AuthorName],
                     [Body], [Decision], [CreatedAt], [UpdatedAt])
                SELECT NEWID(), c.[WorkItemKey], c.[Product], s.[Service], c.[TargetEnv],
                       c.[AuthorEmail], c.[AuthorName], c.[Body], c.[Decision], c.[CreatedAt], c.[UpdatedAt]
                FROM work_item_comments c
                JOIN (SELECT DISTINCT [WorkItemKey], [Product], [TargetEnv], [Service]
                      FROM promotion_work_items
                      WHERE [Service] <> '') s
                  ON s.[WorkItemKey] = c.[WorkItemKey]
                 AND s.[Product] = c.[Product]
                 AND s.[TargetEnv] = c.[TargetEnv]
                WHERE c.[Service] = ''
                  AND s.[Service] <> (SELECT MIN(m.[Service]) FROM promotion_work_items m
                                      WHERE m.[WorkItemKey] = c.[WorkItemKey]
                                        AND m.[Product] = c.[Product]
                                        AND m.[TargetEnv] = c.[TargetEnv]
                                        AND m.[Service] <> '');
                """);

            migrationBuilder.Sql(
                """
                UPDATE c
                SET c.[Service] = (SELECT MIN(m.[Service]) FROM promotion_work_items m
                                   WHERE m.[WorkItemKey] = c.[WorkItemKey]
                                     AND m.[Product] = c.[Product]
                                     AND m.[TargetEnv] = c.[TargetEnv]
                                     AND m.[Service] <> '')
                FROM work_item_comments c
                WHERE c.[Service] = ''
                  AND EXISTS (SELECT 1 FROM promotion_work_items m
                              WHERE m.[WorkItemKey] = c.[WorkItemKey]
                                AND m.[Product] = c.[Product]
                                AND m.[TargetEnv] = c.[TargetEnv]
                                AND m.[Service] <> '');
                """);

            // ── Indexes on the widened key ────────────────────────────────────────────────────

            migrationBuilder.CreateIndex(
                name: "IX_work_item_comments_WorkItemKey_Product_Service_TargetEnv_CreatedAt",
                table: "work_item_comments",
                columns: new[] { "WorkItemKey", "Product", "Service", "TargetEnv", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv",
                table: "work_item_approvals",
                columns: new[] { "WorkItemKey", "Product", "Service", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv_ApproverEmail",
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
                name: "IX_work_item_comments_WorkItemKey_Product_Service_TargetEnv_CreatedAt",
                table: "work_item_comments");

            migrationBuilder.DropIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv",
                table: "work_item_approvals");

            migrationBuilder.DropIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_Service_TargetEnv_ApproverEmail",
                table: "work_item_approvals");

            migrationBuilder.DropIndex(
                name: "IX_promotion_work_items_WorkItemKey_Product_TargetEnv_Service",
                table: "promotion_work_items");

            // Collapse per-service instances to the first service's rows — see the class summary.
            migrationBuilder.Sql(
                """
                DELETE a
                FROM work_item_approvals a
                WHERE a.[Service] > (SELECT MIN(b.[Service]) FROM work_item_approvals b
                                     WHERE b.[WorkItemKey] = a.[WorkItemKey]
                                       AND b.[Product] = a.[Product]
                                       AND b.[TargetEnv] = a.[TargetEnv]
                                       AND b.[ApproverEmail] = a.[ApproverEmail]);
                """);
            migrationBuilder.Sql(
                """
                DELETE c
                FROM work_item_comments c
                WHERE c.[Service] > (SELECT MIN(b.[Service]) FROM work_item_comments b
                                     WHERE b.[WorkItemKey] = c.[WorkItemKey]
                                       AND b.[Product] = c.[Product]
                                       AND b.[TargetEnv] = c.[TargetEnv]);
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
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv_ApproverEmail",
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
