using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class MeetingNoteFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActionId",
                table: "MeetingNotes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DoneAtUtc",
                table: "MeetingNotes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingNotes_ActionId",
                table: "MeetingNotes",
                column: "ActionId");

            migrationBuilder.AddForeignKey(
                name: "FK_MeetingNotes_Actions_ActionId",
                table: "MeetingNotes",
                column: "ActionId",
                principalTable: "Actions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MeetingNotes_Actions_ActionId",
                table: "MeetingNotes");

            migrationBuilder.DropIndex(
                name: "IX_MeetingNotes_ActionId",
                table: "MeetingNotes");

            migrationBuilder.DropColumn(
                name: "ActionId",
                table: "MeetingNotes");

            migrationBuilder.DropColumn(
                name: "DoneAtUtc",
                table: "MeetingNotes");
        }
    }
}
