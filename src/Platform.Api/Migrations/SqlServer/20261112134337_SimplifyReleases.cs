using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class SimplifyReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludedServicesJson",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "ReleaseManager",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "ReleaseManagerName",
                table: "releases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IncludedServicesJson",
                table: "releases",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ReleaseManager",
                table: "releases",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseManagerName",
                table: "releases",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }
    }
}
