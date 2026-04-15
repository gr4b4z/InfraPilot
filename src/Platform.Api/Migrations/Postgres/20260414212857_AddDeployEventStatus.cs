using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddDeployEventStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "deploy_events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "succeeded");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "deploy_events");
        }
    }
}
