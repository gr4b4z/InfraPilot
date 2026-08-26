using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ReplaceExcludeDeployerWithExcludeRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the replacement column first so we can carry the semantics across.
            migrationBuilder.AddColumn<string>(
                name: "ExcludeRole",
                table: "promotion_policies",
                type: "text",
                nullable: true);

            // Preserve existing intent: ExcludeDeployer=true becomes ExcludeRole='triggered-by'
            // (the canonical role the old bool implicitly referred to); false becomes NULL.
            migrationBuilder.Sql(@"
                UPDATE promotion_policies
                SET ""ExcludeRole"" = CASE WHEN ""ExcludeDeployer"" = TRUE THEN 'triggered-by' ELSE NULL END;
            ");

            migrationBuilder.DropColumn(
                name: "ExcludeDeployer",
                table: "promotion_policies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeDeployer",
                table: "promotion_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Best-effort downgrade: anyone with ExcludeRole set at all is treated as true.
            // The original might have pointed at a non-triggered-by role which we can't express
            // in a bool; losing that distinction is acceptable since Down is dev-only.
            migrationBuilder.Sql(@"
                UPDATE promotion_policies
                SET ""ExcludeDeployer"" = CASE WHEN ""ExcludeRole"" IS NOT NULL AND ""ExcludeRole"" <> '' THEN TRUE ELSE FALSE END;
            ");

            migrationBuilder.DropColumn(
                name: "ExcludeRole",
                table: "promotion_policies");
        }
    }
}
