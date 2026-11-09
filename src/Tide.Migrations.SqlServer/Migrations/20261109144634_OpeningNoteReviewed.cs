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

            migrationBuilder.AddColumn<string>(
                name: "TeamNoteReviewedById",
                table: "Briefs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamNoteReviewedByName",
                table: "Briefs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeamNoteReviewedAtUtc",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "TeamNoteReviewedById",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "TeamNoteReviewedByName",
                table: "Briefs");
        }
    }
}
