using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BriefExcuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BriefExcuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BriefDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AnalystId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AnalystName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ExcusedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExcusedById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExcusedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EditVersion = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BriefExcuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BriefExcuses_AnalystId",
                table: "BriefExcuses",
                column: "AnalystId");

            migrationBuilder.CreateIndex(
                name: "IX_BriefExcuses_BriefDate",
                table: "BriefExcuses",
                column: "BriefDate",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BriefExcuses");
        }
    }
}
