using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class PromotionPolicySourceEnv : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_promotion_policies_Product_Service_TargetEnv",
                table: "promotion_policies");

            migrationBuilder.AddColumn<string>(
                name: "SourceEnv",
                table: "promotion_policies",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_policies_Product_Service_SourceEnv_TargetEnv",
                table: "promotion_policies",
                columns: new[] { "Product", "Service", "SourceEnv", "TargetEnv" },
                unique: true,
                filter: "[Service] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_promotion_policies_Product_Service_SourceEnv_TargetEnv",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "SourceEnv",
                table: "promotion_policies");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_policies_Product_Service_TargetEnv",
                table: "promotion_policies",
                columns: new[] { "Product", "Service", "TargetEnv" },
                unique: true,
                filter: "[Service] IS NOT NULL");
        }
    }
}
