using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WhyItMattersKept : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "WhyItMattersKeptAtUtc",
                table: "Articles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhyItMattersKeptById",
                table: "Articles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhyItMattersKeptByName",
                table: "Articles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhyItMattersKeptHash",
                table: "Articles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhyItMattersKeptTlp",
                table: "Articles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhyItMattersKeptAtUtc",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "WhyItMattersKeptById",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "WhyItMattersKeptByName",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "WhyItMattersKeptHash",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "WhyItMattersKeptTlp",
                table: "Articles");
        }
    }
}
