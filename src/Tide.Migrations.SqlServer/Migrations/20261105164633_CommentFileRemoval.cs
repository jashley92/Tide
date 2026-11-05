using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class CommentFileRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FromCommentAtUtc",
                table: "ActionFiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromCommentBy",
                table: "ActionFiles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedAtUtc",
                table: "ActionCommentFiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedById",
                table: "ActionCommentFiles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedByName",
                table: "ActionCommentFiles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedReason",
                table: "ActionCommentFiles",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FromCommentAtUtc",
                table: "ActionFiles");

            migrationBuilder.DropColumn(
                name: "FromCommentBy",
                table: "ActionFiles");

            migrationBuilder.DropColumn(
                name: "RemovedAtUtc",
                table: "ActionCommentFiles");

            migrationBuilder.DropColumn(
                name: "RemovedById",
                table: "ActionCommentFiles");

            migrationBuilder.DropColumn(
                name: "RemovedByName",
                table: "ActionCommentFiles");

            migrationBuilder.DropColumn(
                name: "RemovedReason",
                table: "ActionCommentFiles");
        }
    }
}
