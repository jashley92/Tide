using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoredSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Protected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    SavedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredSecrets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredSecrets_Key",
                table: "StoredSecrets",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredSecrets");
        }
    }
}
