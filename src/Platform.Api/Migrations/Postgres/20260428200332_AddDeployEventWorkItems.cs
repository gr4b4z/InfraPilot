using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddDeployEventWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deploy_event_work_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeployEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Revision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deploy_event_work_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deploy_event_work_items_deploy_events_DeployEventId",
                        column: x => x.DeployEventId,
                        principalTable: "deploy_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deploy_event_work_items_DeployEventId_WorkItemKey",
                table: "deploy_event_work_items",
                columns: new[] { "DeployEventId", "WorkItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deploy_event_work_items_Product",
                table: "deploy_event_work_items",
                column: "Product");

            migrationBuilder.CreateIndex(
                name: "IX_deploy_event_work_items_WorkItemKey_Product",
                table: "deploy_event_work_items",
                columns: new[] { "WorkItemKey", "Product" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deploy_event_work_items");
        }
    }
}
