using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Intake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StoryId",
                table: "Articles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CveRecords",
                columns: table => new
                {
                    CveId = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CvssScore = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    CvssSeverity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CvssVector = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NvdPublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EpssScore = table.Column<decimal>(type: "decimal(8,5)", precision: 8, scale: 5, nullable: true),
                    EpssPercentile = table.Column<decimal>(type: "decimal(8,5)", precision: 8, scale: 5, nullable: true),
                    IsKev = table.Column<bool>(type: "bit", nullable: false),
                    KevAddedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    KevDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    KevRansomware = table.Column<bool>(type: "bit", nullable: false),
                    Vendor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Product = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NvdCheckedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EpssCheckedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    KevCheckedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CveRecords", x => x.CveId);
                });

            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DefaultTlp = table.Column<int>(type: "int", nullable: false),
                    Reliability = table.Column<int>(type: "int", nullable: false),
                    LicenseNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PollMinutes = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    LastPolledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LastNewItems = table.Column<int>(type: "int", nullable: false),
                    ETag = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastModified = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    UrlHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TitleKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FetchedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Excerpt = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Cves = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DismissReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DuplicateOfId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SimilarToId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StatusAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedItems_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FeedItems_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_StoryId",
                table: "Articles",
                column: "StoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CveRecords_IsKev",
                table: "CveRecords",
                column: "IsKev");

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_ArticleId",
                table: "FeedItems",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_FetchedAtUtc",
                table: "FeedItems",
                column: "FetchedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_SourceId",
                table: "FeedItems",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_Status_FetchedAtUtc",
                table: "FeedItems",
                columns: new[] { "Status", "FetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_UrlHash",
                table: "FeedItems",
                column: "UrlHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_Name",
                table: "Sources",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stories_Status",
                table: "Stories",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_Articles_Stories_StoryId",
                table: "Articles",
                column: "StoryId",
                principalTable: "Stories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Articles_Stories_StoryId",
                table: "Articles");

            migrationBuilder.DropTable(
                name: "CveRecords");

            migrationBuilder.DropTable(
                name: "FeedItems");

            migrationBuilder.DropTable(
                name: "Stories");

            migrationBuilder.DropTable(
                name: "Sources");

            migrationBuilder.DropIndex(
                name: "IX_Articles_StoryId",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "StoryId",
                table: "Articles");
        }
    }
}
