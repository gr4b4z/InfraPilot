using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddReferenceParticipantOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reference_participant_overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeployEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferenceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AssigneeEmail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AssigneeDisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AssignedById = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AssignedByName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reference_participant_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reference_participant_overrides_deploy_events_DeployEventId",
                        column: x => x.DeployEventId,
                        principalTable: "deploy_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reference_participant_overrides_DeployEventId",
                table: "reference_participant_overrides",
                column: "DeployEventId");

            migrationBuilder.CreateIndex(
                name: "IX_reference_participant_overrides_DeployEventId_ReferenceKey_~",
                table: "reference_participant_overrides",
                columns: new[] { "DeployEventId", "ReferenceKey", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reference_participant_overrides");
        }
    }
}
