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

            // The rows already there are filled at start-up (DatabaseInitializer.FillUrlHashesAsync): the hash is of the
            // canonical address, which only the app's own code computes.
            migrationBuilder.CreateIndex(
                name: "IX_Articles_SourceUrlHash",
                table: "Articles",
                column: "SourceUrlHash");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleLinks_UrlHash",
                table: "ArticleLinks",
                column: "UrlHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
