using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobLeaseAndRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobLeases",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Holder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AcquiredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobLeases", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Job = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Machine = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_Job_StartedAtUtc",
                table: "JobRuns",
                columns: new[] { "Job", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_StartedAtUtc",
                table: "JobRuns",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobLeases");

            migrationBuilder.DropTable(
                name: "JobRuns");
        }
    }
}
