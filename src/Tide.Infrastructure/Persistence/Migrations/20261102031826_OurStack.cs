using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OurStack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StackProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OtherNames = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Versions = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    TeamId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CheckedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    IsRetired = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StackProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StackProducts_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ArticleProducts",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleProducts", x => new { x.ArticleId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_ArticleProducts_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArticleProducts_StackProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "StackProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleProducts_ProductId",
                table: "ArticleProducts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StackProducts_Name",
                table: "StackProducts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StackProducts_TeamId",
                table: "StackProducts",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleProducts");

            migrationBuilder.DropTable(
                name: "StackProducts");
        }
    }
}
