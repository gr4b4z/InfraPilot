using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
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
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "FilterServicesJson",
                table: "webhook_subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "FilterEnvironmentsJson",
                table: "webhook_subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET "FilterProductsJson" = jsonb_build_array("FilterProduct")
                WHERE "FilterProduct" IS NOT NULL AND btrim("FilterProduct") <> '';
                """);

            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET "FilterEnvironmentsJson" = jsonb_build_array("FilterEnvironment")
                WHERE "FilterEnvironment" IS NOT NULL AND btrim("FilterEnvironment") <> '';
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
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilterEnvironment",
                table: "webhook_subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // A set of many cannot round-trip into a column of one: the first value survives and the
            // rest widen back to "any". Nothing to be done about that — it is what going back means.
            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET "FilterProduct" = "FilterProductsJson" ->> 0
                WHERE jsonb_typeof("FilterProductsJson") = 'array'
                  AND jsonb_array_length("FilterProductsJson") > 0;
                """);

            migrationBuilder.Sql("""
                UPDATE webhook_subscriptions
                SET "FilterEnvironment" = "FilterEnvironmentsJson" ->> 0
                WHERE jsonb_typeof("FilterEnvironmentsJson") = 'array'
                  AND jsonb_array_length("FilterEnvironmentsJson") > 0;
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
