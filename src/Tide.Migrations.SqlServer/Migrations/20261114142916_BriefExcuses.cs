using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AnalystId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AnalystName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ExcusedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExcusedById = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExcusedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EditVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
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
