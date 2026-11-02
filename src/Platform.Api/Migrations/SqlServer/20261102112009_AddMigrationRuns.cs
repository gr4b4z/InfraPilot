using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddMigrationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RunKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DeployEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    SkipReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SupersededById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RunJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppliedCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    TotalCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_migration_runs_deploy_events_DeployEventId",
                        column: x => x.DeployEventId,
                        principalTable: "deploy_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "migration_run_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MigrationRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_run_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_migration_run_items_migration_runs_MigrationRunId",
                        column: x => x.MigrationRunId,
                        principalTable: "migration_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_run_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MigrationRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_migration_run_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_migration_run_logs_migration_runs_MigrationRunId",
                        column: x => x.MigrationRunId,
                        principalTable: "migration_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_migration_run_items_MigrationRunId_Sequence",
                table: "migration_run_items",
                columns: new[] { "MigrationRunId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_migration_run_items_Name",
                table: "migration_run_items",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_migration_run_items_Order",
                table: "migration_run_items",
                column: "Order");

            migrationBuilder.CreateIndex(
                name: "IX_migration_run_logs_MigrationRunId_Name",
                table: "migration_run_logs",
                columns: new[] { "MigrationRunId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_migration_runs_DeployEventId",
                table: "migration_runs",
                column: "DeployEventId");

            migrationBuilder.CreateIndex(
                name: "IX_migration_runs_Kind_Product_Service_Environment_Version_RunKey",
                table: "migration_runs",
                columns: new[] { "Kind", "Product", "Service", "Environment", "Version", "RunKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_migration_runs_Product_Service_Environment_QueuedAt",
                table: "migration_runs",
                columns: new[] { "Product", "Service", "Environment", "QueuedAt" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_migration_runs_QueuedAt",
                table: "migration_runs",
                column: "QueuedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_migration_runs_Status_Product",
                table: "migration_runs",
                columns: new[] { "Status", "Product" });

            migrationBuilder.CreateIndex(
                name: "IX_migration_runs_SupersededById",
                table: "migration_runs",
                column: "SupersededById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "migration_run_items");

            migrationBuilder.DropTable(
                name: "migration_run_logs");

            migrationBuilder.DropTable(
                name: "migration_runs");
        }
    }
}
