using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ActionRecommendationLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecommendationLine",
                table: "Actions",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            // The links of actions raised before this are filled once, after the migrations, by
            // DatabaseInitializer.LinkRecommendationsAsync: the article's line as written, one action per line.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecommendationLine",
                table: "Actions");
        }
    }
}
