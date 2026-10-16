using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddPolicyBuildAutoCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovedWebhookDelaySeconds",
                table: "promotion_policies",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoCreateFromBranchesJson",
                table: "promotion_policies",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedWebhookDelaySeconds",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "AutoCreateFromBranchesJson",
                table: "promotion_policies");
        }
    }
}
