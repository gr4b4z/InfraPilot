using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddReleaseNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "release_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Environment = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    From = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    To = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RenderedContent = table.Column<string>(type: "text", nullable: false),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "published"),
                    ServicesCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_notes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_release_notes_GeneratedAt",
                table: "release_notes",
                column: "GeneratedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_release_notes_Product_Environment_GeneratedAt",
                table: "release_notes",
                columns: new[] { "Product", "Environment", "GeneratedAt" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_notes");
        }
    }
}
