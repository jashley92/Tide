using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArticleWriter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HandoffNote",
                table: "Articles",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WriterId",
                table: "Articles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WriterName",
                table: "Articles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WriterSinceUtc",
                table: "Articles",
                type: "INTEGER",
                nullable: true);

            // Every article written before there was a writer is being written by its author, since it was started
            // (ADR 0031): Today's drafts and "Only mine" read the writer.
            migrationBuilder.Sql("UPDATE [Articles] SET [WriterId] = [AuthorId], [WriterName] = [AuthorName], [WriterSinceUtc] = [CreatedAtUtc] WHERE [WriterId] IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HandoffNote",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "WriterId",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "WriterName",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "WriterSinceUtc",
                table: "Articles");
        }
    }
}
