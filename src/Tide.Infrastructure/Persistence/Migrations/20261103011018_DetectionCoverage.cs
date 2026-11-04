using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DetectionCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DetectionGaps",
                columns: table => new
                {
                    TechniqueId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RaisedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectionGaps", x => x.TechniqueId);
                    table.ForeignKey(
                        name: "FK_DetectionGaps_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DetectionRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Techniques = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectionRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DetectionGaps_ActionId",
                table: "DetectionGaps",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_DetectionRules_Source_ExternalId",
                table: "DetectionRules",
                columns: new[] { "Source", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DetectionGaps");

            migrationBuilder.DropTable(
                name: "DetectionRules");
        }
    }
}
