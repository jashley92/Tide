using AwesomeAssertions;
using Tide.Application.Stack;
using Tide.Domain.Entities;

namespace Tide.UnitTests;

/// <summary>The Our stack list's rules: which products need a look, when a check is old, and what "Right now" may name.</summary>
public class StackAttentionTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly StackNow Quiet = new([], 0, 0);

    private static StackProduct Product(int? checkedDaysAgo = 1, bool owned = true, bool retired = false) => new()
    {
        Name = "GitLab", TeamId = owned ? Team : null, CheckedOn = checkedDaysAgo is { } d ? Today.AddDays(-d) : null, IsRetired = retired,
    };

    [Fact]
    public void ACheck_IsOld_WhenItWasNeverMade_OrIsPastTheThreshold()
    {
        StackService.CheckIsOld(null, Today).Should().BeTrue("never checked");
        StackService.CheckIsOld(Today, Today).Should().BeFalse();
        StackService.CheckIsOld(Today.AddDays(-StackService.CheckOldAfterDays), Today).Should().BeFalse("the threshold itself isn't past it");
        StackService.CheckIsOld(Today.AddDays(-StackService.CheckOldAfterDays - 1), Today).Should().BeTrue();
    }

    [Fact]
    public void AProduct_NeedsALook_WhenSomethingIsHappening_ItsCheckIsOldOrMissing_OrNoTeamOwnsIt()
    {
        // A row as the service makes it: the check is old only for a product in use.
        static StackRow Row(StackProduct p, StackNow now) => new(p, null, 0, 0, now, !p.IsRetired && StackService.CheckIsOld(p.CheckedOn, Today));

        Row(Product(), Quiet).NeedsLook.Should().BeFalse("owned, checked lately, nothing new");
        Row(Product(), Quiet with { TriageItems = null }).NeedsLook.Should().BeFalse("nor for someone who can't see triage");

        Row(Product(), Quiet with { Cves = [new StackCve("CVE-2026-1", null)] }).NeedsLook.Should().BeTrue("a CVE on a recent brief");
        Row(Product(), Quiet with { CvesAbove = 1 }).NeedsLook.Should().BeTrue("a CVE that's counted and not named");
        Row(Product(), Quiet with { TriageItems = 2 }).NeedsLook.Should().BeTrue("new items in triage");
        Row(Product(checkedDaysAgo: StackService.CheckOldAfterDays + 1), Quiet).NeedsLook.Should().BeTrue("an old check");
        Row(Product(checkedDaysAgo: null), Quiet).NeedsLook.Should().BeTrue("never checked");
        Row(Product(owned: false), Quiet).NeedsLook.Should().BeTrue("no owning team");

        var retired = Row(Product(checkedDaysAgo: null, owned: false, retired: true), Quiet with { TriageItems = 2 });
        (retired.NeedsLook, retired.CheckOld, retired.NoTeam).Should().Be((false, false, false), "a retired product asks for nothing");
    }

    [Fact]
    public void TheList_PutsTheMostPressingFirst_ThenHousekeeping_ThenWhatNeedsNothing()
    {
        static StackCve Cve(decimal? cvss, bool kev = false) => new("CVE-2026-1", cvss is null && !kev ? null : new CveRecord { CvssScore = cvss, IsKev = kev });
        StackRow Row(string name, StackNow now, int? checkedDaysAgo = 1, bool owned = true, bool retired = false)
        {
            var p = Product(checkedDaysAgo, owned, retired);
            p.Name = name;
            return new StackRow(p, null, 0, 0, now, !retired && StackService.CheckIsOld(p.CheckedOn, Today));
        }
        // As the service reads them: by name, the retired last.
        var byName = new[]
        {
            Row("A never checked", Quiet, checkedDaysAgo: null),
            Row("B quiet", Quiet),
            Row("C triage 1", Quiet with { TriageItems = 1 }),
            Row("D cve unscored", Quiet with { Cves = [Cve(null)] }),
            Row("E cve 9.8", Quiet with { Cves = [Cve(5m), Cve(9.8m)] }),
            Row("F counted only", Quiet with { CvesAbove = 1 }),
            Row("G kev 7.5", Quiet with { Cves = [Cve(7.5m, kev: true)] }),
            Row("H no team", Quiet, owned: false),
            Row("I triage 3", Quiet with { TriageItems = 3 }),
            Row("J kev 9.0 never checked", Quiet with { Cves = [Cve(9m, kev: true)] }, checkedDaysAgo: null),
            Row("K cve 9.8 and triage", Quiet with { Cves = [Cve(9.8m)], TriageItems = 2 }),
            Row("L quiet too", Quiet),
            Row("M retired", Quiet with { Cves = [Cve(10m, kev: true)], TriageItems = 5 }, checkedDaysAgo: null, owned: false, retired: true),
        };

        StackService.MostPressingFirst(byName).Select(r => r.Product.Name).Should().Equal(
            "J kev 9.0 never checked", "G kev 7.5",                                        // on KEV, the worse score first
            "K cve 9.8 and triage", "E cve 9.8", "D cve unscored", "F counted only",       // a CVE: worst score, then more in triage, then name
            "I triage 3", "C triage 1",                                                    // new in triage, more first
            "A never checked", "H no team",                                                // housekeeping, by name
            "B quiet", "L quiet too", "M retired");                                        // nothing to do, by name; retired last

        // Someone who can't triage has no counts, so triage plays no part in their order.
        var blind = byName.Select(r => r with { Now = r.Now with { TriageItems = null } }).ToList();
        StackService.MostPressingFirst(blind).Select(r => r.Product.Name).Should().Equal(
            "J kev 9.0 never checked", "G kev 7.5", "E cve 9.8", "K cve 9.8 and triage", "D cve unscored", "F counted only",
            "A never checked", "H no team", "B quiet", "C triage 1", "I triage 3", "L quiet too", "M retired");
    }

    [Fact]
    public void RightNow_NamesEachCveOnce_TheWorstFirst()
    {
        var kev = new CveRecord { CveId = "CVE-2026-0003", CvssScore = 7.5m, EpssScore = 0.2m, IsKev = true };
        var critical = new CveRecord { CveId = "CVE-2026-0002", CvssScore = 9.8m, EpssScore = 0.1m };
        var likely = new CveRecord { CveId = "CVE-2026-0004", CvssScore = 9.8m, EpssScore = 0.9m };

        var now = StackService.RightNow(
        [
            new("CVE-2026-0001", false, null), new("CVE-2026-0002", false, critical), new("CVE-2026-0003", false, kev),
            new("CVE-2026-0004", false, likely), new("CVE-2026-0003", false, kev),
        ], triageItems: 3);

        now.Cves.Select(c => c.Id).Should().Equal(["CVE-2026-0003", "CVE-2026-0004", "CVE-2026-0002", "CVE-2026-0001"],
            "on KEV first, then by CVSS, then by EPSS; one not enriched yet last");
        now.Cves[0].Record.Should().BeSameAs(kev);
        (now.CvesAbove, now.TriageItems, now.Any).Should().Be((0, 3, true));
        StackService.RightNow([], 0).Any.Should().BeFalse();
        StackService.RightNow([], null).Any.Should().BeFalse();
    }

    [Fact]
    public void RightNow_CountsButNeverNamesACve_ThatComesOnlyFromAnArticleAboveTheCeiling()
    {
        var hidden = new CveRecord { CveId = "CVE-2026-0666", CvssScore = 10m, IsKev = true };
        var both = new CveRecord { CveId = "CVE-2026-0002", CvssScore = 8m };

        var now = StackService.RightNow(
        [
            new("CVE-2026-0666", true, hidden), new("CVE-2026-0666", true, hidden),   // two articles, both above the ceiling
            new("CVE-2026-0002", true, both), new("CVE-2026-0002", false, both),      // one above it, one not: named
        ], triageItems: null);

        now.Cves.Select(c => c.Id).Should().Equal("CVE-2026-0002");
        now.Cves.Should().NotContain(c => c.Record == hidden, "nothing about it is kept: not its id, score or KEV listing");
        now.CvesAbove.Should().Be(1);
        now.Any.Should().BeTrue();
    }

    [Fact]
    public void AllNamesInOnePass_FindsExactlyWhatMatchingEachProductDoes()
    {
        var entries = new[]
        {
            new StackProduct { Name = "Ivanti Connect Secure", OtherNames = "Pulse Secure; ICS" }, new StackProduct { Name = "Go" },
            new StackProduct { Name = "Connect" }, new StackProduct { Name = "Secure Mail", OtherNames = "Go" }, new StackProduct { Name = "C++" },
        }.Select(StackService.Entry).ToList();
        var names = new StackNames(entries);

        foreach (var (title, summary) in new (string, string?)[]
        {
            ("Attackers chain three Ivanti Connect-Secure flaws", null), ("Old VPN bug", "Pulse secure appliances are targeted again"),
            ("Google patches Chrome", null), ("Go 1.26 fixes a flaw", "Connect to it"), ("ICS", null), ("", "secure mail"), ("", null),
            ("  Connect  ", "go"), ("Secure", "Mail"), ("A C++ compiler bug", null), ("Unrelated", new string('x', StackService.SummaryStart) + " Pulse Secure"),
        })
        {
            var words = StackService.Words(title, summary);
            names.In(words).Select(e => e.Name).Should().BeEquivalentTo(entries.Where(e => StackService.Names(e, words)).Select(e => e.Name), $"for '{title}' / '{summary}'");
        }
        new StackNames([]).In(StackService.Words("Go", null)).Should().BeEmpty();
    }
}
