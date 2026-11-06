using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutreachEmailsAndStackContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "StackProducts",
                type: "TEXT",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactName",
                table: "StackProducts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OutreachTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Ceiling = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsRetired = table.Column<bool>(type: "INTEGER", nullable: false),
                    EditVersion = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutreachTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutreachMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemplateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TemplateName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    To = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 40000, nullable: false),
                    Tlp = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SentById = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SentByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RepliedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ReplyRecordedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ReplyNotes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ReplyFileId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutreachMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutreachMessages_Actions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "Actions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OutreachMessages_OutreachTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "OutreachTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutreachMessages_ActionId",
                table: "OutreachMessages",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_OutreachMessages_RepliedAtUtc",
                table: "OutreachMessages",
                column: "RepliedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutreachMessages_TemplateId",
                table: "OutreachMessages",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_OutreachTemplates_Name",
                table: "OutreachTemplates",
                column: "Name",
                unique: true);

            // Vendor inquiry became Outreach (ADR 0019). Actions keep their kind (it's stored as its number), but an
            // administrator's SLA target for the kind was saved under its name: it moves to the new one, unless a target
            // is already saved there.
            migrationBuilder.Sql("""
                UPDATE Settings SET "Key" = 'Actions:Sla:Kind:Outreach'
                WHERE "Key" = 'Actions:Sla:Kind:VendorInquiry'
                  AND NOT EXISTS (SELECT 1 FROM Settings s WHERE s."Key" = 'Actions:Sla:Kind:Outreach');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Settings SET "Key" = 'Actions:Sla:Kind:VendorInquiry'
                WHERE "Key" = 'Actions:Sla:Kind:Outreach'
                  AND NOT EXISTS (SELECT 1 FROM Settings s WHERE s."Key" = 'Actions:Sla:Kind:VendorInquiry');
                """);

            migrationBuilder.DropTable(
                name: "OutreachMessages");

            migrationBuilder.DropTable(
                name: "OutreachTemplates");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "StackProducts");

            migrationBuilder.DropColumn(
                name: "ContactName",
                table: "StackProducts");
        }
    }
}
