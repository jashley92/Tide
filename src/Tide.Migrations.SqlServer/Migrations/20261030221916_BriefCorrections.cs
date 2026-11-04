using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class BriefCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BriefCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrectsSendId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Edition = table.Column<int>(type: "int", nullable: false),
                    IsTlpSlip = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ArticleIds = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Ceiling = table.Column<int>(type: "int", nullable: false),
                    Marking = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentById = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SentByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EmailHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WebHtml = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BriefCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BriefCorrections_BriefSends_CorrectsSendId",
                        column: x => x.CorrectsSendId,
                        principalTable: "BriefSends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BriefCorrections_Briefs_BriefId",
                        column: x => x.BriefId,
                        principalTable: "Briefs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BriefCorrections_BriefDate_Edition",
                table: "BriefCorrections",
                columns: new[] { "BriefDate", "Edition" });

            migrationBuilder.CreateIndex(
                name: "IX_BriefCorrections_BriefId",
                table: "BriefCorrections",
                column: "BriefId");

            migrationBuilder.CreateIndex(
                name: "IX_BriefCorrections_CorrectsSendId_Status",
                table: "BriefCorrections",
                columns: new[] { "CorrectsSendId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BriefCorrections");
        }
    }
}
