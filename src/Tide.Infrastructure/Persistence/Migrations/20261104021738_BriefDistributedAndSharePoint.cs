using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BriefDistributedAndSharePoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DistributedAtUtc",
                table: "Briefs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DistributedById",
                table: "Briefs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DistributedByName",
                table: "Briefs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SharePointPostedAtUtc",
                table: "Briefs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharePointPostedById",
                table: "Briefs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharePointPostedByName",
                table: "Briefs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DistributedAtUtc",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "DistributedById",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "DistributedByName",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "SharePointPostedAtUtc",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "SharePointPostedById",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "SharePointPostedByName",
                table: "Briefs");
        }
    }
}
