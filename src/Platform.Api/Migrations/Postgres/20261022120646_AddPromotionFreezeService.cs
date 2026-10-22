using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddPromotionFreezeService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_promotion_freezes_Product_TargetEnv",
                table: "promotion_freezes");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "promotion_freezes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_freezes_Product_Service_TargetEnv",
                table: "promotion_freezes",
                columns: new[] { "Product", "Service", "TargetEnv" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_promotion_freezes_Product_Service_TargetEnv",
                table: "promotion_freezes");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "promotion_freezes");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_freezes_Product_TargetEnv",
                table: "promotion_freezes",
                columns: new[] { "Product", "TargetEnv" },
                unique: true);
        }
    }
}
