using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddWorkItemCommentsAndBlockDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "work_item_approvals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "work_item_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetEnv = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AuthorEmail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_comments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_item_comments_WorkItemKey_Product_TargetEnv_CreatedAt",
                table: "work_item_comments",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_item_comments");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "work_item_approvals");
        }
    }
}
