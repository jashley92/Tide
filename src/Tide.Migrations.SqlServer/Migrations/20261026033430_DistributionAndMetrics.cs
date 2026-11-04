using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DistributionAndMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BriefSends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Edition = table.Column<int>(type: "int", nullable: false),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    Ceiling = table.Column<int>(type: "int", nullable: false),
                    Marking = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentById = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SentByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EmailHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WebHtml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BriefSends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BriefSends_Briefs_BriefId",
                        column: x => x.BriefId,
                        principalTable: "Briefs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MetricGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BriefSendImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SendId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BriefSendImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BriefSendImages_BriefSends_SendId",
                        column: x => x.SendId,
                        principalTable: "BriefSends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BriefSendImages_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MetricDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Format = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    WarnThreshold = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CriticalThreshold = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    FlagMissing = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricDefinitions_MetricGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "MetricGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MetricValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BriefDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    EnteredById = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EnteredByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IsOverride = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricValues_MetricDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "MetricDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BriefSendImages_FileId",
                table: "BriefSendImages",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BriefSendImages_SendId",
                table: "BriefSendImages",
                column: "SendId");

            migrationBuilder.CreateIndex(
                name: "IX_BriefSends_BriefId_SentAtUtc",
                table: "BriefSends",
                columns: new[] { "BriefId", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BriefSends_Edition_IsTest_Status_BriefDate",
                table: "BriefSends",
                columns: new[] { "Edition", "IsTest", "Status", "BriefDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricDefinitions_GroupId",
                table: "MetricDefinitions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricDefinitions_Key",
                table: "MetricDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricGroups_Key",
                table: "MetricGroups",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricValues_BriefDate",
                table: "MetricValues",
                column: "BriefDate");

            migrationBuilder.CreateIndex(
                name: "IX_MetricValues_DefinitionId_BriefDate",
                table: "MetricValues",
                columns: new[] { "DefinitionId", "BriefDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BriefSendImages");

            migrationBuilder.DropTable(
                name: "MetricValues");

            migrationBuilder.DropTable(
                name: "BriefSends");

            migrationBuilder.DropTable(
                name: "MetricDefinitions");

            migrationBuilder.DropTable(
                name: "MetricGroups");
        }
    }
}
