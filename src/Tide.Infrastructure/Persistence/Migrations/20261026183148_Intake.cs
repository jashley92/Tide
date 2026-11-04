using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tide.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CveRecords",
                columns: table => new
                {
                    CveId = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CvssScore = table.Column<decimal>(type: "TEXT", precision: 4, scale: 1, nullable: true),
                    CvssSeverity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    CvssVector = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    NvdPublishedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    EpssScore = table.Column<decimal>(type: "TEXT", precision: 8, scale: 5, nullable: true),
                    EpssPercentile = table.Column<decimal>(type: "TEXT", precision: 8, scale: 5, nullable: true),
                    IsKev = table.Column<bool>(type: "INTEGER", nullable: false),
                    KevAddedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    KevDueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    KevRansomware = table.Column<bool>(type: "INTEGER", nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Product = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    NvdCheckedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    EpssCheckedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    KevCheckedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CveRecords", x => x.CveId);
                });

            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DefaultTlp = table.Column<int>(type: "INTEGER", nullable: false),
                    Reliability = table.Column<int>(type: "INTEGER", nullable: false),
                    LicenseNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PollMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRetired = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastPolledAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastNewItems = table.Column<int>(type: "INTEGER", nullable: false),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastModified = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UrlHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TitleKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PublishedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    FetchedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Excerpt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Cves = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DismissReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DuplicateOfId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SimilarToId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StatusByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    StatusAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
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
