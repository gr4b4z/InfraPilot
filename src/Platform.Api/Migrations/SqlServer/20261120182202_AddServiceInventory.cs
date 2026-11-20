using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddServiceInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_cost_month",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Complete = table.Column<bool>(type: "bit", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Direct = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Shared = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SharedUnsplit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Unallocated = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Untagged = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    UnknownTag = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_cost_month", x => new { x.SnapshotId, x.Source, x.Currency });
                });

            migrationBuilder.CreateTable(
                name: "inventory_cost_service",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Product = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Team = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Direct = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Shared = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_cost_service", x => new { x.SnapshotId, x.ServiceId, x.Currency });
                });

            migrationBuilder.CreateTable(
                name: "inventory_cost_unallocated",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceGroup = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_cost_unallocated", x => new { x.SnapshotId, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "inventory_feeds",
                columns: table => new
                {
                    FeedKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    FeedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FeedType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false, defaultValue: ""),
                    CurrentSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastSourceVersion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    ProjectionVersion = table.Column<int>(type: "int", nullable: false),
                    Complete = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_feeds", x => x.FeedKey);
                });

            migrationBuilder.CreateTable(
                name: "inventory_services",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Product = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Team = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Lifecycle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_services", x => new { x.SnapshotId, x.ServiceId });
                });

            migrationBuilder.CreateTable(
                name: "inventory_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FeedType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false, defaultValue: ""),
                    SourceKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceLocation = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SourceVersion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentSha256 = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    DocumentVersion = table.Column<int>(type: "int", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Complete = table.Column<bool>(type: "bit", nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ContentLength = table.Column<int>(type: "int", nullable: false),
                    ContentGzip = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_cost_month_Period",
                table: "inventory_cost_month",
                column: "Period");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_cost_service_SnapshotId_Product",
                table: "inventory_cost_service",
                columns: new[] { "SnapshotId", "Product" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_cost_service_SnapshotId_Team",
                table: "inventory_cost_service",
                columns: new[] { "SnapshotId", "Team" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_cost_unallocated_SnapshotId_Currency_Amount",
                table: "inventory_cost_unallocated",
                columns: new[] { "SnapshotId", "Currency", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_feeds_FeedName_Period",
                table: "inventory_feeds",
                columns: new[] { "FeedName", "Period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_services_SnapshotId_Product",
                table: "inventory_services",
                columns: new[] { "SnapshotId", "Product" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_services_SnapshotId_Team",
                table: "inventory_services",
                columns: new[] { "SnapshotId", "Team" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_snapshots_FeedName_Period_ContentSha256",
                table: "inventory_snapshots",
                columns: new[] { "FeedName", "Period", "ContentSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_snapshots_FeedName_Period_FetchedAt",
                table: "inventory_snapshots",
                columns: new[] { "FeedName", "Period", "FetchedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_cost_month");

            migrationBuilder.DropTable(
                name: "inventory_cost_service");

            migrationBuilder.DropTable(
                name: "inventory_cost_unallocated");

            migrationBuilder.DropTable(
                name: "inventory_feeds");

            migrationBuilder.DropTable(
                name: "inventory_services");

            migrationBuilder.DropTable(
                name: "inventory_snapshots");
        }
    }
}
