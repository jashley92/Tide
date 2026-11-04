using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Actions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationMarks",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SentAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationMarks", x => new { x.Kind, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "QueryTemplates",
                columns: table => new
                {
                    Dialect = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Template = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryTemplates", x => new { x.Dialect, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRetired = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssigneeId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AssigneeName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    OutcomeNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ClosedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    TicketRef = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TicketUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EditVersion = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Actions_Actions_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Actions_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Actions_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamMembers",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMembers", x => new { x.TeamId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TeamMembers_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionComments_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionEvents_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HuntQueries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Dialect = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    GeneratedText = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RunById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RunByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RunAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    EvidenceFileId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HuntQueries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HuntQueries_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HuntQueries_Files_EvidenceFileId",
                        column: x => x.EvidenceFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionComments_ActionId_AtUtc",
                table: "ActionComments",
                columns: new[] { "ActionId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionEvents_ActionId_AtUtc",
                table: "ActionEvents",
                columns: new[] { "ActionId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Actions_ArticleId",
                table: "Actions",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_Actions_AssigneeId",
                table: "Actions",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_Actions_Number",
                table: "Actions",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Actions_ParentId",
                table: "Actions",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Actions_Status_DueDate",
                table: "Actions",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Actions_TeamId",
                table: "Actions",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_HuntQueries_ActionId",
                table: "HuntQueries",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_HuntQueries_EvidenceFileId",
                table: "HuntQueries",
                column: "EvidenceFileId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationMarks_SentAtUtc",
                table: "NotificationMarks",
                column: "SentAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_UserId",
                table: "TeamMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Key",
                table: "Teams",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionComments");

            migrationBuilder.DropTable(
                name: "ActionEvents");

            migrationBuilder.DropTable(
                name: "HuntQueries");

            migrationBuilder.DropTable(
                name: "NotificationMarks");

            migrationBuilder.DropTable(
                name: "QueryTemplates");

            migrationBuilder.DropTable(
                name: "TeamMembers");

            migrationBuilder.DropTable(
                name: "Actions");

            migrationBuilder.DropTable(
                name: "Teams");
        }
    }
}
