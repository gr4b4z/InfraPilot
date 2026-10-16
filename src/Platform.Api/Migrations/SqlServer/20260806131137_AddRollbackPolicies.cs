using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddRollbackPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ApprovalOverridden",
                table: "rollback_requests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOverride",
                table: "rollback_approvals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "rollback_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetEnv = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatorsJson = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "{}"),
                    ApprovalStepsJson = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "[]"),
                    EscalationGroup = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollback_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rollback_policies_Product_TargetEnv",
                table: "rollback_policies",
                columns: new[] { "Product", "TargetEnv" },
                unique: true,
                filter: "[TargetEnv] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rollback_policies");

            migrationBuilder.DropColumn(
                name: "ApprovalOverridden",
                table: "rollback_requests");

            migrationBuilder.DropColumn(
                name: "IsOverride",
                table: "rollback_approvals");
        }
    }
}
