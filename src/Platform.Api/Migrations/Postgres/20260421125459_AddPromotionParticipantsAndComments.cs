using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddPromotionParticipantsAndComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParticipantsJson",
                table: "promotion_candidates",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "promotion_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorEmail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_promotion_comments_promotion_candidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "promotion_candidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_comments_CandidateId_CreatedAt",
                table: "promotion_comments",
                columns: new[] { "CandidateId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_comments");

            migrationBuilder.DropColumn(
                name: "ParticipantsJson",
                table: "promotion_candidates");
        }
    }
}
