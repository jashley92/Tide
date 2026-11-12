using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            // Actions raised from a recommended line before the link was stored were matched to it by title: the slide
            // listed one under its line when its title was the line's words. They get the link once, here (their
            // title found in the article's recommendations, case aside), so the slide never needs to match titles again.
            // One whose title only appears inside a longer line gets it too, but matches no whole line, so the slide
            // keeps it among the article's other actions, as before.
            migrationBuilder.Sql("""
                UPDATE Actions SET RecommendationLine = Title
                WHERE ArticleId IS NOT NULL
                  AND EXISTS (SELECT 1 FROM Articles ar
                              WHERE ar.Id = Actions.ArticleId AND ar.RecommendedActions IS NOT NULL
                                AND instr(lower(ar.RecommendedActions), lower(Actions.Title)) > 0);
                """);
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
