using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class BriefReopenAndNotSent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotSentAtUtc",
                table: "Briefs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotSentById",
                table: "Briefs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotSentByName",
                table: "Briefs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotSentReason",
                table: "Briefs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CarriedFromBriefId",
                table: "BriefItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusBeforeMeeting",
                table: "BriefItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BriefItems_CarriedFromBriefId",
                table: "BriefItems",
                column: "CarriedFromBriefId");

            // A brief presented (not yet sent) before this release: its articles marked presented that no earlier
            // brief had presented were this meeting's, from Ready, so reopening the meeting can put them back.
            // Articles it carried can't be told from ones put on the next brief by hand, so they stay there.
            migrationBuilder.Sql("""
                UPDATE BriefItems SET StatusBeforeMeeting = 1
                WHERE BriefId IN (SELECT Id FROM Briefs WHERE Status = 2)
                  AND ArticleId IN (SELECT Id FROM Articles WHERE Status = 2)
                  AND NOT EXISTS (
                      SELECT 1 FROM BriefItems o
                      JOIN Briefs ob ON ob.Id = o.BriefId
                      JOIN Briefs tb ON tb.Id = BriefItems.BriefId
                      WHERE o.ArticleId = BriefItems.ArticleId AND ob.BriefDate < tb.BriefDate AND ob.Status >= 2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BriefItems_CarriedFromBriefId",
                table: "BriefItems");

            migrationBuilder.DropColumn(
                name: "NotSentAtUtc",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "NotSentById",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "NotSentByName",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "NotSentReason",
                table: "Briefs");

            migrationBuilder.DropColumn(
                name: "CarriedFromBriefId",
                table: "BriefItems");

            migrationBuilder.DropColumn(
                name: "StatusBeforeMeeting",
                table: "BriefItems");
        }
    }
}
