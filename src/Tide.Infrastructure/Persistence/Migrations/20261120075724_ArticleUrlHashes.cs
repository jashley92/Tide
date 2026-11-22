using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArticleUrlHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceUrlHash",
                table: "Articles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UrlHash",
                table: "ArticleLinks",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // The rows already there are filled after start-up by a background job (UrlHashFill): the hash is of the
            // canonical address, which only the app's own code computes. A database with addresses to fill is marked so,
            // and triage matches addresses the old way until the job has filled them; a new install has none. On SQLite a
            // DateTimeOffset is stored as UTC ticks (AppDbContext's converter): days since 0001-01-01 (Julian day 1721425.5)
            // times the ticks in a day.
            migrationBuilder.CreateIndex(
                name: "IX_Articles_SourceUrlHash",
                table: "Articles",
                column: "SourceUrlHash");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleLinks_UrlHash",
                table: "ArticleLinks",
                column: "UrlHash");

            migrationBuilder.Sql("""
            INSERT INTO "Settings" ("Key", "Value", "UpdatedAtUtc", "UpdatedBy")
            SELECT 'Data:UrlHashesToFill', '1', CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER), 'system'
            WHERE (EXISTS (SELECT 1 FROM "Articles" WHERE "SourceUrl" IS NOT NULL) OR EXISTS (SELECT 1 FROM "ArticleLinks"))
              AND NOT EXISTS (SELECT 1 FROM "Settings" WHERE "Key" = 'Data:UrlHashesToFill');
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "Settings" WHERE "Key" = 'Data:UrlHashesToFill';""");

            migrationBuilder.DropIndex(
                name: "IX_Articles_SourceUrlHash",
                table: "Articles");

            migrationBuilder.DropIndex(
                name: "IX_ArticleLinks_UrlHash",
                table: "ArticleLinks");

            migrationBuilder.DropColumn(
                name: "SourceUrlHash",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "UrlHash",
                table: "ArticleLinks");
        }
    }
}
