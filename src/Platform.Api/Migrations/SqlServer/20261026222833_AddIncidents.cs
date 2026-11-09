using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CausedByDeployEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "manual"),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedByName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_CausedByDeployEventId",
                table: "incidents",
                column: "CausedByDeployEventId");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Product_Service_Environment_StartedAt",
                table: "incidents",
                columns: new[] { "Product", "Service", "Environment", "StartedAt" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Source_ExternalId",
                table: "incidents",
                columns: new[] { "Source", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_StartedAt",
                table: "incidents",
                column: "StartedAt",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incidents");
        }
    }
}
