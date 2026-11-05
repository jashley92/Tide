using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
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
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskReason",
                table: "ActionCommentFiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActionFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeclaredType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsImage = table.Column<bool>(type: "bit", nullable: false),
                    TypeNote = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RiskReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddedById = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AddedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedById = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RemovedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RemovedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
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
