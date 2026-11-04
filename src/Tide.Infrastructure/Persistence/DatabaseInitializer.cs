using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Application.Scorecard;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Security;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Run once at startup: applies migrations (unless a DBA applies the schema), seeds the AD-group→role mappings from
/// configuration on first run, adds the scorecard starter set to an empty install, and in Development adds a few sample
/// articles and today's brief so there's something to look at.
/// </summary>
public sealed class DatabaseInitializer(
    AppDbContext db,
    IOptions<RoleMappingOptions> roleMapping,
    ScorecardService scorecard,
    IRoleDirectory roles,
    IHostEnvironment env,
    IConfiguration config,
    OrgClock clock,
    ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Database:ApplyMigrations=false when the schema is applied by a DBA (the app pool has no db_owner).
        if (config.GetValue("Database:ApplyMigrations", true))
        {
            if (db.Database.IsSqlite())
                Directory.CreateDirectory(Path.Combine(env.ContentRootPath, "App_Data"));
            await db.Database.MigrateAsync(ct);
        }
        else if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
        {
            throw new InvalidOperationException("The database schema is behind this version of TIDE. Apply the schema script " +
                "for this release (deploy/sql), or set Database:ApplyMigrations=true.");
        }

        await HolidayService.LoadAsync(db, clock, ct);
        await SeedRoleMappingsAsync(ct);
        roles.Invalidate();
        await scorecard.SeedStarterSetAsync(ct);
        await CanonicalizeIndicatorsAsync(db, log, ct);
        if (env.IsDevelopment()) await SeedSamplesAsync(ct);
    }

    /// <summary>The setting that says the one-time pass over stored indicators has run.</summary>
    public const string IndicatorsCanonicalKey = "Data:IndicatorsCanonical";

    /// <summary>
    /// Brings indicators stored before addresses and domains had one spelling (a zero-padded IPv4 address as a log
    /// prints it, a domain in capitals) to the form a save stores now, once: the sighting lookup, exports and hunts
    /// compare the stored value as it is. Two rows of one article that become the same keep the first (and its note
    /// when the other has none). Logged, idempotent, and skipped after the marker is written. Returns the rows changed.
    /// </summary>
    public static async Task<int> CanonicalizeIndicatorsAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        if (await db.Settings.AnyAsync(s => s.Key == IndicatorsCanonicalKey, ct)) return 0;
        var types = new[] { IndicatorType.IpAddress, IndicatorType.Domain };
        var rows = await db.ArticleIndicators.Where(i => types.Contains(i.Type)).ToListAsync(ct);
        var changed = 0;
        var merged = 0;
        foreach (var article in rows.GroupBy(i => i.ArticleId))
        {
            var seen = new Dictionary<string, ArticleIndicator>(StringComparer.Ordinal);
            // The rows already in canonical form claim their key first, so a renamed row never displaces one.
            foreach (var row in article.OrderBy(i => i.Value == Domain.Observables.IocObservable.Normalize(i.Type, i.Value) ? 0 : 1))
            {
                var canonical = Domain.Observables.IocObservable.Normalize(row.Type, row.Value);
                if (canonical.Length == 0) canonical = row.Value;
                var key = $"{(int)row.Type}|{canonical}";
                if (seen.TryGetValue(key, out var kept))
                {
                    kept.Note ??= row.Note;
                    db.ArticleIndicators.Remove(row);
                    merged++;
                    continue;
                }
                seen[key] = row;
                if (row.Value == canonical) continue;
                row.Value = canonical;
                changed++;
            }
        }
        db.Settings.Add(new AppSetting { Key = IndicatorsCanonicalKey, Value = "1", UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = "system" });
        await db.SaveChangesAsync(ct);
        if (changed + merged > 0)
            log.LogInformation("Brought {Changed} stored indicator(s) to their canonical spelling and merged {Merged} duplicate(s).", changed, merged);
        return changed + merged;
    }

    private async Task SeedRoleMappingsAsync(CancellationToken ct)
    {
        if (await db.RoleMappings.AnyAsync(ct)) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var (roleName, groups) in roleMapping.Value.Groups)
        {
            if (!Enum.TryParse<AppRole>(roleName, ignoreCase: true, out var role))
            {
                log.LogWarning("RoleMapping:Groups names '{Role}', which isn't a TIDE role; skipped.", roleName);
                continue;
            }
            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                db.RoleMappings.Add(new AdGroupRoleMapping { AdGroup = group, RoleName = role.ToString(), UpdatedAtUtc = now, UpdatedBy = "system" });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSamplesAsync(CancellationToken ct)
    {
        if (await db.Articles.AnyAsync(ct)) return;
        var tags = await db.ScorecardTags.ToDictionaryAsync(t => t.Key, t => t.Id, ct);
        Guid Tag(string key) => tags.TryGetValue(key, out var id) ? id : Guid.Empty;

        var vpn = new Article
        {
            Title = "Edge VPN appliance zero-day exploited for initial access",
            SourceName = "Vendor advisory",
            SourceUrl = "https://example.com/advisories/vpn-zero-day",
            Tlp = TlpLevel.Clear,
            Classification = Classification.Threat,
            Priority = Priority.P1,
            Bluf = "An unauthenticated remote code execution flaw in a widely deployed VPN appliance is being exploited; we run the affected version.",
            WhyItMatters = "Our remote-access gateways run the affected firmware branch.",
            RecommendedActions = "- Apply the vendor hotfix to both gateways today.\n- Hunt for the listed indicators in firewall and VPN logs for the last 30 days.",
            Actors = "Unattributed",
            Status = ArticleStatus.Ready,
            AuthorId = "system",
            AuthorName = "Sample data",
        };
        vpn.Bullets.AddRange([
            new() { SortOrder = 0, Text = "Exploitation began at least **five days** before the advisory.", Tlp = TlpLevel.Clear },
            new() { SortOrder = 1, Text = "The vendor's integrity checker misses the implant; use the updated one.", Tlp = TlpLevel.Clear },
            new() { SortOrder = 2, Text = "An ISAC member reports post-exploitation credential harvesting from the appliance.", Tlp = TlpLevel.Amber },
        ]);
        vpn.Techniques.AddRange([new() { TechniqueId = "T1190" }, new() { TechniqueId = "T1133" }]);
        vpn.Cves.Add(new() { CveId = "CVE-2026-0001" });
        vpn.Indicators.AddRange([
            new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" },
            new() { Type = IndicatorType.Domain, Value = "update-check.example.net" },
        ]);
        foreach (var key in new[] { "actively-exploited", "zero-day" })
            if (Tag(key) is var id && id != Guid.Empty) vpn.Tags.Add(new() { TagId = id });

        var phish = new Article
        {
            Title = "Invoice-themed phishing wave targets accounts payable teams",
            SourceName = "Industry ISAC",
            Tlp = TlpLevel.Green,
            Classification = Classification.Informational,
            Bluf = "A sector-wide phishing campaign uses fake overdue invoices to harvest Microsoft 365 credentials.",
            Status = ArticleStatus.Ready,
            AuthorId = "system",
            AuthorName = "Sample data",
        };
        phish.Bullets.AddRange([
            new() { SortOrder = 0, Text = "Lures come from compromised vendor mailboxes, so sender reputation checks pass.", Tlp = TlpLevel.Green },
            new() { SortOrder = 1, Text = "The landing page proxies the real sign-in to steal the session cookie.", Tlp = TlpLevel.Green },
        ]);
        phish.Techniques.AddRange([new() { TechniqueId = "T1566.002" }, new() { TechniqueId = "T1557" }]);
        foreach (var key in new[] { "phishing", "financial-sector" })
            if (Tag(key) is var id && id != Guid.Empty) phish.Tags.Add(new() { TagId = id });

        var restricted = new Article
        {
            Title = "Partner reports targeting of shared payment processor",
            SourceName = "Partner briefing",
            Tlp = TlpLevel.Amber,
            Classification = Classification.Threat,
            Priority = Priority.P2,
            Bluf = "A peer organization saw reconnaissance against a payment processor we also use.",
            RecommendedActions = "Ask the processor's security contact whether they have seen the same activity.",
            Status = ArticleStatus.Draft,
            AuthorId = "system",
            AuthorName = "Sample data",
        };
        restricted.Bullets.Add(new() { SortOrder = 0, Text = "No compromise is confirmed; the activity stopped at scanning.", Tlp = TlpLevel.Amber });
        if (Tag("vendor-breach") is var vb && vb != Guid.Empty) restricted.Tags.Add(new() { TagId = vb });

        db.Articles.AddRange(vpn, phish, restricted);
        var brief = new Brief { BriefDate = clock.CurrentBriefDate, AnalystId = "system", AnalystName = "Sample data" };
        brief.Items.AddRange([
            new() { ArticleId = vpn.Id, SortOrder = 0 },
            new() { ArticleId = phish.Id, SortOrder = 1 },
            new() { ArticleId = restricted.Id, SortOrder = 2 },
        ]);
        db.Briefs.Add(brief);

        var vulnerability = new Team { Key = "vulnerability-management", Name = "Vulnerability Management", SortOrder = 0 };
        var defense = new Team { Key = "cyber-defense", Name = "Cyber Defense", SortOrder = 1 };
        db.Teams.AddRange(vulnerability, defense);
        db.Actions.AddRange(
            new ActionItem
            {
                Number = 1, Title = "Sweep VPN and firewall logs for the advisory's indicators", Kind = ActionKind.IocSweep, Priority = Priority.P1,
                TeamId = defense.Id, ArticleId = vpn.Id, DueDate = clock.Days.Next(clock.Today),
            },
            new ActionItem
            {
                Number = 2, Title = "Apply the vendor hotfix to both remote-access gateways", Kind = ActionKind.PatchRemediate, Priority = Priority.P1,
                TeamId = vulnerability.Id, ArticleId = vpn.Id, DueDate = clock.Days.Next(clock.Today), Status = ActionStatus.InProgress,
            });

        // Intake: a paused sample source with a few items (one repeating another under a new headline, one about a CVE
        // already briefed), a story, and made-up CVE context, so triage and enrichment have something to show offline.
        var feed = new Source { Name = "Sample security news", Url = "https://example.com/feed.xml", Enabled = false, Reliability = SourceReliability.B };
        db.Sources.Add(feed);
        var now = DateTimeOffset.UtcNow;
        FeedItem Item(string title, string url, string excerpt, int hoursAgo, string? cves = null) => new()
        {
            SourceId = feed.Id, Title = title, Url = url, UrlHash = Application.Intake.UrlCanonicalizer.Hash(url),
            TitleKey = Application.Intake.TitleMatch.Key(title), Excerpt = excerpt, Cves = cves,
            PublishedAtUtc = now.AddHours(-hoursAgo), FetchedAtUtc = now.AddHours(-hoursAgo + 1),
        };
        var first = Item("Ransomware gang exploits edge VPN zero-day against manufacturers", "https://example.com/news/vpn-ransomware",
            "Researchers tie a ransomware affiliate to exploitation of CVE-2026-0001 in remote-access gateways.", 3, "CVE-2026-0001");
        var again = Item("Manufacturers hit as ransomware gang exploits edge VPN zero-day", "https://example.org/story/edge-vpn-ransomware",
            "The same campaign, reported by a second outlet.", 2);
        again.SimilarToId = first.Id;
        db.FeedItems.AddRange(first, again,
            Item("Browser vendor ships fix for actively exploited type confusion bug", "https://example.com/news/browser-fix",
                "An emergency update fixes CVE-2026-0002, which the vendor says is exploited in the wild.", 5, "CVE-2026-0002"),
            Item("Phishing kit adds QR codes to evade mail filters", "https://example.net/research/qr-phishing",
                "A widely sold phishing kit now embeds the credential page link in a QR code image.", 8));
        var story = new Story { Name = "Edge VPN appliance exploitation", Summary = "Exploitation of remote-access gateways, from the zero-day onward." };
        db.Stories.Add(story);
        vpn.StoryId = story.Id;
        db.CveRecords.AddRange(
            new CveRecord { CveId = "CVE-2026-0001", CvssScore = 9.8m, CvssSeverity = "CRITICAL", EpssScore = 0.94m, EpssPercentile = 0.99m, IsKev = true,
                KevAddedDate = clock.Today.AddDays(-2), KevDueDate = clock.Today.AddDays(19), KevRansomware = true, Vendor = "Example", Product = "Edge VPN",
                Description = "Sample data: an authentication bypass leading to remote code execution." },
            new CveRecord { CveId = "CVE-2026-0002", CvssScore = 8.8m, CvssSeverity = "HIGH", EpssScore = 0.21m, EpssPercentile = 0.95m,
                Description = "Sample data: a type confusion in a browser engine." });

        // Rotation: the dev sign-in and two colleagues (sign in as them with ?as=Robin or ?as=Sam), eight weeks filled.
        var devId = config["DevAuth:UserId"] ?? "S-1-5-21-DEV-1001";
        var roster = new[] { (devId, config["DevAuth:DisplayName"] ?? "Dev Analyst"), ("dev:robin", "Robin"), ("dev:sam", "Sam") };
        for (var i = 0; i < roster.Length; i++)
        {
            db.RotationMembers.Add(new RotationMember { UserId = roster[i].Item1, DisplayName = roster[i].Item2, SortOrder = i });
            if (i > 0) db.Users.Add(new AppUser { Sid = roster[i].Item1, DisplayName = roster[i].Item2, Email = roster[i].Item2.ToLowerInvariant() + "@example.com", LastSeenUtc = now });
        }
        var monday = Application.Rotation.RotationService.WeekStart(clock.CurrentBriefDate);
        for (var w = 0; w < 8; w++)
            db.RotationWeeks.Add(new RotationWeek { WeekStart = monday.AddDays(7 * w), AnalystId = roster[w % 3].Item1, AnalystName = roster[w % 3].Item2, UpdatedAtUtc = now, UpdatedBy = "system" });
        await db.SaveChangesAsync(ct);
    }
}
