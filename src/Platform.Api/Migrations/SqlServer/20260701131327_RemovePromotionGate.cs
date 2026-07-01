using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class RemovePromotionGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Gate",
                table: "promotion_policies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Gate",
                table: "promotion_policies",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "PromotionOnly");
        }
    }
}
