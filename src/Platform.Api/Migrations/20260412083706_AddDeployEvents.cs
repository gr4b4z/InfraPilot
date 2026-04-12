using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeployEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequesterEmail",
                table: "service_requests",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "external_ticket_key",
                table: "service_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_ticket_url",
                table: "service_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deploy_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Environment = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PreviousVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DeployedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReferencesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    ParticipantsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    EnrichmentJson = table.Column<string>(type: "jsonb", nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deploy_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deploy_events_Environment_DeployedAt",
                table: "deploy_events",
                columns: new[] { "Environment", "DeployedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_deploy_events_Product",
                table: "deploy_events",
                column: "Product");

            migrationBuilder.CreateIndex(
                name: "IX_deploy_events_Product_Service_Environment_DeployedAt",
                table: "deploy_events",
                columns: new[] { "Product", "Service", "Environment", "DeployedAt" },
                descending: new[] { false, false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deploy_events");

            migrationBuilder.DropColumn(
                name: "RequesterEmail",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "external_ticket_key",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "external_ticket_url",
                table: "service_requests");
        }
    }
}
