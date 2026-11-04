using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActionPlaybooksChecklistsRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PlaybookId",
                table: "Actions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaybookName",
                table: "Actions",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActionCardRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Colour = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowAsFilter = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: true),
                    PriorityAtLeast = table.Column<int>(type: "INTEGER", nullable: true),
                    TeamId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: true),
                    Sla = table.Column<int>(type: "INTEGER", nullable: false),
                    Unassigned = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasChecklist = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPart = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionCardRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionCardRules_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActionChecklistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    IsDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    DoneByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DoneAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionChecklistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionChecklistItems_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionCommentFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    IsImage = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionCommentFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionCommentFiles_ActionComments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "ActionComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActionCommentFiles_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Playbooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playbooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaybookSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaybookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybookSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybookSteps_Playbooks_PlaybookId",
                        column: x => x.PlaybookId,
                        principalTable: "Playbooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Actions_PlaybookId",
                table: "Actions",
                column: "PlaybookId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionCardRules_TeamId",
                table: "ActionCardRules",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionChecklistItems_ActionId_SortOrder",
                table: "ActionChecklistItems",
                columns: new[] { "ActionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionCommentFiles_CommentId",
                table: "ActionCommentFiles",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionCommentFiles_FileId",
                table: "ActionCommentFiles",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybookSteps_PlaybookId_Order",
                table: "PlaybookSteps",
                columns: new[] { "PlaybookId", "Order" });

            migrationBuilder.AddForeignKey(
                name: "FK_Actions_Playbooks_PlaybookId",
                table: "Actions",
                column: "PlaybookId",
                principalTable: "Playbooks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Actions_Playbooks_PlaybookId",
                table: "Actions");

            migrationBuilder.DropTable(
                name: "ActionCardRules");

            migrationBuilder.DropTable(
                name: "ActionChecklistItems");

            migrationBuilder.DropTable(
                name: "ActionCommentFiles");

            migrationBuilder.DropTable(
                name: "PlaybookSteps");

            migrationBuilder.DropTable(
                name: "Playbooks");

            migrationBuilder.DropIndex(
                name: "IX_Actions_PlaybookId",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "PlaybookId",
                table: "Actions");

            migrationBuilder.DropColumn(
                name: "PlaybookName",
                table: "Actions");
        }
    }
}
