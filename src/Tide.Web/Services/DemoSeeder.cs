using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Api;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Application.Scorecard;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Security;

namespace Tide.Web.Services;

/// <summary>
/// Development only: a fuller, fictional data set for the screenshot tool (tools/screenshots), on top of the samples
/// <see cref="DatabaseInitializer"/> adds. Turned on with <c>Demo:Seed=true</c>, and run once per database. Six months
/// of briefed articles (for Trends), a year of opening metrics, actions in every state with a hunt, adversaries, more
/// triage items with their text kept for the reading pane, API tokens, and yesterday's brief sent in both editions
/// through the app's own send. Also products on Our stack, a metric group with a number left to enter, two actions
/// still open from the brief before yesterday's, and a second person to follow a live meeting. Its random choices come
/// from a fixed seed, so two runs on the same day match.
/// <para><c>Demo:InfoFile</c>, when set, gets a small JSON file naming the days and numbers the tool needs.</para>
/// </summary>
public static class DemoSeeder
{
    private const string MarkerKey = "Demo:Seeded";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task SeedAsync(IServiceProvider root, IConfiguration config, ILogger log, CancellationToken ct = default)
    {
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;
        var dbs = sp.GetRequiredService<IAppDbContextFactory>();
        var clock = sp.GetRequiredService<OrgClock>();
        var info = config["Demo:InfoFile"];

        await using (var db = await dbs.CreateAsync(ct))
        {
            if (await db.Settings.AnyAsync(s => s.Key == MarkerKey, ct))
            {
                log.LogInformation("Demo data is already in this database; not seeded again.");
                return;
            }
        }

        // The services check the caller's permissions, so the seeding runs as the dev sign-in (an analyst and an
        // administrator), as if from a request: CurrentUser reads the request's principal.
        var dev = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DevAuthOptions>>().Value;
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { User = Principal(dev), RequestServices = sp };
        try
        {
            var seeded = await new Run(sp, dbs, clock, dev).AllAsync(ct);
            await TriageFillerAsync(dbs, config.GetValue<int>("Demo:TriageFiller"), ct);
            if (!string.IsNullOrWhiteSpace(info))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(info))!);
                await File.WriteAllTextAsync(info, JsonSerializer.Serialize(seeded, Indented), ct);
            }
            log.LogInformation("Seeded the demo data: today {Today}, sent {Sent}.", seeded.Today, seeded.Sent);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    /// <summary>
    /// <c>Demo:TriageFiller</c> (none unless set): that many more new items, "Filler k001" onwards, older than the demo's
    /// own, from one feed so they never group, for the layout checks that need Triage to have a second page; and one
    /// event told by two feeds that the Edge VPN article covers (a CVE it names), for the checks on a covered group.
    /// </summary>
    private static async Task TriageFillerAsync(IAppDbContextFactory dbs, int count, CancellationToken ct)
    {
        if (count <= 0) return;
        await using var db = await dbs.CreateAsync(ct);
        var source = new Source { Name = "Example filler feed", Url = "https://example.com/filler.xml", Enabled = false };
        var wire = new Source { Name = "Example filler wire", Url = "https://example.net/filler.xml", Enabled = false };
        db.Sources.AddRange(source, wire);
        var now = DateTimeOffset.UtcNow;
        void Add(Source from, string title, string url, DateTimeOffset at, string? cves = null) => db.FeedItems.Add(new FeedItem
        {
            Source = from, Title = title, Url = url, UrlHash = Application.Intake.UrlCanonicalizer.Hash(url),
            TitleKey = Application.Intake.TitleMatch.Key(title), PublishedAtUtc = at, FetchedAtUtc = at, Cves = cves,
        });
        for (var n = 1; n <= Math.Min(count, 500); n++) Add(source, $"Filler k{n:000}", $"https://example.com/filler/k{n:000}", now.AddHours(-8).AddMinutes(-10 * n));
        const string cve = "CVE-2026-0098";
        var vpn = await db.Articles.FirstAsync(a => a.Title.StartsWith("Edge VPN"), ct);
        db.ArticleCves.Add(new ArticleCve { ArticleId = vpn.Id, CveId = cve });
        Add(source, $"Quillon Bank confirms $3.7 million theft ({cve})", "https://example.com/filler/quillon-theft", now.AddHours(-7), cve);
        Add(wire, "Quillon Bank $3.7 million heist traced to stolen gateway keys", "https://example.net/filler/quillon-heist", now.AddHours(-7).AddMinutes(-20));
        await db.SaveChangesAsync(ct);
    }

    private static ClaimsPrincipal Principal(DevAuthOptions dev)
    {
        string[] roles = ["Analyst", "Admin"];
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, dev.UserId), new(ClaimTypes.Name, dev.DisplayName),
            new(ClaimTypes.Upn, dev.Upn), new(ClaimTypes.Email, dev.Email),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(RoleDefinitions.PermissionsForRoleNames(roles).Select(p => new Claim(AppClaimTypes.Permission, p.ToString())));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, DevAuthenticationHandler.SchemeName, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>What the screenshot tool needs to find its pages, and <paramref name="Follower"/>: who to sign a second
    /// browser in as (<c>?as=</c>) to follow a live meeting.</summary>
    public sealed record Seeded(string Today, string Sent, int HuntAction, int DetailAction, string VpnArticle, string SentArticle,
        string StackProduct, string Follower);

    // One seeding pass. Everything random comes from one fixed seed.
    private sealed class Run(IServiceProvider sp, IAppDbContextFactory dbs, OrgClock clock, DevAuthOptions dev)
    {
        private readonly Random _rng = new(20261029);
        private readonly DateOnly _today = clock.CurrentBriefDate;
        private DateOnly Sent => clock.Days.Previous(_today);

        private static readonly (string Id, string Name)[] Analysts = [("dev:robin", "Robin"), ("dev:sam", "Sam")];

        // Follows a live meeting from a second browser: the dev sign-in's ?as=Jordan&roles=Viewer is this person.
        private const string Follower = "Jordan";

        public async Task<Seeded> AllAsync(CancellationToken ct)
        {
            await using var db = await dbs.CreateAsync(ct);
            var tags = await db.ScorecardTags.ToDictionaryAsync(t => t.Key, t => t.Id, ct);
            var teams = await TeamsAsync(db, ct);
            var vpn = await db.Articles.FirstAsync(a => a.Title.StartsWith("Edge VPN"), ct);
            var adversaries = Adversaries(db);
            var sources = Sources(db);
            await db.SaveChangesAsync(ct);

            Triage(db, sources);
            await HistoryAsync(db, tags, teams, ct);
            var sent = SentBriefArticles(db, tags, adversaries);
            db.ArticleAdversaries.Add(new ArticleAdversary { ArticleId = vpn.Id, AdversaryId = adversaries[0].Id });
            var detail = CurrentActions(db, teams, sent);
            EarlierBrief(db, tags, teams);
            var edgeVpn = Stack(db, teams, vpn);
            var login = Follower.ToLowerInvariant();   // as the dev sign-in names a ?as= person
            db.Users.Add(new AppUser
            {
                Sid = "dev:" + login, DisplayName = Follower, UserPrincipalName = login + "@example.com", Email = login + "@example.com",
                RolesCsv = nameof(AppRole.Viewer), LastSeenUtc = DateTimeOffset.UtcNow,
            });
            // Today's brief is the dev sign-in's.
            var todays = await db.Briefs.FirstAsync(b => b.BriefDate == _today, ct);
            todays.AnalystId = dev.UserId;
            todays.AnalystName = dev.DisplayName;
            await db.SaveChangesAsync(ct);

            await ScreenshotsAsync(vpn.Id, [("Vendor advisory: affected firmware versions", TlpLevel.Clear, Teal),
                ("ISAC member's implant listing", TlpLevel.Amber, Red)], ct);
            await ScreenshotsAsync(sent[0].Id, [("Remote monitoring console seen in the intrusion", TlpLevel.Green, Teal)], ct);
            await MetricsAsync(ct);
            await ExposureMetricsAsync(ct);
            await HuntAsync(db, ct);
            await TokensAsync(ct);
            await SendAsync(sent, ct);

            db.Settings.Add(new AppSetting { Key = MarkerKey, Value = "1", UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = "system" });
            await db.SaveChangesAsync(ct);
            return new Seeded(_today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Sent.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), 1, detail, vpn.Id.ToString(), sent[0].Id.ToString(),
                edgeVpn.Id.ToString(), Follower);
        }

        // --- reference data ----------------------------------------------------------------------------------------

        private static async Task<Dictionary<string, Team>> TeamsAsync(IAppDbContext db, CancellationToken ct)
        {
            var teams = await db.Teams.ToDictionaryAsync(t => t.Key, ct);
            var identity = new Team { Key = "identity-access", Name = "Identity & Access", SortOrder = 2 };
            var fraud = new Team { Key = "fraud-operations", Name = "Fraud Operations", SortOrder = 3 };
            db.Teams.AddRange(identity, fraud);
            teams[identity.Key] = identity;
            teams[fraud.Key] = fraud;
            return teams;
        }

        private static List<Adversary> Adversaries(IAppDbContext db)
        {
            List<Adversary> list =
            [
                new() { Name = "Ember Lynx", Kind = AdversaryKind.Actor, Aliases = "EL-04, Copper Fox", Motivation = "Financial",
                    Sectors = "Manufacturing, Financial services", Watched = true,
                    Notes = "Ransomware affiliate that buys edge-device access and moves to remote monitoring tools within hours. Fictional, for the demo." },
                new() { Name = "Quiet Harbor", Kind = AdversaryKind.Actor, Aliases = "QH", Motivation = "Espionage",
                    Sectors = "Government, Financial regulators", Watched = true,
                    Notes = "State-aligned group interested in regulators and payment infrastructure. Fictional, for the demo." },
                new() { Name = "Overdue Invoice", Kind = AdversaryKind.Campaign, Motivation = "Financial", Sectors = "Accounts payable",
                    Notes = "Invoice-themed credential phishing run from compromised supplier mailboxes. Fictional, for the demo." },
            ];
            db.Adversaries.AddRange(list);
            return list;
        }

        private static List<Source> Sources(IAppDbContext db)
        {
            // Paused, so the intake never goes to the network for them.
            List<Source> list =
            [
                new() { Name = "Example vendor advisories", Url = "https://example.com/advisories.xml", Enabled = false, Reliability = SourceReliability.A },
                new() { Name = "Example research blog", Url = "https://example.org/research/feed", Enabled = false, Reliability = SourceReliability.B },
                new() { Name = "Example sector ISAC", Url = "https://example.net/isac/rss", Enabled = false, Reliability = SourceReliability.B, DefaultTlp = TlpLevel.Green },
            ];
            db.Sources.AddRange(list);
            return list;
        }

        // --- triage ------------------------------------------------------------------------------------------------

        private static void Triage(IAppDbContext db, List<Source> sources)
        {
            var now = DateTimeOffset.UtcNow;
            (int Source, string Title, string Url, string Excerpt, int HoursAgo, string Text)[] items =
            [
                (0, "Vendor patches authentication bypass in managed file transfer server", "https://example.com/advisories/mft-auth-bypass",
                    "A fixed release closes CVE-2026-0003, an authentication bypass in the web interface of a managed file transfer server.", 1,
                    "The vendor has released fixed builds for an authentication bypass, tracked as CVE-2026-0003, in the web interface of its managed file transfer server.\n\n" +
                    "An unauthenticated attacker who can reach the interface can create an administrator session. The vendor says it is not aware of exploitation, but proof-of-concept code was posted publicly within hours of the advisory.\n\n" +
                    "Indicators shared by a researcher include requests from 198.51.100.23 and the domain files-sync.example.org.\n\n" +
                    "Customers who cannot upgrade should restrict the interface to internal addresses."),
                (1, "Researchers detail loader that hides in signed driver packages", "https://example.org/research/signed-driver-loader",
                    "A new loader abuses a legitimately signed but vulnerable driver to switch off endpoint protection.", 4,
                    "Researchers describe a loader that installs a legitimately signed but vulnerable driver, then uses it to stop endpoint protection processes.\n\n" +
                    "The loader arrives in archives named like invoices and is linked to an affiliate of the Ember Lynx ransomware operation.\n\n" +
                    "The report lists the driver's hash and a staging server at 203.0.113.77."),
                (2, "ISAC members report help-desk calls asking to reset MFA", "https://example.net/isac/helpdesk-mfa-resets",
                    "Several member organizations received calls to their help desks from people posing as employees who had lost their phones.", 6,
                    "Several member organizations have received calls to their IT help desks from people posing as employees who say they have lost their phones and need multi-factor authentication reset.\n\n" +
                    "Callers knew employee ID numbers and managers' names. In two cases the reset was completed and the account was used to read email within the hour.\n\n" +
                    "Members recommend call-back verification to the number on file before any reset."),
            ];
            foreach (var (s, title, url, excerpt, hours, text) in items)
            {
                var item = new FeedItem
                {
                    SourceId = sources[s].Id, Title = title, Url = url, UrlHash = Application.Intake.UrlCanonicalizer.Hash(url),
                    TitleKey = Application.Intake.TitleMatch.Key(title), Excerpt = excerpt,
                    Cves = title.Contains("file transfer", StringComparison.Ordinal) ? "CVE-2026-0003" : null,
                    PublishedAtUtc = now.AddHours(-hours), FetchedAtUtc = now.AddHours(-hours).AddMinutes(20),
                };
                db.FeedItems.Add(item);
                db.FeedItemTexts.Add(new FeedItemText { FeedItemId = item.Id, Text = text, How = "the page's article markup", FetchedAtUtc = now });
            }
            db.CveRecords.Add(new CveRecord
            {
                CveId = "CVE-2026-0003", CvssScore = 9.1m, CvssSeverity = "CRITICAL", EpssScore = 0.37m, EpssPercentile = 0.97m,
                Vendor = "Example", Product = "File Transfer Server", Description = "Sample data: an authentication bypass in a web interface.",
            });
        }

        // --- six months of briefs ----------------------------------------------------------------------------------

        private static readonly (string Title, string Source, Classification Kind, string[] Tags, string[] Techniques)[] Pool =
        [
            ("Ransomware group claims victims in regional manufacturing", "Research blog", Classification.Threat, ["ransomware", "data-extortion", "cybercrime"], ["T1486", "T1567"]),
            ("Edge appliance exploited ahead of a vendor patch", "Vendor advisory", Classification.Threat, ["actively-exploited", "zero-day"], ["T1190"]),
            ("Credential phishing kit targets payroll portals", "Industry ISAC", Classification.Threat, ["phishing", "financial-sector"], ["T1566.002", "T1557"]),
            ("Vendor discloses breach of its customer support portal", "Vendor notice", Classification.Threat, ["vendor-breach", "cloud-saas-provider"], ["T1199"]),
            ("Typosquatted open-source packages steal developer tokens", "Research blog", Classification.Threat, ["software-supply-chain", "open-source"], ["T1195.001", "T1552"]),
            ("State-aligned group targets financial regulators", "Government advisory", Classification.Threat, ["nation-state", "peer-organization-targeted"], ["T1566.001", "T1059.001"]),
            ("Supplier impersonation drives business email compromise", "Industry ISAC", Classification.Threat, ["business-email-compromise", "payment-fraud"], ["T1534", "T1114"]),
            ("Access broker advertises bank VPN credentials", "Dark web monitoring", Classification.Threat, ["initial-access-brokers", "financial-sector"], ["T1078", "T1133"]),
            ("Hacktivists announce denial-of-service campaign against banks", "News", Classification.Informational, ["hacktivism", "financial-sector"], ["T1498"]),
            ("Patch released for exploited file transfer flaw", "Vendor advisory", Classification.Threat, ["patch-released", "actively-exploited"], ["T1190"]),
            ("Identity provider warns of session token replay", "Vendor advisory", Classification.Informational, ["cloud-saas-provider"], ["T1550.004"]),
            ("Former employee charged with copying customer records", "News", Classification.Informational, ["insider"], ["T1052"]),
            ("Government advisory on SIM-swap fraud", "Government advisory", Classification.Informational, ["government-advisory", "payment-fraud"], ["T1111"]),
            ("Certificate authority revokes mis-issued certificates", "Vendor notice", Classification.Informational, ["pki-certificate-trust"], []),
            ("Data extortion crew leaks retailer files", "Research blog", Classification.Threat, ["data-extortion", "ransomware"], ["T1567.002"]),
            ("Regulator finalizes incident reporting rule", "Regulator", Classification.Informational, ["regulation"], []),
            ("Unpatched router flaw disclosed with proof of concept", "Research blog", Classification.Threat, ["unpatched-disclosure"], ["T1190"]),
            ("QR-code phishing reaches executive mailboxes", "Industry ISAC", Classification.Threat, ["phishing"], ["T1566.002"]),
        ];

        private async Task HistoryAsync(IAppDbContext db, Dictionary<string, Guid> tags, Dictionary<string, Team> teams, CancellationToken ct)
        {
            var number = await db.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).Select(a => (int?)a.Number).MaxAsync(ct) ?? 0;   // a deleted one's number isn't reused
            var teamList = teams.Values.OrderBy(t => t.SortOrder).ToList();
            var start = _today.AddDays(-190);
            var people = new[] { (dev.UserId, dev.DisplayName) }.Concat(Analysts).ToArray();
            var seen = 0;
            for (var day = start; day < clock.Days.Previous(Sent); day = day.AddDays(1))
            {
                if (!clock.Days.IsWorkingDay(day) || _rng.NextDouble() < 0.35) continue;
                var who = people[seen++ % people.Length];
                var held = clock.MeetingStart(day).ToUniversalTime();
                var brief = new Brief
                {
                    BriefDate = day, AnalystId = who.Item1, AnalystName = who.Item2, Status = BriefStatus.Sent,
                    MeetingHeldAtUtc = held, PresentedAtUtc = held.AddMinutes(25), DistributedAtUtc = held.AddMinutes(40),
                    DistributedById = who.Item1, DistributedByName = who.Item2,
                };
                var count = _rng.Next(1, 4);
                for (var i = 0; i < count; i++)
                {
                    var (title, source, kind, tagKeys, techniques) = Pool[_rng.Next(Pool.Length)];
                    var article = new Article
                    {
                        Title = title, SourceName = source, Classification = kind, Status = ArticleStatus.Presented,
                        Tlp = (TlpLevel)_rng.Next(0, 3), Priority = kind == Classification.Threat ? (Priority)_rng.Next(0, 4) : null,
                        Bluf = "Sample data for the trends page.", AuthorId = who.Item1, AuthorName = who.Item2,
                    };
                    article.Bullets.Add(new ArticleBullet { Text = "Sample data: a past article, briefed and sent.", Tlp = TlpLevel.Clear });
                    foreach (var key in tagKeys)
                        if (tags.TryGetValue(key, out var id)) article.Tags.Add(new ArticleTag { TagId = id });
                    article.Techniques.AddRange(techniques.Select(t => new ArticleTechnique { TechniqueId = t }));
                    db.Articles.Add(article);
                    brief.Items.Add(new BriefItem { ArticleId = article.Id, SortOrder = i, Discussed = true });

                    // Threats raised follow-up work, mostly closed by now, mostly on time.
                    if (kind != Classification.Threat || _rng.NextDouble() < 0.4) continue;
                    var due = clock.Days.Next(clock.Days.Next(day));
                    var closed = _rng.NextDouble() < 0.8 ? due : clock.Days.Next(clock.Days.Next(due));
                    var owner = people[_rng.Next(people.Length)];
                    db.Actions.Add(new ActionItem
                    {
                        Number = ++number, Title = "Check exposure: " + title.ToLowerInvariant(), Kind = (ActionKind)_rng.Next(0, 5),
                        Priority = article.Priority ?? Priority.P3, TeamId = teamList[_rng.Next(teamList.Count)].Id, ArticleId = article.Id,
                        AssigneeId = owner.Item1, AssigneeName = owner.Item2, RaisedOn = day, DueDate = due,
                        Status = ActionStatus.Done, Outcome = ActionOutcome.None, ClosedDate = closed,
                        ClosedAtUtc = clock.MeetingStart(closed).ToUniversalTime().AddHours(4),
                    });
                }
                db.Briefs.Add(brief);
            }
            await db.SaveChangesAsync(ct);
        }

        // --- yesterday's brief, sent ----------------------------------------------------------------------------------

        private List<Article> SentBriefArticles(IAppDbContext db, Dictionary<string, Guid> tags, List<Adversary> adversaries)
        {
            Guid? Tag(string key) => tags.TryGetValue(key, out var id) ? id : null;
            var robin = Analysts[0];
            Article Make(string title, string source, TlpLevel tlp, Classification kind, Priority? priority, string bluf, string? why,
                string? actions, string[] tagKeys, string[] techniques, params (string Text, TlpLevel Tlp)[] bullets)
            {
                var a = new Article
                {
                    Title = title, SourceName = source, SourceUrl = "https://example.com/reports/" + title.Split(' ')[0].ToLowerInvariant(),
                    Tlp = tlp, Classification = kind, Priority = priority, Bluf = bluf, WhyItMatters = why,
                    WhyItMattersTlp = Article.DefaultWhyItMattersTlp(kind, tlp), RecommendedActions = actions,
                    Status = ArticleStatus.Ready, AuthorId = robin.Id, AuthorName = robin.Name,
                };
                for (var i = 0; i < bullets.Length; i++) a.Bullets.Add(new ArticleBullet { SortOrder = i, Text = bullets[i].Text, Tlp = bullets[i].Tlp });
                foreach (var k in tagKeys) if (Tag(k) is { } id) a.Tags.Add(new ArticleTag { TagId = id });
                a.Techniques.AddRange(techniques.Select(t => new ArticleTechnique { TechniqueId = t }));
                db.Articles.Add(a);
                return a;
            }

            var rmm = Make("Ransomware affiliate abuses remote monitoring tool for persistence", "Research blog", TlpLevel.Green,
                Classification.Threat, Priority.P2,
                "An Ember Lynx affiliate installs a commercial remote monitoring agent after initial access, so the foothold survives a password reset.",
                "We allow two remote monitoring products on servers; a third would not be blocked by the current application control policy.",
                "- Alert on installation of remote monitoring agents outside the approved two.\n- Review application control rules for unsigned agent updaters.",
                ["ransomware", "cybercrime"], ["T1219", "T1543.003"],
                ("The agent is installed **within two hours** of the first sign-in.", TlpLevel.Clear),
                ("It is registered to a trial account, so the vendor can disable it on request.", TlpLevel.Green),
                ("A peer saw the same account used against its own servers last week.", TlpLevel.Amber));
            rmm.Indicators.AddRange([
                new ArticleIndicator { Type = IndicatorType.Domain, Value = "rmm-relay.example.org" },
                new ArticleIndicator { Type = IndicatorType.IpAddress, Value = "192.0.2.144" },
            ]);
            var token = Make("Cloud storage provider discloses token theft affecting customer tenants", "Vendor notice", TlpLevel.Clear,
                Classification.Threat, Priority.P3,
                "A cloud storage provider says an attacker stole access tokens for a subset of customer tenants from a support system.",
                "Two business units use the provider; neither has been told it is affected yet.",
                "Rotate the integration tokens for both business units' tenants as a precaution.",
                ["vendor-breach", "cloud-saas-provider"], ["T1528"],
                ("Tokens were taken from a support system, not from the storage service itself.", TlpLevel.Clear),
                ("Affected customers are being notified directly over the next week.", TlpLevel.Clear));
            var rule = Make("Regulator publishes incident reporting guidance for financial firms", "Regulator", TlpLevel.Clear,
                Classification.Informational, null,
                "New guidance sets out what a reportable cyber incident is and shortens the first notice to 24 hours.",
                null, null, ["regulation", "financial-sector"], [],
                ("The 24-hour clock starts when an incident is judged reportable, not when it begins.", TlpLevel.Clear),
                ("The guidance takes effect at the start of next quarter.", TlpLevel.Clear));
            var helpdesk = Make("Peer bank reports help-desk social engineering for MFA resets", "Partner briefing", TlpLevel.Amber,
                Classification.Threat, Priority.P2,
                "Callers posing as employees talked a peer bank's help desk into resetting multi-factor authentication twice this week.",
                "Our help desk resets MFA after a knowledge check that uses details a caller could find online.",
                "Ask the service desk to require call-back verification before any MFA reset.",
                ["peer-organization-targeted", "financial-sector"], ["T1656", "T1621"],
                ("Both resets led to mailbox access within the hour.", TlpLevel.Amber));
            db.ArticleAdversaries.Add(new ArticleAdversary { ArticleId = rmm.Id, AdversaryId = adversaries[0].Id });
            var brief = new Brief { BriefDate = Sent, AnalystId = robin.Id, AnalystName = robin.Name };
            var order = 0;
            foreach (var a in new[] { rmm, token, rule, helpdesk }) brief.Items.Add(new BriefItem { ArticleId = a.Id, SortOrder = order++, Discussed = true });
            db.Briefs.Add(brief);
            return [rmm, token, rule, helpdesk];
        }

        // --- current actions -----------------------------------------------------------------------------------------

        private int CurrentActions(IAppDbContext db, Dictionary<string, Team> teams, List<Article> sent)
        {
            var number = db.Actions.Local.Select(a => a.Number).DefaultIfEmpty(2).Max();
            number = Math.Max(number, 2);
            var (rmm, token, helpdesk) = (sent[0], sent[1], sent[3]);
            var day = Sent;
            DateOnly Bd(int n) { var d = _today; for (var i = 0; i < Math.Abs(n); i++) d = n > 0 ? clock.Days.Next(d) : clock.Days.Previous(d); return d; }
            ActionItem Add(string title, ActionKind kind, Priority p, string team, Article? article, (string Id, string Name)? who, DateOnly due,
                ActionStatus status, string? description = null)
            {
                var a = new ActionItem
                {
                    Number = ++number, Title = title, Kind = kind, Priority = p, TeamId = teams[team].Id, ArticleId = article?.Id,
                    AssigneeId = who?.Id, AssigneeName = who?.Name, RaisedOn = day, DueDate = due, Status = status, Description = description,
                };
                db.Actions.Add(a);
                return a;
            }
            var robin = Analysts[0];
            var sam = Analysts[1];
            var me = (dev.UserId, dev.DisplayName);
            var detail = Add("Block the remote monitoring relay and alert on unapproved agents", ActionKind.Block, Priority.P2, "cyber-defense", rmm, robin,
                Bd(1), ActionStatus.InProgress,
                "Block **rmm-relay.example.org** at the proxy and add a detection for remote monitoring agents other than the two we approve.");
            detail.Checklist.AddRange([
                new ActionChecklistItem { SortOrder = 0, Text = "Block the relay domain at the proxy", IsDone = true, DoneByName = robin.Name, DoneAtUtc = DateTimeOffset.UtcNow.AddHours(-3) },
                new ActionChecklistItem { SortOrder = 1, Text = "Add the detection rule for unapproved agents" },
                new ActionChecklistItem { SortOrder = 2, Text = "Confirm no hits in the last 30 days of proxy logs" },
            ]);
            var raised = DateTimeOffset.UtcNow.AddHours(-20);
            detail.Events.AddRange([
                new ActionEvent { AtUtc = raised, ByName = robin.Name, Text = "Raised from the brief." },
                new ActionEvent { AtUtc = raised.AddMinutes(5), ByName = robin.Name, Text = "Took the action." },
                new ActionEvent { AtUtc = raised.AddHours(17), ByName = robin.Name, Text = "Status: Open → In progress." },
            ]);
            detail.Comments.Add(new ActionComment
            {
                Text = "Proxy block is in. Working on the detection now; the agent's service name is stable across versions, which helps.",
                AuthorId = robin.Id, AuthorName = robin.Name, AtUtc = DateTimeOffset.UtcNow.AddHours(-2),
            });
            Add("Rotate integration tokens for the cloud storage tenants", ActionKind.ConfigChange, Priority.P3, "identity-access", token, sam, Bd(3), ActionStatus.Open);
            Add("Require call-back verification before MFA resets", ActionKind.ConfigChange, Priority.P2, "identity-access", helpdesk, null, Bd(2), ActionStatus.Open);
            Add("Brief the service desk on MFA reset pretexting", ActionKind.Awareness, Priority.P3, "fraud-operations", helpdesk, me, Bd(-1), ActionStatus.Open);
            Add("Confirm the vendor's patch window for the file transfer server", ActionKind.PatchRemediate, Priority.P2, "vulnerability-management", null, sam, Bd(-2), ActionStatus.Blocked);
            Add("Hunt for unapproved remote monitoring agents on servers", ActionKind.ThreatHunt, Priority.P2, "cyber-defense", rmm, me, Bd(2), ActionStatus.Open);
            return detail.Number;
        }

        // --- the brief before yesterday's, with work still open -------------------------------------------------------

        // Runs after CurrentActions, so its actions take the next numbers.
        private void EarlierBrief(IAppDbContext db, Dictionary<string, Guid> tags, Dictionary<string, Team> teams)
        {
            var day = clock.Days.Previous(Sent);
            var sam = Analysts[1];
            var held = clock.MeetingStart(day).ToUniversalTime();
            var article = new Article
            {
                Title = "Identity provider session tokens replayed from unmanaged devices", SourceName = "Vendor advisory",
                SourceUrl = "https://example.com/advisories/session-token-replay", Tlp = TlpLevel.Green, Classification = Classification.Threat,
                Priority = Priority.P2, Status = ArticleStatus.Presented, AuthorId = sam.Id, AuthorName = sam.Name,
                Bluf = "Stolen session tokens for a cloud identity provider are being replayed from unmanaged devices, which skips the sign-in checks.",
                WhyItMatters = "Our sessions last twelve hours and aren't tied to a managed device.",
                RecommendedActions = "- Shorten session lifetime for unmanaged devices.\n- Review sign-ins from unmanaged devices over the last 14 days.",
            };
            article.Bullets.Add(new ArticleBullet { Text = "The tokens come from information-stealing malware on personal devices.", Tlp = TlpLevel.Green });
            if (tags.TryGetValue("cloud-saas-provider", out var tag)) article.Tags.Add(new ArticleTag { TagId = tag });
            article.Techniques.Add(new ArticleTechnique { TechniqueId = "T1550.004" });
            db.Articles.Add(article);
            var brief = new Brief
            {
                BriefDate = day, AnalystId = sam.Id, AnalystName = sam.Name, Status = BriefStatus.Sent,
                MeetingHeldAtUtc = held, PresentedAtUtc = held.AddMinutes(25), DistributedAtUtc = held.AddMinutes(40),
                DistributedById = sam.Id, DistributedByName = sam.Name,
            };
            brief.Items.Add(new BriefItem { ArticleId = article.Id, Discussed = true });
            db.Briefs.Add(brief);

            var number = db.Actions.Local.Max(a => a.Number);
            ActionItem Add(string title, ActionKind kind, Priority p, (string Id, string Name)? who, DateOnly due)
            {
                var a = new ActionItem
                {
                    Number = ++number, Title = title, Kind = kind, Priority = p, TeamId = teams["identity-access"].Id, ArticleId = article.Id,
                    AssigneeId = who?.Id, AssigneeName = who?.Name, RaisedOn = day, DueDate = due,
                };
                a.Events.Add(new ActionEvent { AtUtc = held.AddMinutes(10), ByName = sam.Name, Text = "Raised from the brief." });
                db.Actions.Add(a);
                return a;
            }
            // Due the day it was raised and not done: the ladder flagged it the next morning. The other is a day late.
            var escalated = Add("Shorten session lifetime for unmanaged devices", ActionKind.ConfigChange, Priority.P1, null, day);
            escalated.EscalatedForDue = escalated.DueDate;
            escalated.EscalatedAtUtc = clock.MeetingStart(Sent).ToUniversalTime().AddHours(-2);
            escalated.Events.Add(new ActionEvent
            {
                AtUtc = escalated.EscalatedAtUtc.Value, ByName = ReminderService.EscalatedBy,
                Text = $"Escalated, {EscalationLadder.Lateness(day, Sent, clock.Days)}: flagged escalated.",
            });
            Add("Review sign-ins from unmanaged devices over the last 14 days", ActionKind.ThreatHunt, Priority.P2, sam, Sent);
        }

        // --- Our stack ---------------------------------------------------------------------------------------------------

        /// <summary>Five products: one tagged on the edge VPN article (returned), one a triage item names, one not checked
        /// for over a year, one with no owning team and one never checked.</summary>
        private StackProduct Stack(IAppDbContext db, Dictionary<string, Team> teams, Article vpn)
        {
            StackProduct Add(string name, string? otherNames, string versions, string notes, string? team, int? checkedDaysAgo,
                (string Name, string Email)? contact = null)
            {
                var p = new StackProduct
                {
                    Name = name, OtherNames = otherNames, Versions = versions, Notes = notes, TeamId = team is null ? null : teams[team].Id,
                    CheckedOn = checkedDaysAgo is { } days ? _today.AddDays(-days) : null, ContactName = contact?.Name, ContactEmail = contact?.Email,
                };
                db.StackProducts.Add(p);
                return p;
            }
            var edgeVpn = Add("Edge VPN", "Remote access gateway", "9.1R4 on both gateways",
                "Two gateways in front of remote access. Change window: Tuesdays, 02:00 to 04:00.", "vulnerability-management", 6,
                ("Example vendor support", "support@example.com"));
            Add("File Transfer Server", "MFT", "2025.3", "One server in the partner zone; the web interface is reachable from partner networks only.",
                "vulnerability-management", 40);
            Add("Identity Provider", "Single sign-on; SSO", "Cloud tenant", "Sign-in for staff and contractors. Sessions last twelve hours.",
                "identity-access", 420);
            Add("Payment Processor", null, "Hosted service", "Card payments for the online shop. Nobody has claimed it since the reorganization.",
                null, 14, ("Processor security desk", "security@example.net"));
            Add("Email Security Gateway", "Secure email gateway", "Cloud service", "Filters inbound mail before it reaches mailboxes.", "cyber-defense", null);
            db.ArticleProducts.Add(new ArticleProduct { ArticleId = vpn.Id, ProductId = edgeVpn.Id });
            return edgeVpn;
        }

        // --- files, metrics, hunts, tokens -------------------------------------------------------------------------------

        private async Task ScreenshotsAsync(Guid articleId, (string Caption, TlpLevel Tlp, (byte, byte, byte) Color)[] shots, CancellationToken ct)
        {
            var service = sp.GetRequiredService<ScreenshotService>();
            var added = await service.AddAsync(articleId, shots.Select((s, i) => new ScreenshotUpload($"figure-{i + 1}.png", Png(s.Color))).ToList(), ct);
            await using var db = await dbs.CreateAsync(ct);
            for (var i = 0; i < added.Count; i++)
            {
                var row = await db.ArticleScreenshots.FirstAsync(s => s.Id == added[i].Id, ct);
                row.Caption = shots[i].Caption;
                row.Tlp = shots[i].Tlp;
            }
            await db.SaveChangesAsync(ct);
        }

        private async Task MetricsAsync(CancellationToken ct)
        {
            await sp.GetRequiredService<MetricsService>().ImportExampleSetAsync(ct);
            await using var db = await dbs.CreateAsync(ct);
            var defs = await db.MetricDefinitions.Include(d => d.Group).ToListAsync(ct);
            defs = defs.OrderBy(d => d.Group!.SortOrder).ThenBy(d => d.SortOrder).ToList();
            decimal[] bases = [46, 4, 60, 2, 170, 3];
            decimal[] spread = [4, 3, 5, 2, 8, 2];
            for (var m = 0; m < defs.Count; m++)
            {
                var level = bases[m % bases.Length];
                for (var day = _today.AddDays(-365); day <= _today; day = day.AddDays(1))
                {
                    if (!clock.Days.IsWorkingDay(day)) continue;
                    // Today leaves its last number to be entered, as a morning does.
                    if (day == _today && m == defs.Count - 1) continue;
                    var s = spread[m % spread.Length];
                    level = Math.Max(0, Math.Round(level + (decimal)(_rng.NextDouble() * 2 - 1) * s * 0.6m + (bases[m % bases.Length] - level) * 0.15m));
                    var value = day == _today && m == 1 ? 12 : day == _today && m == 3 ? 6 : level;
                    db.MetricValues.Add(new MetricValue
                    {
                        DefinitionId = defs[m].Id, BriefDate = day, Value = value, EnteredById = dev.UserId, EnteredByName = dev.DisplayName,
                    });
                }
            }
            await db.SaveChangesAsync(ct);
        }

        // A group of three with six weeks of numbers; today leaves its last one to be entered. After MetricsAsync, which
        // gives a year of values to every metric there is when it runs.
        private async Task ExposureMetricsAsync(CancellationToken ct)
        {
            await using var db = await dbs.CreateAsync(ct);
            const string name = "Exposed services";
            (string Name, MetricDirection Direction, decimal? Warn, decimal? Critical, int Level)[] metrics =
            [
                ("Internet-facing hosts", MetricDirection.Neutral, null, null, 212),
                ("With a critical finding", MetricDirection.LowerIsBetter, 3, 6, 2),
                ("No owner on record", MetricDirection.LowerIsBetter, 5, 15, 7),
            ];
            var group = new MetricGroup
            {
                Key = ScorecardService.KeyFor(name), Name = name,
                SortOrder = await db.MetricGroups.MaxAsync(g => g.SortOrder, ct) + 1,
            };
            db.MetricGroups.Add(group);
            for (var m = 0; m < metrics.Length; m++)
            {
                var (metric, direction, warn, critical, level) = metrics[m];
                var def = new MetricDefinition
                {
                    Key = ScorecardService.KeyFor(name + " " + metric), Name = metric, Direction = direction,
                    WarnThreshold = warn, CriticalThreshold = critical, SortOrder = m,
                };
                group.Metrics.Add(def);
                for (var day = _today.AddDays(-42); day <= _today; day = day.AddDays(1))
                {
                    if (!clock.Days.IsWorkingDay(day) || (day == _today && m == metrics.Length - 1)) continue;
                    db.MetricValues.Add(new MetricValue
                    {
                        DefinitionId = def.Id, BriefDate = day, Value = Math.Max(0, level + _rng.Next(-2, 3)),
                        EnteredById = dev.UserId, EnteredByName = dev.DisplayName,
                    });
                }
            }
            await db.SaveChangesAsync(ct);
        }

        private async Task HuntAsync(IAppDbContext db, CancellationToken ct)
        {
            var actions = sp.GetRequiredService<ActionService>();
            var a1 = await db.Actions.AsNoTracking().FirstAsync(a => a.Number == 1, ct);
            var hunt = await actions.GenerateHuntAsync(a1.Id, "kql", ct: ct);
            await actions.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, 0, "30 days of network and DNS events", null, ct);
        }

        private async Task TokensAsync(CancellationToken ct)
        {
            var tokens = sp.GetRequiredService<ApiTokenService>();
            await tokens.IssueAsync("SOAR playbooks", [ApiScopes.ActionsRead, ApiScopes.ActionsWrite, ApiScopes.BriefsRead], TlpLevel.Amber, 180, ct);
            await tokens.IssueAsync("Metrics importer", [ApiScopes.MetricsWrite], TlpLevel.Clear, 90, ct);
            await tokens.IssueAsync("Detection pipeline", [ApiScopes.DetectionsWrite, ApiScopes.StackRead], TlpLevel.Green, 300, ct);
        }

        private async Task SendAsync(List<Article> sent, CancellationToken ct)
        {
            var briefs = sp.GetRequiredService<BriefService>();
            var distribution = sp.GetRequiredService<DistributionService>();
            await sp.GetRequiredService<DistributionSettings>().SaveRecipientsAsync("security-team@example.com", "all-staff@example.com", confirmOutside: true, ct);
            var brief = await briefs.GetAsync(Sent, ct) ?? throw new InvalidOperationException("The sent brief wasn't seeded.");
            await briefs.MarkPresentedAsync(brief.Id, ct: ct);
            foreach (var edition in new[] { Edition.Team, Edition.Public })
                await distribution.SendAsync(await distribution.PlanAsync(Sent, edition, ct), ct: ct);
        }

        // --- a stand-in screenshot ---------------------------------------------------------------------------------------

        private static readonly (byte, byte, byte) Teal = (16, 124, 138);
        private static readonly (byte, byte, byte) Red = (178, 34, 46);

        /// <summary>A small PNG that reads as a screenshot of a report: a title bar, lines of text and a panel.</summary>
        private static byte[] Png((byte R, byte G, byte B) ink)
        {
            const int w = 640, h = 300;
            var bg = ((byte)(255 - (255 - ink.R) / 10), (byte)(255 - (255 - ink.G) / 10), (byte)(255 - (255 - ink.B) / 10));
            var raw = new byte[h * (w * 3 + 1)];
            for (var y = 0; y < h; y++)
            {
                raw[y * (w * 3 + 1)] = 0;
                for (var x = 0; x < w; x++)
                {
                    var c = bg;
                    if (y is >= 24 and < 44 && x is >= 24 and < 616) c = ink;
                    else if (y is >= 64 and < 270 && x is >= 24 and < 300) c = ink;
                    else if (x is >= 324 and < 616 && (y - 70) % 26 is >= 0 and < 10 && y is >= 70 and < 270
                             && x < 616 - (y * 37 % 120)) c = (150, 158, 166);
                    var i = y * (w * 3 + 1) + 1 + x * 3;
                    (raw[i], raw[i + 1], raw[i + 2]) = c;
                }
            }
            using var ms = new MemoryStream();
            ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, w);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), h);
            header[8] = 8; header[9] = 2;
            Chunk(ms, "IHDR", header);
            using (var z = new MemoryStream())
            {
                using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zl.Write(raw);
                Chunk(ms, "IDAT", z.ToArray());
            }
            Chunk(ms, "IEND", []);
            return ms.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
            s.Write(four);
            var body = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type, body);
            data.CopyTo(body, 4);
            s.Write(body);
            BinaryPrimitives.WriteUInt32BigEndian(four, Crc(body));
            s.Write(four);
        }

        private static uint Crc(byte[] bytes)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                c ^= b;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xFFFFFFFFu;
        }
    }
}
