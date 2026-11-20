using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DetectionRuleCaller : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Caller",
                table: "DetectionRules",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            // An API rule's caller is the start of its id, up to the caller mark (U+001F), as DetectionRule.CallerOf reads it;
            // an imported rule, and an API rule pushed before callers were kept, has none.
            migrationBuilder.Sql("""
            UPDATE [DetectionRules] SET [Caller] = substr([ExternalId], 1, instr([ExternalId], char(31)) - 1)
            WHERE [Source] = 3 AND instr([ExternalId], char(31)) > 1;
            """);

            migrationBuilder.CreateIndex(
                name: "IX_DetectionRules_Source_Caller",
                table: "DetectionRules",
                columns: new[] { "Source", "Caller" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DetectionRules_Source_Caller",
                table: "DetectionRules");

            migrationBuilder.DropColumn(
                name: "Caller",
                table: "DetectionRules");
        }
    }
}
