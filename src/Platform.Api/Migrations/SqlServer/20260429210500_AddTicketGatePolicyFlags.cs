using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddTicketGatePolicyFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveOnAllTicketsApproved",
                table: "promotion_policies",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveWhenNoTickets",
                table: "promotion_policies",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireAllTicketsApproved",
                table: "promotion_policies",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoApproveOnAllTicketsApproved",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "AutoApproveWhenNoTickets",
                table: "promotion_policies");

            migrationBuilder.DropColumn(
                name: "RequireAllTicketsApproved",
                table: "promotion_policies");
        }
    }
}
