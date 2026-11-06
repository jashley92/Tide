using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class TriageSplitsAndGroupingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TriageSplits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Batch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SplitAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SplitBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SplitByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriageSplits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TriageSplits_FeedItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FeedItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriageSplits_Batch_ItemId",
                table: "TriageSplits",
                columns: new[] { "Batch", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriageSplits_ItemId",
                table: "TriageSplits",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TriageSplits");
        }
    }
}
