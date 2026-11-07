using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalationName",
                table: "Teams",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EscalatedAtUtc",
                table: "Actions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EscalatedForDue",
                table: "Actions",
                type: "TEXT",
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
