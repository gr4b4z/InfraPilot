using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddPromotionPolicyExecutor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutorConfigJson",
                table: "promotion_policies",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutorKind",
                table: "promotion_policies",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutorConfigJson",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "ExecutorKind",
                table: "promotion_policies");
        }
    }
}
