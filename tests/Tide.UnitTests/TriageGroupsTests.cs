using FluentAssertions;
using Tide.Application.Intake;

namespace Tide.UnitTests;

public class TriageGroupsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 6, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, Guid> Feeds = new();

    private static GroupCandidate Item(string title, string feed, double hoursAgo, params string[] cves) =>
        new(Guid.NewGuid(), title, Feeds.TryGetValue(feed, out var id) ? id : Feeds[feed] = Guid.NewGuid(), feed, Now.AddHours(-hoursAgo), cves);

    // The review's Binance theft: five reports from four feeds, every headline worded differently.
    private static List<GroupCandidate> Binance() =>
    [
        Item("$6 Billion Binance Hack Bears Hallmarks of North Korea’s TraderTraitor", "SecurityWeek", 1.7),
        Item("Binance links $6.3 billion crypto theft to compromised DevOps laptop", "BleepingComputer", 0.7),
        Item("Binance Suffers Security Breach Affecting $6.3 Billion in Assets", "The Hacker News", 5.8),
        Item("Binance hit by record $6.3 billion crypto hack, scrambles to freeze funds", "The Record", 10),
        Item("Binance probes potential $5.8 billion crypto-theft hack", "BleepingComputer", 11.25),
    ];

    private static List<GroupCandidate> Polymarket() =>
    [
        Item("Polymarket users lose funds after Magic Link login flaw", "BleepingComputer", 3),
        Item("Polymarket Accounts Drained Through Magic Link Authentication Weakness", "SecurityWeek", 4),
    ];

    [Fact]
    public void TheBinanceHeadlines_AreOneEvent_FromFourFeeds_NewestFirst()
    {
        var items = Binance();
        var group = TriageGroups.Find(items).Should().ContainSingle().Subject;

        group.Ids.Should().HaveCount(5);
        group.Ids[0].Should().Be(items[1].Id, "the DevOps-laptop report is the newest");
        group.Newest.Should().Be(items[1].Id);
        group.Ids.Select(id => items.Single(i => i.Id == id).At).Should().BeInDescendingOrder();
        group.Sources.Should().Be(4);
        group.Shared.Should().StartWith(["Binance", "$6.3 billion"], "the names and the amount every headline shares, as the newest spells them");
        group.Cves.Should().BeEmpty();
    }

    [Fact]
    public void BinanceAndPolymarket_AreTwoEvents()
    {
        var binance = Binance();
        var polymarket = Polymarket();
        var groups = TriageGroups.Find([.. binance, .. polymarket]);

        groups.Should().HaveCount(2);
        groups.Should().ContainSingle(g => g.Ids.Count == 5).Which.Ids.Should().BeEquivalentTo(binance.Select(i => i.Id));
        groups.Should().ContainSingle(g => g.Ids.Count == 2).Which.Ids.Should().BeEquivalentTo(polymarket.Select(i => i.Id));
        groups[0].Ids.Should().HaveCount(5, "the group with the newest report leads");
    }

    [Fact]
    public void TwoUnrelatedCriticalFlaws_DontGroup()
    {
        TriageGroups.Find(
        [
            Item("Critical Flaw Exploited in Cisco Firewalls, Patch Now", "SecurityWeek", 1),
            Item("Critical Flaw Exploited in Ivanti VPN, Patch Now", "The Hacker News", 2),
            Item("Critical security update fixes exploited vulnerability in Fortinet FortiOS", "BleepingComputer", 3),
        ]).Should().BeEmpty("hack, flaw, exploit, critical, patch and update are in every headline");
    }

    [Fact]
    public void OneNameAlone_IsntOneEvent()
    {
        TriageGroups.Find(
        [
            Item("Microsoft zero-day exploited in the wild", "SecurityWeek", 1),
            Item("Microsoft Teams phishing campaign targets finance staff", "The Hacker News", 2),
        ]).Should().BeEmpty("they share only Microsoft");
    }

    [Fact]
    public void ASharedCve_IsEnough_AndEveryCveIsListed()
    {
        var items = new List<GroupCandidate>
        {
            Item("Max-Severity GitLab Vulnerability Exploited Against Alpine Linux", "SecurityWeek", 1, "CVE-2026-97229"),
            Item("Attackers abuse regex engine bug", "The Hacker News", 6, "CVE-2026-97229", "CVE-2026-1000"),
        };
        var group = TriageGroups.Find(items).Should().ContainSingle().Subject;
        group.Cves.Should().Equal("CVE-2026-1000", "CVE-2026-97229");
        group.Shared.Should().BeEmpty("the headlines share no names");
    }

    [Fact]
    public void ReportsMoreThan48HoursApart_DontGroup()
    {
        TriageGroups.Find(
        [
            Item("Binance links $6.3 billion crypto theft to DevOps laptop", "SecurityWeek", 0),
            Item("Binance probes potential $5.8 billion crypto-theft", "The Record", 49),
        ]).Should().BeEmpty();
    }

    [Fact]
    public void OneFeedsOwnReports_ArentOffered()
    {
        TriageGroups.Find(
        [
            Item("Binance links $6.3 billion crypto theft to DevOps laptop", "BleepingComputer", 1),
            Item("Binance probes potential $5.8 billion crypto-theft", "BleepingComputer", 2),
        ]).Should().BeEmpty("a group needs two feeds");
    }

    [Fact]
    public void ABigRun_IsCappedAtEight_KeepingTheTightestPairs()
    {
        // Ten reports of one CVE: the eight closest together make the group, and the two far behind make their own.
        var items = Enumerable.Range(0, 10)
            .Select(i => Item($"Report {i} on the Exchange bug", "Feed " + (i % 3), i < 8 ? i * 0.1 : 20 + i, "CVE-2026-5555"))
            .ToList();
        var groups = TriageGroups.Find(items);
        groups.Should().HaveCount(2);
        groups[0].Ids.Should().HaveCount(TriageGroups.MaxSize).And.BeEquivalentTo(items.Take(8).Select(i => i.Id));
        groups[1].Ids.Should().BeEquivalentTo(items.Skip(8).Select(i => i.Id));
    }

    [Fact]
    public void Tokens_KeepNamesAndAmounts_AndDropCommonWordsAndThePublisher()
    {
        var tokens = TriageGroups.Tokens("The Record: Binance hit by record $6.3 billion hack, 16 million users exposed via iOS 18 and Log4j", "The Record");
        tokens.Keys.Should().BeEquivalentTo("binance", "$billion", "#16million", "ios", "log4j");
        tokens["$billion"].Shown.Should().Be("$6.3 billion");
        tokens["$billion"].Name.Should().BeFalse();
        tokens["binance"].Name.Should().BeTrue();

        TriageGroups.Tokens("Hackers Exploited Patched Flaws, Hitachi Says", "SecurityWeek").Keys
            .Should().BeEquivalentTo(["hitachi"], "an ending is trimmed to find a common word, but a name that starts like one stays");
        TriageGroups.Tokens("$6B stolen; €20m more; $500,000 ransom", "x").Keys.Should().BeEquivalentTo("$billion", "$million", "$500000");
    }

    [Fact]
    public void ARoundupNamingTwoEvents_DoesntChainThemIntoOneGroup()
    {
        // The review's case: a CISA roundup shared names with a GitLab story and with a Cisco one, and pulled both into one group.
        var gitlab = Item("GitLab Patches Critical RCE in Self-Managed GitLab Runner", "SecurityWeek", 1);
        var gitlab2 = Item("GitLab Runner RCE Exploited, Patch GitLab Now", "The Hacker News", 2);
        var roundup = Item("CISA Adds GitLab Runner RCE and Cisco ASA Firewall Bugs to KEV Catalog", "BleepingComputer", 3);
        var cisco = Item("Attackers Exploit Second Cisco ASA Firewall Zero-Day", "The Record", 4);
        var cisco2 = Item("Cisco Confirms ASA Firewall Zero-Day Exploitation", "Dark Reading", 5);

        var groups = TriageGroups.Find([gitlab, gitlab2, roundup, cisco, cisco2]);

        groups.Should().NotContain(g => g.Ids.Contains(gitlab.Id) && g.Ids.Contains(cisco.Id), "every report must match the group's first");
        groups.Should().Contain(g => g.Ids.Contains(gitlab.Id) && g.Ids.Contains(gitlab2.Id));
        groups.Should().Contain(g => g.Ids.Contains(cisco.Id) && g.Ids.Contains(cisco2.Id));
        groups.SelectMany(g => g.Shared).Should().NotContain("Adds", "a headline verb isn't a shared name");
    }

    [Fact]
    public void NothingToGroup_IsNoGroups()
    {
        TriageGroups.Find([]).Should().BeEmpty();
        TriageGroups.Find([Item("Binance links $6.3 billion crypto theft", "SecurityWeek", 1)]).Should().BeEmpty();
    }
}
