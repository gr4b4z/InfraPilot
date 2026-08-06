using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddWebhookDeliveryCancelKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelKey",
                table: "webhook_deliveries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_CancelKey",
                table: "webhook_deliveries",
                column: "CancelKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_webhook_deliveries_CancelKey",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "CancelKey",
                table: "webhook_deliveries");
        }
    }
}
