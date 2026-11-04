using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArticleReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArticleReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestedByName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ReviewerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ReviewerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ReviewedVersion = table.Column<Guid>(type: "TEXT", nullable: true),
                    Checks = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleReviews_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReviewId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Quote = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Text = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedByName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewComments_ArticleReviews_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "ArticleReviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleReviews_ArticleId",
                table: "ArticleReviews",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleReviews_ReviewerId_Status",
                table: "ArticleReviews",
                columns: new[] { "ReviewerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewComments_ReviewId",
                table: "ReviewComments",
                column: "ReviewId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewComments");

            migrationBuilder.DropTable(
                name: "ArticleReviews");
        }
    }
}
