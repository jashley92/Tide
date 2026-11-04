using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Adversaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Adversaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Aliases = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MitreId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Motivation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Sectors = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Watched = table.Column<bool>(type: "bit", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    MergedIntoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Adversaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArticleAdversaries",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdversaryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAdversaries", x => new { x.ArticleId, x.AdversaryId });
                    table.ForeignKey(
                        name: "FK_ArticleAdversaries_Adversaries_AdversaryId",
                        column: x => x.AdversaryId,
                        principalTable: "Adversaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArticleAdversaries_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Adversaries_MitreId",
                table: "Adversaries",
                column: "MitreId");

            migrationBuilder.CreateIndex(
                name: "IX_Adversaries_Name",
                table: "Adversaries",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAdversaries_AdversaryId",
                table: "ArticleAdversaries",
                column: "AdversaryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleAdversaries");

            migrationBuilder.DropTable(
                name: "Adversaries");
        }
    }
}
