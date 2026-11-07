using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
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
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "EditVersion",
                table: "Playbooks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "ModifiedByName",
                table: "Playbooks",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            // Playbooks are the team's now (EP-89), kept as the saved queries are. Retired is the old Active turned over;
            // who made and last changed each is filled once from the audit trail, the starters (made by the system at
            // startup) as TIDE's.
            migrationBuilder.Sql("""
                UPDATE Playbooks SET IsRetired = CASE WHEN IsRetired = 0 THEN 1 ELSE 0 END;
                """);
            migrationBuilder.Sql("""
                UPDATE p SET CreatedByName = CASE WHEN p.CreatedBy = N'system' THEN N'TIDE' ELSE COALESCE((
                    SELECT TOP 1 a.UserName FROM AuditLog a
                    WHERE a.EntityType = N'Playbook' AND a.Action = N'Added' AND a.EntityKey = LOWER(CONVERT(nvarchar(36), p.Id))
                    ORDER BY a.AtUtc), N'') END
                FROM Playbooks p;
                """);
            migrationBuilder.Sql("""
                UPDATE p SET ModifiedByName = (
                    SELECT TOP 1 a.UserName FROM AuditLog a
                    WHERE a.EntityType = N'Playbook' AND a.Action = N'Modified' AND a.EntityKey = LOWER(CONVERT(nvarchar(36), p.Id))
                    ORDER BY a.AtUtc DESC)
                FROM Playbooks p
                WHERE p.ModifiedAtUtc IS NOT NULL;
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
                UPDATE Playbooks SET IsActive = CASE WHEN IsActive = 0 THEN 1 ELSE 0 END;
                """);
        }
    }
}
