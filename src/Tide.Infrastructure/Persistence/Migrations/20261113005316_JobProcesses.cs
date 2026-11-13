using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobProcesses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobProcesses",
                columns: table => new
                {
                    Holder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobProcesses", x => x.Holder);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobProcesses_LastSeenAtUtc",
                table: "JobProcesses",
                column: "LastSeenAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobProcesses");
        }
    }
}
