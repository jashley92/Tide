using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SourceTagFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastFilteredOut",
                table: "Sources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OnlyCategories",
                table: "Sources",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeenCategories",
                table: "Sources",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkipCategories",
                table: "Sources",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastFilteredOut",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "OnlyCategories",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SeenCategories",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SkipCategories",
                table: "Sources");
        }
    }
}
