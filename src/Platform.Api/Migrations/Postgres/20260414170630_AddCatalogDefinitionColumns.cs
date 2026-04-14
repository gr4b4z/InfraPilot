using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCatalogDefinitionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalJson",
                table: "catalog_items",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutorJson",
                table: "catalog_items",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputsJson",
                table: "catalog_items",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ValidationsJson",
                table: "catalog_items",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalJson",
                table: "catalog_items");

            migrationBuilder.DropColumn(
                name: "ExecutorJson",
                table: "catalog_items");

            migrationBuilder.DropColumn(
                name: "InputsJson",
                table: "catalog_items");

            migrationBuilder.DropColumn(
                name: "ValidationsJson",
                table: "catalog_items");
        }
    }
}
