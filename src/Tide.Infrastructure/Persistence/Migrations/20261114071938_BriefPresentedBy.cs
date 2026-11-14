using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BriefPresentedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PresentedByName",
                table: "Briefs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PresentedByName",
                table: "Briefs");
        }
    }
}
