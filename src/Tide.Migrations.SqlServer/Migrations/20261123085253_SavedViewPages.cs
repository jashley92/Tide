using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SavedViewPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedActionViews_UserId_Name",
                table: "SavedActionViews");

            migrationBuilder.AddColumn<string>(
                name: "Page",
                table: "SavedActionViews",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "actions");

            migrationBuilder.CreateIndex(
                name: "IX_SavedActionViews_UserId_Page_Name",
                table: "SavedActionViews",
                columns: new[] { "UserId", "Page", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one name per person: only the Actions page's views fit the narrower index.
            migrationBuilder.Sql("DELETE FROM SavedActionViews WHERE Page <> 'actions'");

            migrationBuilder.DropIndex(
                name: "IX_SavedActionViews_UserId_Page_Name",
                table: "SavedActionViews");

            migrationBuilder.DropColumn(
                name: "Page",
                table: "SavedActionViews");

            migrationBuilder.CreateIndex(
                name: "IX_SavedActionViews_UserId_Name",
                table: "SavedActionViews",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }
    }
}
