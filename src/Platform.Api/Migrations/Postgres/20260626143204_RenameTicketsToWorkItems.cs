using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RenameTicketsToWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RequireAllTicketsApproved",
                table: "promotion_policies",
                newName: "RequireAllWorkItemsApproved");

            migrationBuilder.RenameColumn(
                name: "AutoApproveWhenNoTickets",
                table: "promotion_policies",
                newName: "AutoApproveWhenNoWorkItems");

            migrationBuilder.RenameColumn(
                name: "AutoApproveOnAllTicketsApproved",
                table: "promotion_policies",
                newName: "AutoApproveOnAllWorkItemsApproved");

            // The Gate enum is persisted as a string; rename the legacy values in existing rows.
            migrationBuilder.Sql(
                "UPDATE promotion_policies SET \"Gate\" = 'WorkItemsOnly' WHERE \"Gate\" = 'TicketsOnly';");
            migrationBuilder.Sql(
                "UPDATE promotion_policies SET \"Gate\" = 'WorkItemsAndManual' WHERE \"Gate\" = 'TicketsAndManual';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE promotion_policies SET \"Gate\" = 'TicketsOnly' WHERE \"Gate\" = 'WorkItemsOnly';");
            migrationBuilder.Sql(
                "UPDATE promotion_policies SET \"Gate\" = 'TicketsAndManual' WHERE \"Gate\" = 'WorkItemsAndManual';");

            migrationBuilder.RenameColumn(
                name: "RequireAllWorkItemsApproved",
                table: "promotion_policies",
                newName: "RequireAllTicketsApproved");

            migrationBuilder.RenameColumn(
                name: "AutoApproveWhenNoWorkItems",
                table: "promotion_policies",
                newName: "AutoApproveWhenNoTickets");

            migrationBuilder.RenameColumn(
                name: "AutoApproveOnAllWorkItemsApproved",
                table: "promotion_policies",
                newName: "AutoApproveOnAllTicketsApproved");
        }
    }
}
