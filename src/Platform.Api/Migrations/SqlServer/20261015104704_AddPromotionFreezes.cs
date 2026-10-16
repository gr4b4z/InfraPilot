using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddPromotionFreezes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "promotion_freezes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetEnv = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    FrozenBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FrozenByEmail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_freezes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_freezes_Product_TargetEnv",
                table: "promotion_freezes",
                columns: new[] { "Product", "TargetEnv" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_freezes");
        }
    }
}
