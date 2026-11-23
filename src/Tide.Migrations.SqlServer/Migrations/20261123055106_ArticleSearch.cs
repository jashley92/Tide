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
            // Created empty: the search index job builds every article's row after start-up, and until an article has
            // one a search matches it part by part, as before.
            migrationBuilder.CreateTable(
                name: "ArticleSearch",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Builder = table.Column<int>(type: "int", nullable: false),
                    Dirty = table.Column<bool>(type: "bit", nullable: false),
                    Stamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuiltAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleSearch");
        }
    }
}
