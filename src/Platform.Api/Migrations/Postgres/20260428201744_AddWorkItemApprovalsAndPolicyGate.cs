using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddWorkItemApprovalsAndPolicyGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Gate",
                table: "promotion_policies",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "PromotionOnly");

            migrationBuilder.CreateTable(
                name: "work_item_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetEnv = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApproverEmail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_approvals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_Product_TargetEnv",
                table: "work_item_approvals",
                columns: new[] { "Product", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv",
                table: "work_item_approvals",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_approvals_WorkItemKey_Product_TargetEnv_ApproverE~",
                table: "work_item_approvals",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv", "ApproverEmail" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_item_approvals");

            migrationBuilder.DropColumn(
                name: "Gate",
                table: "promotion_policies");
        }
    }
}
