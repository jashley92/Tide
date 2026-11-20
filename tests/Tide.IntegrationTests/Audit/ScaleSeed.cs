using Microsoft.EntityFrameworkCore;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests.Audit;

/// <summary>What <see cref="ScaleSeed.SeedAsync"/> made that a call needs to name.</summary>
public sealed record ScaleData(Guid StoryId, Guid TeamA, Guid TeamB);

/// <summary>
/// A few years of a busy team, for the scale harness (<see cref="ScaleAuditTests"/>) and the query budget
/// (<see cref="QueryBudgetTests"/>). At <c>factor</c> 1: 10k articles (half briefed), 2k briefs, 5k actions with comments,
/// events and checklists, 50k feed items in every state, 2k metric values, a 3-person rotation. Everything is dated from
/// the database's clock and drawn from a fixed random seed, so the same clock and factor give the same data.
/// </summary>
public static class ScaleSeed
{
    public static async Task<ScaleData> SeedAsync(TestDb db, double factor = 1)
    {
        int N(int count) => Math.Max(1, (int)(count * factor));
        var (articleCount, briefCount, actionCount, feedCount) = (N(10_000), N(2_000), N(5_000), N(50_000));

        await db.Scorecard().SeedStarterSetAsync();
        var today = db.Clock.Today;
        var now = db.Clock.Now.ToUniversalTime();
        var rnd = new Random(42);

        List<Guid> tagIds;
        Guid storyId;
        await using (var ctx = db.PlainContext())
        {
            tagIds = await ctx.ScorecardTags.OrderBy(t => t.Key).Select(t => t.Id).ToListAsync();
            var story = new Story { Name = "Edge VPN exploitation", CreatedAtUtc = now };
            storyId = story.Id;
            ctx.Stories.Add(story);
            await ctx.SaveChangesAsync();
        }
        var techniques = new[] { "T1190", "T1566", "T1566.002", "T1059", "T1059.001", "T1133", "T1486", "T1078", "T1021", "T1041" };

        // Briefs: one per business day going back (2,000 of them, ~8 years, at 1x), each carrying five articles.
        var briefDays = new List<DateOnly>();
        for (var d = today; briefDays.Count < briefCount; d = d.AddDays(-1))
            if (Tide.Application.Org.WorkingDays.Weekdays.IsWorkingDay(d)) briefDays.Add(d);

        var articleIds = new List<Guid>();
        for (var start = 0; start < articleCount; start += 1000)
        {
            await using var ctx = db.PlainContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var n = start; n < Math.Min(start + 1000, articleCount); n++)
            {
                var briefed = n < articleCount / 2;
                var day = briefed ? briefDays[n / 5 % briefDays.Count] : today.AddDays(-rnd.Next(0, 730));
                var a = new Article
                {
                    Title = $"Article {n}: {(n % 3 == 0 ? "ransomware gang hits vendor" : n % 3 == 1 ? "phishing wave" : "zero-day exploited")}",
                    Bluf = "Bottom line " + n + (n % 7 == 0 ? " ransomware vendor" : ""), SourceName = "Vendor " + n % 50, SourceUrl = $"https://e.com/{n}",
                    Tlp = (TlpLevel)(n % 4), Classification = n % 2 == 0 ? Classification.Threat : Classification.Informational,
                    Priority = n % 2 == 0 ? (Priority)(n % 4) : null, Status = briefed ? ArticleStatus.Presented : n % 10 == 0 ? ArticleStatus.Draft : ArticleStatus.Ready,
                    AuthorId = "S-1-5-21-TEST-1", AuthorName = "Test Analyst",
                    CreatedAtUtc = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero), StoryId = n % 500 == 0 ? storyId : null,
                };
                a.Bullets.Add(new ArticleBullet { Text = "Bullet one of " + n, SortOrder = 0 });
                a.Bullets.Add(new ArticleBullet { Text = "Bullet two **bold** of " + n, SortOrder = 1 });
                a.Indicators.Add(new ArticleIndicator { Type = IndicatorType.IpAddress, Value = $"10.{n / 256 % 256}.{n % 256}.9" });
                a.Indicators.Add(new ArticleIndicator { Type = IndicatorType.Domain, Value = $"c2-{n}.example.net" });
                a.Cves.Add(new ArticleCve { CveId = $"CVE-2025-{n % 300:0000}" });
                a.Tags.Add(new ArticleTag { TagId = tagIds[n % tagIds.Count] });
                if (n % 2 == 0) a.Tags.Add(new ArticleTag { TagId = tagIds[(n + 3) % tagIds.Count] });
                a.Techniques.Add(new ArticleTechnique { TechniqueId = techniques[n % techniques.Length] });
                if (n % 3 == 0) a.Techniques.Add(new ArticleTechnique { TechniqueId = techniques[(n + 1) % techniques.Length] });
                ctx.Articles.Add(a);
                articleIds.Add(a.Id);
            }
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.PlainContext())
        {
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var b = 0; b < briefDays.Count; b++)
            {
                var brief = new Brief
                {
                    BriefDate = briefDays[b], AnalystId = "S-1-5-21-TEST-1", AnalystName = "Test Analyst", Status = b == 0 ? BriefStatus.Draft : BriefStatus.Sent,
                    CreatedAtUtc = now, Intro = "Opening note",
                };
                for (var k = 0; k < 5; k++) brief.Items.Add(new BriefItem { ArticleId = articleIds[(b * 5 + k) % articleIds.Count], SortOrder = k });
                ctx.Briefs.Add(brief);
            }
            await ctx.SaveChangesAsync();
        }

        Guid teamA, teamB;
        await using (var ctx = db.PlainContext())
        {
            var a = new Team { Key = "cyber-defense", Name = "Cyber Defense", CreatedAtUtc = now };
            var b = new Team { Key = "vuln-mgmt", Name = "Vulnerability Management", CreatedAtUtc = now, Email = "vm@example.org" };
            a.Members.Add(new TeamMember { UserId = "S-1-5-21-TEST-1", DisplayName = "Test Analyst", AddedAtUtc = now });
            ctx.Teams.AddRange(a, b);
            ctx.Users.Add(new AppUser { Sid = "S-1-5-21-TEST-1", DisplayName = "Test Analyst", Email = "test@example.com", LastSeenUtc = now });
            await ctx.SaveChangesAsync();
            (teamA, teamB) = (a.Id, b.Id);
        }

        for (var start = 0; start < actionCount; start += 1000)
        {
            await using var ctx = db.PlainContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var n = start; n < Math.Min(start + 1000, actionCount); n++)
            {
                var raised = today.AddDays(-rnd.Next(0, 730));
                var open = n % 5 == 0;
                var act = new ActionItem
                {
                    Number = n + 1, Title = "Action " + n, Kind = (ActionKind)(n % 5), Priority = (Priority)(n % 4), TeamId = n % 2 == 0 ? teamA : teamB,
                    ArticleId = articleIds[n % articleIds.Count], Status = open ? (ActionStatus)(n % 3) : ActionStatus.Done,
                    Outcome = open ? ActionOutcome.None : ActionOutcome.NoHits, DueDate = raised.AddDays(n % 9), AssigneeId = n % 4 == 0 ? "S-1-5-21-TEST-1" : null,
                    AssigneeName = n % 4 == 0 ? "Test Analyst" : null, ClosedDate = open ? null : raised.AddDays(5), Description = "Do the thing " + n,
                    CreatedAtUtc = new DateTimeOffset(raised.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero),
                    ClosedAtUtc = open ? null : new DateTimeOffset(raised.AddDays(5).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero),
                };
                act.Comments.Add(new ActionComment { Text = "Looked at it", AuthorId = "S-1-5-21-TEST-1", AuthorName = "Test Analyst", AtUtc = now });
                act.Comments.Add(new ActionComment { Text = "Ransomware vendor note " + n, AuthorId = "S-1-5-21-TEST-1", AuthorName = "Test Analyst", AtUtc = now });
                for (var e = 0; e < 3; e++) act.Events.Add(new ActionEvent { Text = "Event " + e, ByName = "Test Analyst", AtUtc = now.AddMinutes(e) });
                for (var c = 0; c < 3; c++) act.Checklist.Add(new ActionChecklistItem { Text = "Step " + c, SortOrder = c, IsDone = c == 0 });
                if (n % 10 == 0) act.Hunts.Add(new HuntQuery { Dialect = "kql", Text = "x", GeneratedText = "x", Result = HuntResult.Hits, RunAtUtc = now, CreatedAtUtc = now, CreatedByName = "T" });
                ctx.Actions.Add(act);
            }
            await ctx.SaveChangesAsync();
        }

        Guid sourceId;
        await using (var ctx = db.PlainContext())
        {
            var s = new Source { Name = "Big feed", Url = "https://feed.example/rss", CreatedAtUtc = now };
            ctx.Sources.Add(s);
            await ctx.SaveChangesAsync();
            sourceId = s.Id;
        }
        for (var start = 0; start < feedCount; start += 5000)
        {
            await using var ctx = db.PlainContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var n = start; n < Math.Min(start + 5000, feedCount); n++)
            {
                var status = (n % 10) switch { 0 => FeedItemStatus.New, 1 => FeedItemStatus.Shortlisted, 2 or 3 => FeedItemStatus.Promoted, 4 => FeedItemStatus.Duplicate, _ => FeedItemStatus.Dismissed };
                var url = $"https://news.example/{n}";
                ctx.FeedItems.Add(new FeedItem
                {
                    SourceId = sourceId, Url = url, UrlHash = UrlCanonicalizer.Hash(url), Title = $"Feed item {n} about CVE-2025-{n % 300:0000}",
                    TitleKey = TitleMatch.Key($"Feed item {n} about CVE-2025-{n % 300:0000}"), Excerpt = new string('x', 1500), Cves = $"CVE-2025-{n % 300:0000}",
                    PublishedAtUtc = now.AddHours(-n), FetchedAtUtc = now.AddHours(-n), Status = status,
                    ArticleId = status == FeedItemStatus.Promoted ? articleIds[n % articleIds.Count] : null,
                    StatusAtUtc = status == FeedItemStatus.New ? null : now, StatusByName = status == FeedItemStatus.New ? null : "Test Analyst",
                });
            }
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.PlainContext())
        {
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            var group = new MetricGroup { Key = "queue", Name = "Alert queue", CreatedAtUtc = now };
            var metrics = Enumerable.Range(0, 4).Select(i => new MetricDefinition { Key = "m" + i, Name = "Metric " + i, GroupId = group.Id, SortOrder = i, CreatedAtUtc = now }).ToList();
            ctx.MetricGroups.Add(group);
            ctx.MetricDefinitions.AddRange(metrics);
            foreach (var m in metrics)
                for (var k = 0; k < Math.Min(N(500), briefDays.Count); k++)
                    ctx.MetricValues.Add(new MetricValue { DefinitionId = m.Id, BriefDate = briefDays[k], Value = k % 37, EnteredById = "x", EnteredByName = "x", CreatedAtUtc = now });
            var roster = new[] { ("S-1-5-21-TEST-1", "Test Analyst"), ("dev:robin", "Robin"), ("dev:sam", "Sam") };
            for (var i = 0; i < roster.Length; i++) ctx.RotationMembers.Add(new RotationMember { UserId = roster[i].Item1, DisplayName = roster[i].Item2, SortOrder = i });
            var monday = Tide.Application.Rotation.RotationService.WeekStart(today.AddDays(-365));
            for (var w = 0; w < 110; w++)
                ctx.RotationWeeks.Add(new RotationWeek { WeekStart = monday.AddDays(7 * w), AnalystId = roster[w % 3].Item1, AnalystName = roster[w % 3].Item2, UpdatedAtUtc = now, UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }
        return new ScaleData(storyId, teamA, teamB);
    }
}
