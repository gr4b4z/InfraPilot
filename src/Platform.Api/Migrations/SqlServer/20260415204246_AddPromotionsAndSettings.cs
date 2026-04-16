using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddPromotionsAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "promotion_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceEnv = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetEnv = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceDeployEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceDeployerEmail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedPolicyJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExternalRunUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeployedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SupersededById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_candidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "promotion_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TargetEnv = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ApproverGroup = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Strategy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MinApprovers = table.Column<int>(type: "int", nullable: false),
                    ExcludeDeployer = table.Column<bool>(type: "bit", nullable: false),
                    TimeoutHours = table.Column<int>(type: "int", nullable: false),
                    EscalationGroup = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "promotion_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApproverEmail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ApproverName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_promotion_approvals_promotion_candidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "promotion_candidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_approvals_CandidateId_ApproverEmail",
                table: "promotion_approvals",
                columns: new[] { "CandidateId", "ApproverEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_candidates_Product_Service_SourceEnv_TargetEnv",
                table: "promotion_candidates",
                columns: new[] { "Product", "Service", "SourceEnv", "TargetEnv" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_candidates_SourceDeployEventId",
                table: "promotion_candidates",
                column: "SourceDeployEventId");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_candidates_Status",
                table: "promotion_candidates",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_policies_Product_Service_TargetEnv",
                table: "promotion_policies",
                columns: new[] { "Product", "Service", "TargetEnv" },
                unique: true,
                filter: "[Service] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_policies_Product_TargetEnv",
                table: "promotion_policies",
                columns: new[] { "Product", "TargetEnv" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_settings");

            migrationBuilder.DropTable(
                name: "promotion_approvals");

            migrationBuilder.DropTable(
                name: "promotion_policies");

            migrationBuilder.DropTable(
                name: "promotion_candidates");
        }
    }
}
