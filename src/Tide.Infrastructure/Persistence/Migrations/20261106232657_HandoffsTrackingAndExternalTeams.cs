using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HandoffsTrackingAndExternalTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveLimit",
                table: "Teams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Teams",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactName",
                table: "Teams",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Engage",
                table: "Teams",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExternal",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LimitMode",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RemindBeforeDays",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "RemindOverdueEveryDays",
                table: "Teams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemindersOn",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "HandedAtUtc",
                table: "Actions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandedById",
                table: "Actions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandedByName",
                table: "Actions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HandedFromTeamId",
                table: "Actions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoffReason",
                table: "Actions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NoReminders",
                table: "Actions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Existing teams take the external reminders' defaults (they matter once a team is marked external): reminded two
            // business days before the due date, and every three while overdue.
            migrationBuilder.Sql("UPDATE Teams SET RemindOverdueEveryDays = 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveLimit",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ContactName",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "Engage",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "IsExternal",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "LimitMode",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "RemindBeforeDays",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "RemindOverdueEveryDays",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "RemindersOn",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "HandedAtUtc",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "HandedById",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "HandedByName",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "HandedFromTeamId",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "HandoffReason",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "NoReminders",
                table: "Actions");
        }
    }
}
