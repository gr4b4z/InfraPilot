using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <summary>
    /// Adds <c>DeploysOnApproval</c> to promotion policies: whether an approval on the edge is the
    /// last gate before the version goes live (the default, <c>true</c>) or whether the deployment
    /// run it starts stops at an approval outside InfraPortal. Display only — the promotion page uses
    /// it to tell an approver which of the two Approve means.
    ///
    /// <para>The column defaults to <c>true</c>, so every existing edge reads as the automatic path —
    /// what they all did before the flag existed. One data seed corrects the exception this was added
    /// for: the <c>mpt</c> product's staging and prod edges are released by the SDP pipeline
    /// (the marketplace repo), whose per-environment stages each wait on an Azure DevOps environment
    /// check, so an InfraPortal approval there is necessary but not sufficient. Every other product
    /// (<c>mpt-extensions</c> and the rest of the <c>mpt-*</c> family) is released by mpt-release,
    /// which deploys straight off the approval.</para>
    ///
    /// <para>A seed, not a rule: the flag is per edge and admin-editable in Settings → Promotions, so
    /// a product that changes how it releases is corrected there rather than in another migration.</para>
    /// </summary>
    public partial class AddPromotionPolicyDeploysOnApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeploysOnApproval",
                table: "promotion_policies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Environment names are canonicalised on write, but the aliases an admin may have made
            // canonical are not, so match the spellings the same environment answers to.
            migrationBuilder.Sql(
                """
                UPDATE promotion_policies
                SET "DeploysOnApproval" = FALSE
                WHERE lower("Product") = 'mpt'
                  AND lower("TargetEnv") IN ('staging', 'stage', 'prod', 'production');
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
