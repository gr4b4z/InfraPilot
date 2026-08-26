using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddBuildRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "builds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Branch = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BuildId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BuildUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ManifestJson = table.Column<string>(type: "jsonb", nullable: true),
                    ArtifactRef = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ArtifactDigest = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_builds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_builds_Branch",
                table: "builds",
                column: "Branch");

            migrationBuilder.CreateIndex(
                name: "IX_builds_Product_Service_CreatedAt",
                table: "builds",
                columns: new[] { "Product", "Service", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_builds_Product_Service_Version",
                table: "builds",
                columns: new[] { "Product", "Service", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "builds");
        }
    }
}
