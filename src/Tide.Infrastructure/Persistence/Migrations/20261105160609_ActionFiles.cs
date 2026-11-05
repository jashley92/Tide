using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActionFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "ActionCommentFiles",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskReason",
                table: "ActionCommentFiles",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActionFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeclaredType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsImage = table.Column<bool>(type: "INTEGER", nullable: false),
                    TypeNote = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RiskReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AddedById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AddedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RemovedById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RemovedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RemovedReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionFiles_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActionFiles_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionFiles_ActionId",
                table: "ActionFiles",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionFiles_FileId",
                table: "ActionFiles",
                column: "FileId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionFiles");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "ActionCommentFiles");

            migrationBuilder.DropColumn(
                name: "RiskReason",
                table: "ActionCommentFiles");
        }
    }
}
