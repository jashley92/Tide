using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>
/// Migrations that fill a new column from what's already there, run over real rows: the database is migrated, rows are
/// written, it's taken back to the migration before and brought forward again, as an upgrade from that release would.
/// Each provider runs its own migrations (TestDb's SQL Server database is made from the model, so it can't).
/// </summary>
public class MigrationFillTests
{
    private static readonly DateTimeOffset Decided = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ScreenshotAndOutcomeTimes_AreFilledFromTheAuditTrail(string provider)
    {
        using var database = new MigratedDatabase(provider);
        var (legacy, current, send) = (new Article { Title = "Reviewed on alpha.9", AuthorId = "x", AuthorName = "X" },
            new Article { Title = "Reviewed since", AuthorId = "x", AuthorName = "X" },
            new BriefSend { Edition = Edition.Team, Status = SendStatus.Sent, Subject = "Daily brief", SentAtUtc = Decided, SentById = "x", SentByName = "X" });
        await using (var ctx = database.Context())
        {
            var brief = new Brief { BriefDate = new DateOnly(2026, 11, 2), AnalystName = "Robin" };
            (send.BriefId, send.BriefDate) = (brief.Id, brief.BriefDate);
            ctx.AddRange(brief, send, legacy, current);
            ctx.ArticleReviews.AddRange(
                new ArticleReview { ArticleId = legacy.Id, Status = ReviewStatus.Approved, DecidedAtUtc = Decided, ReviewedVersion = legacy.EditVersion },
                new ArticleReview { ArticleId = current.Id, Status = ReviewStatus.Approved, DecidedAtUtc = Decided, ReviewedVersion = Guid.NewGuid() });
            ctx.AuditLog.AddRange(
                Audit(nameof(BriefSend), "Modified", send.Id, Decided.AddMinutes(20), """{"Status":["Unknown","Sent"]}"""),
                Audit(nameof(BriefSend), "Modified", send.Id, Decided.AddMinutes(30), """{"Error":[null,"Checked"]}"""),
                Audit(nameof(ArticleScreenshot), "Added", Guid.NewGuid(), Decided.AddMinutes(5), $$"""{"ArticleId":[null,"{{legacy.Id}}"]}"""),
                Audit(nameof(ArticleScreenshot), "Deleted", Guid.NewGuid(), Decided.AddMinutes(40), $$"""{"ArticleId":["{{legacy.Id}}",null]}"""),
                Audit(nameof(ArticleScreenshot), "Added", Guid.NewGuid(), Decided.AddMinutes(50), $$"""{"ArticleId":[null,"{{current.Id}}"]}"""));
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("ScreenshotAndOutcomeTimes");

        await using (var ctx = database.Context())
        {
            (await ctx.BriefSends.SingleAsync()).OutcomeAtUtc.Should().Be(Decided.AddMinutes(30), "its row's last change");
            var articles = await ctx.Articles.ToDictionaryAsync(a => a.Id);
            articles[legacy.Id].ScreenshotsChangedAtUtc.Should().Be(Decided.AddMinutes(40), "its last screenshot change");
            articles[current.Id].ScreenshotsChangedAtUtc.Should().BeNull("its review records the content version, which covers screenshots");
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task BriefReopenAndNotSent_RecordsWhatAPresentedBriefsMeetingMarked(string provider)
    {
        using var database = new MigratedDatabase(provider);
        Article Made(string title, ArticleStatus status) => new() { Title = title, AuthorId = "x", AuthorName = "X", Status = status };
        var (earlier, fresh, carried, sentOnly) = (Made("Presented on Friday", ArticleStatus.Presented), Made("Presented on Monday", ArticleStatus.Presented),
            Made("Carried on", ArticleStatus.Ready), Made("On a sent brief", ArticleStatus.Presented));
        var friday = new Brief { BriefDate = new DateOnly(2026, 10, 23), AnalystName = "Robin", Status = BriefStatus.Sent };
        var monday = new Brief { BriefDate = new DateOnly(2026, 10, 26), AnalystName = "Robin", Status = BriefStatus.Presented };
        await using (var ctx = database.Context())
        {
            ctx.AddRange(earlier, fresh, carried, sentOnly, friday, monday);
            ctx.BriefItems.AddRange(
                new BriefItem { BriefId = friday.Id, ArticleId = earlier.Id }, new BriefItem { BriefId = friday.Id, ArticleId = sentOnly.Id },
                new BriefItem { BriefId = monday.Id, ArticleId = earlier.Id, SortOrder = 0 }, new BriefItem { BriefId = monday.Id, ArticleId = fresh.Id, SortOrder = 1 },
                new BriefItem { BriefId = monday.Id, ArticleId = carried.Id, SortOrder = 2 });
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("BriefReopenAndNotSent");

        await using (var ctx = database.Context())
        {
            var items = await ctx.BriefItems.AsNoTracking().ToListAsync();
            items.Single(i => i.BriefId == monday.Id && i.ArticleId == fresh.Id).StatusBeforeMeeting.Should().Be(ArticleStatus.Ready, "Monday's meeting presented it");
            items.Single(i => i.BriefId == monday.Id && i.ArticleId == earlier.Id).StatusBeforeMeeting.Should().BeNull("Friday's brief presented it first");
            items.Single(i => i.BriefId == monday.Id && i.ArticleId == carried.Id).StatusBeforeMeeting.Should().BeNull("it wasn't marked presented");
            items.Where(i => i.BriefId == friday.Id).Should().OnlyContain(i => i.StatusBeforeMeeting == null, "a sent brief can't be reopened");
        }
    }

    private static AuditLogEntry Audit(string type, string action, Guid key, DateTimeOffset at, string changes) => new()
    {
        AtUtc = at, UserId = "x", UserName = "X", Action = action, EntityType = type, EntityKey = key.ToString(), ChangesJson = changes,
    };

    // A database on one provider, migrated as the app migrates its own, and dropped afterwards.
    private sealed class MigratedDatabase : IDisposable
    {
        private readonly SqliteConnection? _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public MigratedDatabase(string provider)
        {
            var b = new DbContextOptionsBuilder<AppDbContext>();
            if (provider == TestDb.SqlServer)
                b.UseSqlServer(SqlServerFactAttribute.NewDatabase(), sql => sql.MigrationsAssembly(AppDbContextFactory.SqlServerMigrationsAssembly));
            else
            {
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                b.UseSqlite(_connection);
            }
            _options = b.Options;
            using var db = Context();
            db.Database.Migrate();
        }

        public AppDbContext Context() => new(_options);

        /// <summary>Takes the database back to the migration before <paramref name="name"/>, then up to the latest again.</summary>
        public void UpgradeAgainFrom(string name)
        {
            using var db = Context();
            var all = db.Database.GetMigrations().ToList();
            var at = all.FindIndex(m => m.EndsWith("_" + name, StringComparison.Ordinal));
            at.Should().BePositive($"{name} is a migration with one before it");
            var migrator = db.GetService<IMigrator>();
            migrator.Migrate(all[at - 1]);
            migrator.Migrate();
        }

        public void Dispose()
        {
            if (_connection is not null) _connection.Dispose();
            else
            {
                using var db = Context();
                db.Database.EnsureDeleted();
            }
        }
    }
}
