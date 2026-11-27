using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class HotScanIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FeedItems_Status_FetchedAtUtc",
                table: "FeedItems");

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_Status_FetchedAtUtc_SourceId",
                table: "FeedItems",
                columns: new[] { "Status", "FetchedAtUtc", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Actions_ClosedDate",
                table: "Actions",
                column: "ClosedDate",
                filter: "[ClosedDate] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FeedItems_Status_FetchedAtUtc_SourceId",
                table: "FeedItems");

            migrationBuilder.DropIndex(
                name: "IX_Actions_ClosedDate",
                table: "Actions");

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_Status_FetchedAtUtc",
                table: "FeedItems",
                columns: new[] { "Status", "FetchedAtUtc" });
        }
    }
}
