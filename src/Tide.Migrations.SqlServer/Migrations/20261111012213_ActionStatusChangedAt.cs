using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ActionStatusChangedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAtUtc",
                table: "Actions",
                type: "datetimeoffset",
                nullable: true);

            // An action from before this was kept is dated from what was recorded: a closed one from when it was closed; an open
            // one from the latest history line that moved it to the status it has now, as the app writes it ("Status: A → B",
            // at the start of the line or after an update's lead, or taking it on, which starts it); otherwise from when it was
            // raised. A person's own words that only look like a move (a quoted checklist item, a note) don't start a line
            // that way, so they aren't taken for one.
            migrationBuilder.Sql("""
            UPDATE [Actions] SET [StatusChangedAtUtc] = COALESCE(
                CASE WHEN [Status] IN (3, 4, 5) THEN [ClosedAtUtc] END,
                (SELECT MAX(e.[AtUtc]) FROM [ActionEvents] AS e WHERE e.[ActionId] = [Actions].[Id] AND (
                     ([Actions].[Status] = 0 AND (e.[Text] LIKE N'Status: % → Open%' OR e.[Text] LIKE N'%: Status: % → Open%'))
                     OR ([Actions].[Status] = 1 AND (e.[Text] LIKE N'Status: % → In progress%' OR e.[Text] LIKE N'%: Status: % → In progress%' OR e.[Text] LIKE N'Took it on and started it%'))
                     OR ([Actions].[Status] = 2 AND (e.[Text] LIKE N'Status: % → Blocked%' OR e.[Text] LIKE N'%: Status: % → Blocked%'))
                     OR ([Actions].[Status] = 6 AND (e.[Text] LIKE N'Status: % → Tracking%' OR e.[Text] LIKE N'%: Status: % → Tracking%')))),
                [CreatedAtUtc])
            WHERE [StatusChangedAtUtc] IS NULL;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StatusChangedAtUtc",
                table: "Actions");
        }
    }
}
