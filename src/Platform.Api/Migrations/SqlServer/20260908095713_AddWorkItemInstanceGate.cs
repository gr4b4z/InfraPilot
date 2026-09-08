using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <summary>
    /// Adds <c>RequireAllWorkItemInstancesApproved</c> to promotion policies: whether the work-item
    /// gate judges a ticket by this promotion's own service instance (the default, <c>false</c>) or by
    /// the ticket's overall status across every service carrying it in the target environment. Schema
    /// only — existing policies keep the per-service gate.
    /// </summary>
    public partial class AddWorkItemInstanceGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireAllWorkItemInstancesApproved",
                table: "promotion_policies",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireAllWorkItemInstancesApproved",
                table: "promotion_policies");
        }
    }
}
