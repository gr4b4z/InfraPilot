using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <summary>
    /// Turns the single-valued webhook product/environment filters into sets, and adds the service
    /// dimension the deploy and promotion events always carried but nothing could filter on.
    ///
    /// <para>Add-backfill-drop rather than the scaffolded drop-then-add: an existing subscription's
    /// filter is the difference between a receiver getting its own product's events and getting
    /// everybody's, so the old value is folded into the new set before the column goes.</para>
    /// </summary>
    public partial class AddWebhookFilterSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilterProductsJson",
                table: "webhook_subscriptions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "FilterServicesJson",
                table: "webhook_subscriptions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "FilterEnvironmentsJson",
                table: "webhook_subscriptions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            // STRING_ESCAPE rather than plain concatenation: a product name is unlikely to contain a
            // quote or a backslash, but a filter silently turning into invalid JSON is not a failure
            // mode worth leaving open.
            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET [FilterProductsJson] = '["' + STRING_ESCAPE([FilterProduct], 'json') + '"]'
                WHERE [FilterProduct] IS NOT NULL AND LTRIM(RTRIM([FilterProduct])) <> '';
                """);

            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET [FilterEnvironmentsJson] = '["' + STRING_ESCAPE([FilterEnvironment], 'json') + '"]'
                WHERE [FilterEnvironment] IS NOT NULL AND LTRIM(RTRIM([FilterEnvironment])) <> '';
                """);

            migrationBuilder.DropColumn(
                name: "FilterProduct",
                table: "webhook_subscriptions");

            migrationBuilder.DropColumn(
                name: "FilterEnvironment",
                table: "webhook_subscriptions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilterProduct",
                table: "webhook_subscriptions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilterEnvironment",
                table: "webhook_subscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // A set of many cannot round-trip into a column of one: the first value survives and the
            // rest widen back to "any". Nothing to be done about that — it is what going back means.
            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET [FilterProduct] = JSON_VALUE([FilterProductsJson], '$[0]')
                WHERE ISJSON([FilterProductsJson]) = 1;
                """);

            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET [FilterEnvironment] = JSON_VALUE([FilterEnvironmentsJson], '$[0]')
                WHERE ISJSON([FilterEnvironmentsJson]) = 1;
                """);

            migrationBuilder.DropColumn(
                name: "FilterProductsJson",
                table: "webhook_subscriptions");

            migrationBuilder.DropColumn(
                name: "FilterServicesJson",
                table: "webhook_subscriptions");

            migrationBuilder.DropColumn(
                name: "FilterEnvironmentsJson",
                table: "webhook_subscriptions");
        }
    }
}
