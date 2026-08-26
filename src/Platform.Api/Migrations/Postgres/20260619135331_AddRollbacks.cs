using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRollbacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rollback_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetEnv = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReferenceEnv = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExclusionsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedPolicyJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollback_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rollback_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApproverEmail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollback_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rollback_approvals_rollback_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "rollback_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rollback_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ToVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CompletedDeployEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalRunUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollback_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rollback_items_rollback_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "rollback_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rollback_approvals_RequestId_ApproverEmail",
                table: "rollback_approvals",
                columns: new[] { "RequestId", "ApproverEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rollback_items_RequestId",
                table: "rollback_items",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_rollback_items_Service_Status",
                table: "rollback_items",
                columns: new[] { "Service", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_rollback_requests_Product_TargetEnv",
                table: "rollback_requests",
                columns: new[] { "Product", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_rollback_requests_Status",
                table: "rollback_requests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rollback_approvals");

            migrationBuilder.DropTable(
                name: "rollback_items");

            migrationBuilder.DropTable(
                name: "rollback_requests");
        }
    }
}
