using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddRetiredVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "retired_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetiredBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retired_versions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_retired_versions_Product_Service_Version",
                table: "retired_versions",
                columns: new[] { "Product", "Service", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retired_versions_RetiredAt",
                table: "retired_versions",
                column: "RetiredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retired_versions");
        }
    }
}
