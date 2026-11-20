using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
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
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Complete = table.Column<bool>(type: "boolean", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Direct = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Shared = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    SharedUnsplit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Unallocated = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Untagged = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    UnknownTag = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_cost_month", x => new { x.SnapshotId, x.Source, x.Currency });
                });

            migrationBuilder.CreateTable(
                name: "inventory_cost_service",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Product = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Team = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Direct = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Shared = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_cost_service", x => new { x.SnapshotId, x.ServiceId, x.Currency });
                });

            migrationBuilder.CreateTable(
                name: "inventory_cost_unallocated",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResourceGroup = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_cost_unallocated", x => new { x.SnapshotId, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "inventory_feeds",
                columns: table => new
                {
                    FeedKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FeedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FeedType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false, defaultValue: ""),
                    CurrentSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastSourceVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    ProjectionVersion = table.Column<int>(type: "integer", nullable: false),
                    Complete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_feeds", x => x.FeedKey);
                });

            migrationBuilder.CreateTable(
                name: "inventory_services",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Product = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Team = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Lifecycle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_services", x => new { x.SnapshotId, x.ServiceId });
                });

            migrationBuilder.CreateTable(
                name: "inventory_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FeedType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false, defaultValue: ""),
                    SourceKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceLocation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    DocumentVersion = table.Column<int>(type: "integer", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Complete = table.Column<bool>(type: "boolean", nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContentLength = table.Column<int>(type: "integer", nullable: false),
                    ContentGzip = table.Column<byte[]>(type: "bytea", nullable: false)
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
