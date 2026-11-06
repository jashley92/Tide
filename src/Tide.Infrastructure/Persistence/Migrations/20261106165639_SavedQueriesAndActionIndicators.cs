using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SavedQueryName",
                table: "HuntQueries",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActionIndicators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Dialect = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 19500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsRetired = table.Column<bool>(type: "INTEGER", nullable: false),
                    EditVersion = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
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
