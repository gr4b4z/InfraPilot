using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class DropPromotionPolicyExecutor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutorConfigJson",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "ExecutorKind",
                table: "promotion_policies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutorConfigJson",
                table: "promotion_policies",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutorKind",
                table: "promotion_policies",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
