using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlaybooksTeamLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsActive",
                table: "Playbooks",
                newName: "IsRetired");

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "Playbooks",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "EditVersion",
                table: "Playbooks",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "ModifiedByName",
                table: "Playbooks",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            // Playbooks are the team's now (EP-89), kept as the saved queries are. Retired is the old Active turned over;
            // who made and last changed each is filled once from the audit trail, the starters (made by the system at
            // startup) as TIDE's.
            migrationBuilder.Sql("""
                UPDATE "Playbooks" SET "IsRetired" = CASE WHEN "IsRetired" = 0 THEN 1 ELSE 0 END;
                """);
            migrationBuilder.Sql("""
                UPDATE "Playbooks" SET "CreatedByName" = CASE WHEN "CreatedBy" = 'system' THEN 'TIDE' ELSE COALESCE((
                    SELECT a."UserName" FROM "AuditLog" a
                    WHERE a."EntityType" = 'Playbook' AND a."Action" = 'Added' AND a."EntityKey" = lower("Playbooks"."Id")
                    ORDER BY a."AtUtc" LIMIT 1), '') END;
                """);
            migrationBuilder.Sql("""
                UPDATE "Playbooks" SET "ModifiedByName" = (
                    SELECT a."UserName" FROM "AuditLog" a
                    WHERE a."EntityType" = 'Playbook' AND a."Action" = 'Modified' AND a."EntityKey" = lower("Playbooks"."Id")
                    ORDER BY a."AtUtc" DESC LIMIT 1)
                WHERE "ModifiedAtUtc" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "Playbooks");

            migrationBuilder.DropColumn(
                name: "EditVersion",
                table: "Playbooks");

            migrationBuilder.DropColumn(
                name: "ModifiedByName",
                table: "Playbooks");

            migrationBuilder.RenameColumn(
                name: "IsRetired",
                table: "Playbooks",
                newName: "IsActive");

            migrationBuilder.Sql("""
                UPDATE "Playbooks" SET "IsActive" = CASE WHEN "IsActive" = 0 THEN 1 ELSE 0 END;
                """);
        }
    }
}
