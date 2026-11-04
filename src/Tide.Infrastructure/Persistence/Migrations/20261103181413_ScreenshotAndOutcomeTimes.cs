using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScreenshotAndOutcomeTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OutcomeAtUtc",
                table: "BriefSends",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ScreenshotsChangedAtUtc",
                table: "Articles",
                type: "INTEGER",
                nullable: true);

            // Filled once from the audit trail, which recorded both until now; from here on the app keeps them itself.
            // A send's row was only ever modified to record or settle its outcome. A screenshot's rows name its article;
            // only articles with a review decided on 0.1.0-alpha.9 (one that recorded the edit version) read it.
            migrationBuilder.Sql("""
                UPDATE "BriefSends" SET "OutcomeAtUtc" = (
                    SELECT MAX(a."AtUtc") FROM "AuditLog" a
                    WHERE a."EntityType" = 'BriefSend' AND a."Action" = 'Modified' AND a."EntityKey" = lower("BriefSends"."Id"));
                """);
            migrationBuilder.Sql("""
                UPDATE "Articles" SET "ScreenshotsChangedAtUtc" = (
                    SELECT MAX(a."AtUtc") FROM "AuditLog" a
                    WHERE a."EntityType" = 'ArticleScreenshot' AND a."ChangesJson" LIKE '%' || lower("Articles"."Id") || '%')
                WHERE EXISTS (SELECT 1 FROM "ArticleReviews" r WHERE r."ArticleId" = "Articles"."Id" AND r."ReviewedVersion" = "Articles"."EditVersion");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutcomeAtUtc",
                table: "BriefSends");

            migrationBuilder.DropColumn(
                name: "ScreenshotsChangedAtUtc",
                table: "Articles");
        }
    }
}
