using AwesomeAssertions;
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

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task OutreachEmailsAndStackContacts_MovesVendorInquirysSlaTarget_AndItsActionsAreOutreachActions(string provider)
    {
        using var database = new MigratedDatabase(provider);
        var team = new Team { Key = "network", Name = "Network" };
        var asked = new ActionItem { Number = 1, Title = "Ask the vendor", Kind = (ActionKind)6, Team = team };
        await using (var ctx = database.Context())
        {
            ctx.AddRange(team, asked);
            ctx.Settings.AddRange(
                new AppSetting { Key = "Actions:Sla:Kind:VendorInquiry", Value = "4", UpdatedAtUtc = Decided, UpdatedBy = "x" },
                new AppSetting { Key = "Actions:Sla:Kind:Block", Value = "1", UpdatedAtUtc = Decided, UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("OutreachEmailsAndStackContacts");

        await using (var ctx = database.Context())
        {
            (await ctx.Settings.AsNoTracking().OrderBy(s => s.Key).Select(s => s.Key + "=" + s.Value).ToListAsync())
                .Should().Equal("Actions:Sla:Kind:Block=1", "Actions:Sla:Kind:Outreach=4");
            (await ctx.Actions.AsNoTracking().SingleAsync()).Kind.Should().Be(ActionKind.Outreach, "the kind is stored as its number");
        }
    }

    private static AuditLogEntry Audit(string type, string action, Guid key, DateTimeOffset at, string changes) => new()
    {
        AtUtc = at, UserId = "x", UserName = "X", Action = action, EntityType = type, EntityKey = key.ToString(), ChangesJson = changes,
    };

    // The same, by someone named.
    private static AuditLogEntry By(string name, AuditLogEntry e)
    {
        e.UserName = name;
        return e;
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task HandoffsTrackingAndExternalTeams_GivesExistingTeamsTheReminderDefaults_AndLeavesThemInternalWithNoLimit(string provider)
    {
        using var database = new MigratedDatabase(provider);
        await using (var ctx = database.Context())
        {
            ctx.Teams.Add(new Team { Key = "network-engineering", Name = "Network Engineering" });
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("HandoffsTrackingAndExternalTeams");

        await using (var ctx = database.Context())
        {
            var team = await ctx.Teams.SingleAsync();
            (team.IsExternal, team.ActiveLimit, team.LimitMode).Should().Be((false, (int?)null, ActiveLimitMode.Show));
            (team.RemindersOn, team.RemindBeforeDays, team.RemindOverdueEveryDays).Should().Be((true, 2, (int?)3));
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task EscalationLadder_LeavesExistingTeamsWithNoContact_AndExistingActionsNotEscalated(string provider)
    {
        using var database = new MigratedDatabase(provider);
        await using (var ctx = database.Context())
        {
            var team = new Team { Key = "network-engineering", Name = "Network Engineering", IsExternal = true, ContactEmail = "network@example.org" };
            ctx.Teams.Add(team);
            ctx.Actions.Add(new ActionItem { Number = 7, Title = "Block the domains", TeamId = team.Id, DueDate = new DateOnly(2026, 11, 2) });
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("EscalationLadder");

        await using (var ctx = database.Context())
        {
            var team = await ctx.Teams.SingleAsync();
            (team.EscalationName, team.EscalationEmail, team.ContactEmail).Should().Be(((string?)null, (string?)null, "network@example.org"));
            (await ctx.Actions.SingleAsync()).Should().Match<ActionItem>(a => a.EscalatedForDue == null && a.EscalatedAtUtc == null && !a.IsEscalated,
                "nothing is escalated until the ladder, turned on, says so");
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task PlaybooksTeamLibrary_TurnsActiveIntoRetired_AndNamesWhoMadeAndChangedEach(string provider)
    {
        using var database = new MigratedDatabase(provider);
        var starter = new Playbook { Name = "IOC sweep", CreatedBy = "system", CreatedAtUtc = Decided };
        var admins = new Playbook { Name = "Old sweep", CreatedBy = "S-1-ADA", CreatedAtUtc = Decided, ModifiedAtUtc = Decided.AddHours(1), IsRetired = true };
        await using (var ctx = database.Context())
        {
            ctx.Playbooks.AddRange(starter, admins);
            ctx.AuditLog.AddRange(
                By("Ada Admin", Audit(nameof(Playbook), "Added", admins.Id, Decided, "{}")),
                By("Bo", Audit(nameof(Playbook), "Modified", admins.Id, Decided.AddMinutes(30), "{}")),
                By("Cy", Audit(nameof(Playbook), "Modified", admins.Id, Decided.AddHours(1), "{}")));
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("PlaybooksTeamLibrary");

        await using (var ctx = database.Context())
        {
            var all = await ctx.Playbooks.ToDictionaryAsync(p => p.Id);
            (all[starter.Id].IsRetired, all[starter.Id].CreatedByName, all[starter.Id].ModifiedByName).Should().Be((false, "TIDE", (string?)null), "the starters are TIDE's");
            (all[admins.Id].IsRetired, all[admins.Id].CreatedByName, all[admins.Id].ModifiedByName).Should().Be((true, "Ada Admin", "Cy"),
                "an inactive one is retired, named from the audit trail: who added it, and who changed it last");
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ActionOriginalDueDate_StartsFromTheDateEachActionHas(string provider)
    {
        using var database = new MigratedDatabase(provider);
        var team = new Team { Key = "fill", Name = "Fill" };
        var dated = new ActionItem { Number = 1, Title = "Dated", Team = team, DueDate = new DateOnly(2026, 11, 2) };
        dated.DueDate = new DateOnly(2026, 11, 20);   // moved before the upgrade: the move isn't on record then
        var undated = new ActionItem { Number = 2, Title = "Undated", Team = team };
        await using (var ctx = database.Context())
        {
            ctx.AddRange(team, dated, undated);
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("ActionOriginalDueDate");

        await using (var ctx = database.Context())
        {
            var all = await ctx.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).ToDictionaryAsync(a => a.Number);
            (all[1].DueDate, all[1].OriginalDueDate, all[1].DueDateMoves).Should().Be((new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 20), 0),
                "an action from before starts from the date it has, with no moves counted");
            (all[2].DueDate, all[2].OriginalDueDate, all[2].DueDateMoves).Should().Be(((DateOnly?)null, (DateOnly?)null, 0));
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ActionStatusTimes_AreFilledFromWhatWasRecorded(string provider)
    {
        using var database = new MigratedDatabase(provider);
        var team = new Team { Key = "fill-team", Name = "Fill team" };
        var number = 0;
        ActionItem Made(ActionStatus status, int createdDaysAgo) => new()
        {
            Number = ++number, Title = $"Fill {number}", TeamId = team.Id, Status = status, CreatedAtUtc = Decided.AddDays(-createdDaysAgo),
        };
        var (never, moved, updated, taken, closed, misled) = (Made(ActionStatus.Open, 9), Made(ActionStatus.InProgress, 9), Made(ActionStatus.Blocked, 9),
            Made(ActionStatus.InProgress, 9), Made(ActionStatus.Done, 9), Made(ActionStatus.Blocked, 9));
        closed.ClosedAtUtc = Decided.AddDays(-1);
        ActionEvent Line(ActionItem a, int daysAgo, string text) => new() { ActionId = a.Id, AtUtc = Decided.AddDays(-daysAgo), ByName = "X", Text = text };
        await using (var ctx = database.Context())
        {
            ctx.Add(team);
            ctx.AddRange(never, moved, updated, taken, closed, misled);
            ctx.ActionEvents.AddRange(
                Line(never, 8, "Raised from the brief."),
                Line(moved, 7, "Status: Open → Blocked."), Line(moved, 5, "Status: Blocked → In progress."), Line(moved, 2, "Changed priority: P3 → P2."),
                Line(updated, 4, "Update from Network: waiting on the vendor. Status: In progress → Blocked."),
                Line(taken, 3, "Took it on and started it."),
                Line(closed, 6, "Status: Open → In progress."),
                // A person's words that only look like a move don't count.
                Line(misled, 2, "Deleted the checklist item \"Status: Open → Blocked\"."));
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("ActionStatusChangedAt");

        await using (var ctx = database.Context())
        {
            var at = await ctx.Actions.ToDictionaryAsync(a => a.Id, a => a.StatusChangedAtUtc);
            at[never.Id].Should().Be(Decided.AddDays(-9), "never moved: since it was raised");
            at[moved.Id].Should().Be(Decided.AddDays(-5), "its last move into the status it has");
            at[updated.Id].Should().Be(Decided.AddDays(-4), "a team's update that moved it");
            at[taken.Id].Should().Be(Decided.AddDays(-3), "taking it on started it");
            at[closed.Id].Should().Be(Decided.AddDays(-1), "when it was closed");
            at[misled.Id].Should().Be(Decided.AddDays(-9), "no move was recorded, so since it was raised");
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task WhyItMattersKept_LeavesEveryArticleUnkept_AndKeepsWhatIsKept(string provider)
    {
        using var database = new MigratedDatabase(provider);
        var threat = new Article
        {
            Title = "Written before", AuthorId = "x", AuthorName = "X", Classification = Classification.Threat,
            WhyItMatters = "We run two of these.", WhyItMattersTlp = TlpLevel.Clear,
        };
        await using (var ctx = database.Context())
        {
            ctx.Add(threat);
            await ctx.SaveChangesAsync();
        }

        database.UpgradeAgainFrom("WhyItMattersKept");

        await using (var ctx = database.Context())
        {
            var a = await ctx.Articles.SingleAsync();
            (a.WhyItMattersKeptTlp, a.WhyItMattersKeptHash, a.WhyItMattersKeptById, a.WhyItMattersKeptByName, a.WhyItMattersKeptAtUtc)
                .Should().Be(((TlpLevel?)null, (string?)null, (string?)null, (string?)null, (DateTimeOffset?)null), "nothing was kept before");
            Tide.Application.Articles.ArticleChecks.WhyReachesCompany(a, TlpLevel.Green).Should().NotBeNull("so its warning still shows");
            a.KeepWhyItMatters("dev:robin", "Robin", Decided);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = database.Context())
        {
            var a = await ctx.Articles.SingleAsync();
            (a.WhyItMattersKeptTlp, a.WhyItMattersKeptById, a.WhyItMattersKeptByName, a.WhyItMattersKeptAtUtc, a.WhyItMattersKept)
                .Should().Be(((TlpLevel?)TlpLevel.Clear, "dev:robin", "Robin", (DateTimeOffset?)Decided, true));
        }
    }

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
