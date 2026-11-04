using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeetingRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeetingAttendees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BriefId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    JoinedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LeftAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingAttendees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingAttendees_Briefs_BriefId",
                        column: x => x.BriefId,
                        principalTable: "Briefs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MeetingNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BriefId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EditedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Text = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingNotes_Briefs_BriefId",
                        column: x => x.BriefId,
                        principalTable: "Briefs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttendees_BriefId_UserId_Name",
                table: "MeetingAttendees",
                columns: new[] { "BriefId", "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingNotes_BriefId_AtUtc",
                table: "MeetingNotes",
                columns: new[] { "BriefId", "AtUtc" });

            // A brief's notes from before they were kept per person become one note, by its presenting analyst, at the
            // meeting's time; the old column is emptied so they aren't shown twice. The id is a random GUID in the text
            // form EF stores here (upper case, hyphenated), worked out once per row.
            migrationBuilder.Sql("""
                WITH old AS MATERIALIZED (
                    SELECT upper(hex(randomblob(16))) AS h, Id, AnalystId, AnalystName, MeetingNotes,
                           COALESCE(MeetingHeldAtUtc, PresentedAtUtc, ModifiedAtUtc, CreatedAtUtc) AS AtUtc
                    FROM Briefs WHERE MeetingNotes IS NOT NULL AND trim(MeetingNotes) <> '')
                INSERT INTO MeetingNotes (Id, BriefId, AuthorId, AuthorName, ArticleId, AtUtc, EditedAtUtc, Text)
                SELECT substr(h, 1, 8) || '-' || substr(h, 9, 4) || '-' || substr(h, 13, 4) || '-' || substr(h, 17, 4) || '-' || substr(h, 21, 12),
                       Id, AnalystId, AnalystName, NULL, AtUtc, NULL, MeetingNotes
                FROM old;
                UPDATE Briefs SET MeetingNotes = NULL WHERE MeetingNotes IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one text per brief: each note as "Name: text", oldest first, so a rollback keeps them.
            migrationBuilder.Sql("""
                UPDATE Briefs SET MeetingNotes = (
                    SELECT group_concat(Line, char(10) || char(10)) FROM (
                        SELECT AuthorName || ': ' || Text AS Line FROM MeetingNotes n
                        WHERE n.BriefId = Briefs.Id ORDER BY n.AtUtc))
                WHERE EXISTS (SELECT 1 FROM MeetingNotes n WHERE n.BriefId = Briefs.Id);
                """);

            migrationBuilder.DropTable(
                name: "MeetingAttendees");

            migrationBuilder.DropTable(
                name: "MeetingNotes");
        }
    }
}
