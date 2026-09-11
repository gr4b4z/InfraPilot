using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class PromotionApprovalUniquePerRequirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_promotion_approvals_CandidateId_ApproverEmail",
                table: "promotion_approvals");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_approvals_CandidateId_ApproverEmail_StepName_RequirementName",
                table: "promotion_approvals",
                columns: new[] { "CandidateId", "ApproverEmail", "StepName", "RequirementName" },
                unique: true,
                filter: "[StepName] IS NOT NULL AND [RequirementName] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_promotion_approvals_CandidateId_ApproverEmail_StepName_RequirementName",
                table: "promotion_approvals");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_approvals_CandidateId_ApproverEmail",
                table: "promotion_approvals",
                columns: new[] { "CandidateId", "ApproverEmail" },
                unique: true);
        }
    }
}
