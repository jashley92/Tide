using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommentFileRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FromCommentAtUtc",
                table: "ActionFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromCommentBy",
                table: "ActionFiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RemovedAtUtc",
                table: "ActionCommentFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedById",
                table: "ActionCommentFiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedByName",
                table: "ActionCommentFiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedReason",
                table: "ActionCommentFiles",
                type: "TEXT",
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
