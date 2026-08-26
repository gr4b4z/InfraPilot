using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ExternalPromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TODO(D19): the dormant `promotions.topology` row in platform_settings is now unused
            // (topology removed). Left in place — clean it up with a dedicated data migration if
            // desired. Not deleted here to avoid baking a data-mutation into a schema migration.

            migrationBuilder.DropIndex(
                name: "IX_promotion_candidates_Product_Service_SourceEnv_TargetEnv",
                table: "promotion_candidates");

            migrationBuilder.DropIndex(
                name: "IX_promotion_candidates_SourceDeployEventId",
                table: "promotion_candidates");

            migrationBuilder.DropColumn(
                name: "SourceDeployEventId",
                table: "promotion_candidates");

            // The candidate's bundle representation changes entirely: the old event-id list
            // (SupersededSourceEventIdsJson) is dropped and a fresh, self-contained ReferencesJson
            // column is added (default "[]"). These hold DIFFERENT data — a rename would carry
            // GUID arrays into a column that expects ReferenceDto objects and corrupt every
            // existing candidate — so drop + add, not rename.
            migrationBuilder.DropColumn(
                name: "SupersededSourceEventIdsJson",
                table: "promotion_candidates");

            migrationBuilder.AddColumn<string>(
                name: "ReferencesJson",
                table: "promotion_candidates",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "FromRevision",
                table: "promotion_candidates",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToRevision",
                table: "promotion_candidates",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "promotion_work_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Product = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetEnv = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Revision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_work_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_promotion_work_items_promotion_candidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "promotion_candidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_candidates_Product_Service_SourceEnv_TargetEnv_Ve~",
                table: "promotion_candidates",
                columns: new[] { "Product", "Service", "SourceEnv", "TargetEnv", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_work_items_CandidateId",
                table: "promotion_work_items",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_work_items_WorkItemKey_Product_TargetEnv",
                table: "promotion_work_items",
                columns: new[] { "WorkItemKey", "Product", "TargetEnv" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_work_items");

            migrationBuilder.DropIndex(
                name: "IX_promotion_candidates_Product_Service_SourceEnv_TargetEnv_Ve~",
                table: "promotion_candidates");

            migrationBuilder.DropColumn(
                name: "FromRevision",
                table: "promotion_candidates");

            migrationBuilder.DropColumn(
                name: "ToRevision",
                table: "promotion_candidates");

            migrationBuilder.DropColumn(
                name: "ReferencesJson",
                table: "promotion_candidates");

            migrationBuilder.AddColumn<string>(
                name: "SupersededSourceEventIdsJson",
                table: "promotion_candidates",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceDeployEventId",
                table: "promotion_candidates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_promotion_candidates_Product_Service_SourceEnv_TargetEnv",
                table: "promotion_candidates",
                columns: new[] { "Product", "Service", "SourceEnv", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_candidates_SourceDeployEventId",
                table: "promotion_candidates",
                column: "SourceDeployEventId");
        }
    }
}
