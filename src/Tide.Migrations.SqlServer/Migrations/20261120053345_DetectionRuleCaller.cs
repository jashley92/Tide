using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
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
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            // An API rule's caller is the start of its id, up to the caller mark (U+001F), as DetectionRule.CallerOf reads it;
            // an imported rule, and an API rule pushed before callers were kept, has none. A binary collation finds the mark,
            // which a linguistic one may ignore, and both CHARINDEX and LEFT then count UTF-16 units as C# does (a
            // supplementary-character collation counts an emoji once, so its LEFT would take more than the caller). In EXEC, so
            // it compiles only once the column exists, in any script of this.
            migrationBuilder.Sql("""
            EXEC(N'UPDATE [DetectionRules] SET [Caller] = LEFT([ExternalId] COLLATE Latin1_General_BIN2, CHARINDEX(NCHAR(31), [ExternalId] COLLATE Latin1_General_BIN2) - 1)
            WHERE [Source] = 3 AND CHARINDEX(NCHAR(31), [ExternalId] COLLATE Latin1_General_BIN2) > 1;');
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
