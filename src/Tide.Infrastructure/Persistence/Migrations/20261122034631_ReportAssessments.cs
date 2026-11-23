using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Section = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Tlp = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EditVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportAssessments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportAssessments_PeriodKey_Section",
                table: "ReportAssessments",
                columns: new[] { "PeriodKey", "Section" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportAssessments");
        }
    }
}
