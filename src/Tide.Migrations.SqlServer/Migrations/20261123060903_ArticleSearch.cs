using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ArticleSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One marked row for each article already there: the search index job builds them after start-up, and until
            // an article's row is built a search matches it part by part, as before. A new article gets its row as it's saved.
            migrationBuilder.CreateTable(
                name: "ArticleSearch",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Builder = table.Column<int>(type: "int", nullable: false),
                    Dirty = table.Column<bool>(type: "bit", nullable: false),
                    Stamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleSearch", x => x.ArticleId);
                    table.ForeignKey(
                        name: "FK_ArticleSearch_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleSearch_Dirty",
                table: "ArticleSearch",
                column: "Dirty",
                filter: "[Dirty] = 1");

            migrationBuilder.Sql("""
            INSERT INTO [ArticleSearch] ([ArticleId], [Text], [Builder], [Dirty], [Stamp]) SELECT [Id], N'', 0, 1, '00000000-0000-0000-0000-000000000000' FROM [Articles];
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleSearch");
        }
    }
}
