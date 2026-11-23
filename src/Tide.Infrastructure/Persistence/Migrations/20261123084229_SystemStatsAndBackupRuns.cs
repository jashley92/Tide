using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemStatsAndBackupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    FinishedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Machine = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TakenAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DatabaseBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    FilesBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DiskFreeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DiskTotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Tables = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    Folders = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_Kind_FinishedAtUtc",
                table: "BackupRuns",
                columns: new[] { "Kind", "FinishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemStats_Day",
                table: "SystemStats",
                column: "Day",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupRuns");

            migrationBuilder.DropTable(
                name: "SystemStats");
        }
    }
}
