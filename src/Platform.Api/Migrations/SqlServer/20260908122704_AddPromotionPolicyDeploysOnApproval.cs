using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <summary>
    /// SQL Server counterpart of the Postgres <c>AddPromotionPolicyDeploysOnApproval</c> — see that
    /// migration for the full rationale. Adds the <c>DeploysOnApproval</c> policy column (default
    /// <c>true</c>: an approval releases the deploy on its own) and seeds <c>false</c> on the
    /// <c>mpt</c> product's staging and prod edges, which the SDP pipeline releases behind its own
    /// Azure DevOps environment approvals.
    /// </summary>
    public partial class AddPromotionPolicyDeploysOnApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeploysOnApproval",
                table: "promotion_policies",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                """
                UPDATE promotion_policies
                SET [DeploysOnApproval] = 0
                WHERE LOWER([Product]) = 'mpt'
                  AND LOWER([TargetEnv]) IN ('staging', 'stage', 'prod', 'production');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the column takes the seeded values with it.
            migrationBuilder.DropColumn(
                name: "DeploysOnApproval",
                table: "promotion_policies");
        }
    }
}
