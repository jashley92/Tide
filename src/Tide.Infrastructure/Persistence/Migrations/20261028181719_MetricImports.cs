using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "MetricValues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MetricImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ImportedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Rows = table.Column<int>(type: "INTEGER", nullable: false),
                    Applied = table.Column<int>(type: "INTEGER", nullable: false),
                    Unchanged = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Rejected = table.Column<bool>(type: "INTEGER", nullable: false),
                    Problems = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
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
