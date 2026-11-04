using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class MetricImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImportId",
                table: "MetricValues",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "MetricValues",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MetricImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    ReadAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ImportedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Rows = table.Column<int>(type: "int", nullable: false),
                    Applied = table.Column<int>(type: "int", nullable: false),
                    Unchanged = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    Rejected = table.Column<bool>(type: "bit", nullable: false),
                    Problems = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricImports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricValues_ImportId",
                table: "MetricValues",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricImports_ReadAtUtc",
                table: "MetricImports",
                column: "ReadAtUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_MetricValues_MetricImports_ImportId",
                table: "MetricValues",
                column: "ImportId",
                principalTable: "MetricImports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetricValues_MetricImports_ImportId",
                table: "MetricValues");

            migrationBuilder.DropTable(
                name: "MetricImports");

            migrationBuilder.DropIndex(
                name: "IX_MetricValues_ImportId",
                table: "MetricValues");

            migrationBuilder.DropColumn(
                name: "ImportId",
                table: "MetricValues");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "MetricValues");
        }
    }
}
