using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class OpeningNoteReviewed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TeamNoteReviewedAtUtc",
                table: "Briefs",
                type: "datetimeoffset",
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
