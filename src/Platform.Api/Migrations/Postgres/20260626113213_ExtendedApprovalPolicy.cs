using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ExtendedApprovalPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the new rule-tree column first (defaults to "[]" ⇒ auto-approve) so we can
            // backfill it from the legacy single-group columns BEFORE dropping them.
            migrationBuilder.AddColumn<string>(
                name: "ApprovalStepsJson",
                table: "promotion_policies",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // Backfill (D17): wrap each legacy policy's ApproverGroup into a single step
            // "Release Approval" with one requirement (the group, minApprovers from the old
            // MinApprovers clamped to >= 1). Auto-approve policies (null/empty ApproverGroup)
            // keep the "[]" default. camelCase keys to match the JSON serializer options.
            migrationBuilder.Sql(@"
                UPDATE promotion_policies
                SET ""ApprovalStepsJson"" = json_build_array(
                        json_build_object(
                            'name', 'Release Approval',
                            'requirements', json_build_array(
                                json_build_object(
                                    'name', 'Approvers',
                                    'groups', json_build_array(""ApproverGroup""),
                                    'users', json_build_array(),
                                    'minApprovers', GREATEST(COALESCE(""MinApprovers"", 1), 1)
                                )
                            )
                        )
                    )::jsonb
                WHERE ""ApproverGroup"" IS NOT NULL AND ""ApproverGroup"" <> '';");

            migrationBuilder.DropColumn(
                name: "ApproverGroup",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "ExcludeRole",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "MinApprovers",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "Strategy",
                table: "promotion_policies");

            migrationBuilder.AddColumn<string>(
                name: "RequirementName",
                table: "promotion_approvals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StepName",
                table: "promotion_approvals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalStepsJson",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "RequirementName",
                table: "promotion_approvals");

            migrationBuilder.DropColumn(
                name: "StepName",
                table: "promotion_approvals");

            migrationBuilder.AddColumn<string>(
                name: "ApproverGroup",
                table: "promotion_policies",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExcludeRole",
                table: "promotion_policies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinApprovers",
                table: "promotion_policies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Strategy",
                table: "promotion_policies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");
        }
    }
}
