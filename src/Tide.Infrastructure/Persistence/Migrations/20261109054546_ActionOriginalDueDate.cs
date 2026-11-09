using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActionOriginalDueDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DueDateMoves",
                table: "Actions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "OriginalDueDate",
                table: "Actions",
                type: "TEXT",
                nullable: true);

            // An action from before the first due date was kept starts from the date it has now, with no moves counted:
            // its earlier moves weren't recorded. Quoted with square brackets, which SQLite and SQL Server both read.
            migrationBuilder.Sql("UPDATE [Actions] SET [OriginalDueDate] = [DueDate] WHERE [OriginalDueDate] IS NULL AND [DueDate] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DueDateMoves",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "OriginalDueDate",
                table: "Actions");
        }
    }
}
