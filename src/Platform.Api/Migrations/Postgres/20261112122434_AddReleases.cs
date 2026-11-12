using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "releases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProductsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, defaultValue: "[]"),
                    StagingEnvironment = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProductionEnvironment = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReleaseManager = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ReleaseManagerName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    LinksJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    IncludedServicesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false, defaultValue: "[]"),
                    ExcludedServicesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false, defaultValue: "[]"),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_releases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "release_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Check = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CompletedByName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    LinksJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    Custom = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_release_steps_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_release_steps_ReleaseId_Order",
                table: "release_steps",
                columns: new[] { "ReleaseId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_releases_Status_CreatedAt",
                table: "releases",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_releases_Version",
                table: "releases",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_steps");

            migrationBuilder.DropTable(
                name: "releases");
        }
    }
}
