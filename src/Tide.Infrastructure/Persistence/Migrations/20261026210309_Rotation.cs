using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RotationChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    RequesterId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequesterName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PartnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PartnerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    GiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    GiveTo = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TakeFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    TakeTo = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DecidedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DecisionNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RotationChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RotationMembers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RotationMembers", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "RotationWeeks",
                columns: table => new
                {
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AnalystId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AnalystName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RotationWeeks", x => x.WeekStart);
                });

            migrationBuilder.CreateTable(
                name: "RotationOverrides",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AnalystId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AnalystName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChangeId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RotationOverrides", x => x.Date);
                    table.ForeignKey(
                        name: "FK_RotationOverrides_RotationChanges_ChangeId",
                        column: x => x.ChangeId,
                        principalTable: "RotationChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RotationChanges_RequesterId",
                table: "RotationChanges",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_RotationChanges_Status_PartnerId",
                table: "RotationChanges",
                columns: new[] { "Status", "PartnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_RotationOverrides_AnalystId",
                table: "RotationOverrides",
                column: "AnalystId");

            migrationBuilder.CreateIndex(
                name: "IX_RotationOverrides_ChangeId",
                table: "RotationOverrides",
                column: "ChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_RotationWeeks_AnalystId",
                table: "RotationWeeks",
                column: "AnalystId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RotationMembers");

            migrationBuilder.DropTable(
                name: "RotationOverrides");

            migrationBuilder.DropTable(
                name: "RotationWeeks");

            migrationBuilder.DropTable(
                name: "RotationChanges");
        }
    }
}
