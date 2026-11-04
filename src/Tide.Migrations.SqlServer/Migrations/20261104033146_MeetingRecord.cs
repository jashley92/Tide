using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    JoinedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeftAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AuthorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EditedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false)
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
            // meeting's time; the old column is emptied so they aren't shown twice.
            migrationBuilder.Sql("""
                INSERT INTO MeetingNotes (Id, BriefId, AuthorId, AuthorName, ArticleId, AtUtc, EditedAtUtc, Text)
                SELECT NEWID(), Id, AnalystId, AnalystName, NULL,
                       COALESCE(MeetingHeldAtUtc, PresentedAtUtc, ModifiedAtUtc, CreatedAtUtc), NULL, MeetingNotes
                FROM Briefs WHERE MeetingNotes IS NOT NULL AND LTRIM(RTRIM(MeetingNotes)) <> N'';
                UPDATE Briefs SET MeetingNotes = NULL WHERE MeetingNotes IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one text per brief: each note as "Name: text", oldest first, so a rollback keeps them.
            migrationBuilder.Sql("""
                UPDATE b SET MeetingNotes = x.Notes
                FROM Briefs b
                JOIN (SELECT BriefId, STRING_AGG(CAST(AuthorName + N': ' + Text AS nvarchar(max)), CHAR(10) + CHAR(10)) WITHIN GROUP (ORDER BY AtUtc) AS Notes
                      FROM MeetingNotes GROUP BY BriefId) x ON x.BriefId = b.Id;
                """);

            migrationBuilder.DropTable(
                name: "MeetingAttendees");

            migrationBuilder.DropTable(
                name: "MeetingNotes");
        }
    }
}
