using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddWebhookTargetType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitHubEventType",
                table: "webhook_subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatureHeader",
                table: "webhook_subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetType",
                table: "webhook_subscriptions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "generic");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitHubEventType",
                table: "webhook_subscriptions");

            migrationBuilder.DropColumn(
                name: "SignatureHeader",
                table: "webhook_subscriptions");

            migrationBuilder.DropColumn(
                name: "TargetType",
                table: "webhook_subscriptions");
        }
    }
}
