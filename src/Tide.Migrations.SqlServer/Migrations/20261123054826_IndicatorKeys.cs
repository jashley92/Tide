using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class IndicatorKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostRev",
                table: "ArticleIndicators",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValueKey",
                table: "ArticleIndicators",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValueKey",
                table: "ActionIndicators",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // The rows already there are keyed after start-up by a background job (IndicatorKeyFill): the key is a hash
            // only the app's own code computes. A row with no ValueKey is one still to key, and lookups match it by its
            // value as before until then; a new install has none.
            migrationBuilder.CreateIndex(
                name: "IX_ArticleIndicators_HostRev",
                table: "ArticleIndicators",
                column: "HostRev");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleIndicators_ValueKey",
                table: "ArticleIndicators",
                column: "ValueKey");

            migrationBuilder.CreateIndex(
                name: "IX_ActionIndicators_ValueKey",
                table: "ActionIndicators",
                column: "ValueKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArticleIndicators_HostRev",
                table: "ArticleIndicators");

            migrationBuilder.DropIndex(
                name: "IX_ArticleIndicators_ValueKey",
                table: "ArticleIndicators");

            migrationBuilder.DropIndex(
                name: "IX_ActionIndicators_ValueKey",
                table: "ActionIndicators");

            migrationBuilder.DropColumn(
                name: "HostRev",
                table: "ArticleIndicators");

            migrationBuilder.DropColumn(
                name: "ValueKey",
                table: "ArticleIndicators");

            migrationBuilder.DropColumn(
                name: "ValueKey",
                table: "ActionIndicators");
        }
    }
}
