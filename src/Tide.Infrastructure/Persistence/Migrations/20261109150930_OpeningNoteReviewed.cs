using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpeningNoteReviewed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TeamNoteReviewedAtUtc",
                table: "Briefs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeamNoteReviewedAtUtc",
                table: "Briefs");
        }
    }
}
