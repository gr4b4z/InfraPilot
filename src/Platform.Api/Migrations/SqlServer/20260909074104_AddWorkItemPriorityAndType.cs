using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddWorkItemPriorityAndType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "promotion_work_items",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkItemType",
                table: "promotion_work_items",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "deploy_event_work_items",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkItemType",
                table: "deploy_event_work_items",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "promotion_work_items");

            migrationBuilder.DropColumn(
                name: "WorkItemType",
                table: "promotion_work_items");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "deploy_event_work_items");

            migrationBuilder.DropColumn(
                name: "WorkItemType",
                table: "deploy_event_work_items");
        }
    }
}
