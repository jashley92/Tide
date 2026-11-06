using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SavedQueriesAndActionIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SavedQueryId",
                table: "HuntQueries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SavedQueryName",
                table: "HuntQueries",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActionIndicators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AddedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionIndicators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionIndicators_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedQueries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Dialect = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 19500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    EditVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedQueries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HuntQueries_SavedQueryId",
                table: "HuntQueries",
                column: "SavedQueryId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionIndicators_ActionId",
                table: "ActionIndicators",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_Dialect_Name",
                table: "SavedQueries",
                columns: new[] { "Dialect", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HuntQueries_SavedQueries_SavedQueryId",
                table: "HuntQueries",
                column: "SavedQueryId",
                principalTable: "SavedQueries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HuntQueries_SavedQueries_SavedQueryId",
                table: "HuntQueries");

            migrationBuilder.DropTable(
                name: "ActionIndicators");

            migrationBuilder.DropTable(
                name: "SavedQueries");

            migrationBuilder.DropIndex(
                name: "IX_HuntQueries_SavedQueryId",
                table: "HuntQueries");

            migrationBuilder.DropColumn(
                name: "SavedQueryId",
                table: "HuntQueries");

            migrationBuilder.DropColumn(
                name: "SavedQueryName",
                table: "HuntQueries");
        }
    }
}
