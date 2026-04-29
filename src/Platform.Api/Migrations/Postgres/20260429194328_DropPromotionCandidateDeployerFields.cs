using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class DropPromotionCandidateDeployerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceDeployerEmail",
                table: "promotion_candidates");

            migrationBuilder.DropColumn(
                name: "SourceDeployerName",
                table: "promotion_candidates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceDeployerEmail",
                table: "promotion_candidates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDeployerName",
                table: "promotion_candidates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }
    }
}
