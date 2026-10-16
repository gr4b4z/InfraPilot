using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddDeploymentRunAndLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunJson",
                table: "deploy_events",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deploy_event_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeployEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Truncated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ByteCount = table.Column<int>(type: "int", nullable: false),
                    LineCount = table.Column<int>(type: "int", nullable: false),
                    OriginalByteCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deploy_event_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deploy_event_logs_deploy_events_DeployEventId",
                        column: x => x.DeployEventId,
                        principalTable: "deploy_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deploy_event_logs_DeployEventId",
                table: "deploy_event_logs",
                column: "DeployEventId");

            migrationBuilder.CreateIndex(
                name: "IX_deploy_event_logs_DeployEventId_Name",
                table: "deploy_event_logs",
                columns: new[] { "DeployEventId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deploy_event_logs");

            migrationBuilder.DropColumn(
                name: "RunJson",
                table: "deploy_events");
        }
    }
}
