using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ScreenshotAndOutcomeTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OutcomeAtUtc",
                table: "BriefSends",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScreenshotsChangedAtUtc",
                table: "Articles",
                type: "datetimeoffset",
                nullable: true);

            // Filled once from the audit trail, which recorded both until now; from here on the app keeps them itself.
            // A send's row was only ever modified to record or settle its outcome. A screenshot's rows name its article;
            // only articles with a review decided on 0.1.0-alpha.9 (one that recorded the edit version) read it.
            migrationBuilder.Sql("""
                UPDATE s SET OutcomeAtUtc = (
                    SELECT MAX(a.AtUtc) FROM AuditLog a
                    WHERE a.EntityType = N'BriefSend' AND a.Action = N'Modified' AND a.EntityKey = LOWER(CONVERT(nvarchar(36), s.Id)))
                FROM BriefSends s;
                """);
            migrationBuilder.Sql("""
                UPDATE ar SET ScreenshotsChangedAtUtc = (
                    SELECT MAX(a.AtUtc) FROM AuditLog a
                    WHERE a.EntityType = N'ArticleScreenshot' AND a.ChangesJson LIKE N'%' + LOWER(CONVERT(nvarchar(36), ar.Id)) + N'%')
                FROM Articles ar
                WHERE EXISTS (SELECT 1 FROM ArticleReviews r WHERE r.ArticleId = ar.Id AND r.ReviewedVersion = ar.EditVersion);
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
