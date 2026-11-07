using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class EscalationLadder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EscalationEmail",
                table: "Teams",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalationName",
                table: "Teams",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscalatedAtUtc",
                table: "Actions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EscalatedForDue",
                table: "Actions",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Actions_EscalatedForDue",
                table: "Actions",
                column: "EscalatedForDue",
                filter: "[EscalatedForDue] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Actions_EscalatedForDue",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "EscalationEmail",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "EscalationName",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "EscalatedAtUtc",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "EscalatedForDue",
                table: "Actions");
        }
    }
}
