using AwesomeAssertions;
using Tide.Application.Intake;
using Tide.Domain.Entities;

namespace Tide.UnitTests;

/// <summary>The KEV round-up's bullets (<see cref="KevRoundup.Bullets"/>): one per KEV-listed CVE, from TIDE's own records.</summary>
public class KevRoundupTests
{
    private static CveRecord Kev(string id, decimal? cvss = 9.8m, decimal? epss = 0.94m, DateOnly? due = null, string? vendor = "Microsoft",
        string? product = "Exchange Server", bool ransomware = false) => new()
    {
        CveId = id, IsKev = true, CvssScore = cvss, EpssScore = epss, KevDueDate = due, Vendor = vendor, Product = product, KevRansomware = ransomware,
    };

    [Fact]
    public void ABullet_SaysWhatItAffects_CvssEpss_AndTheDueDate()
    {
        KevRoundup.Bullets([Kev("CVE-2026-1234", due: new DateOnly(2026, 12, 5), ransomware: true)])
            .Should().Equal("**CVE-2026-1234**: Microsoft Exchange Server. CVSS 9.8, EPSS 94%, due 2026-12-05, ransomware use.");
    }

    [Fact]
    public void WhatARecordDoesntKnow_IsSaid_RatherThanLeftOut()
    {
        KevRoundup.Bullets([Kev("CVE-2026-0001", cvss: null, epss: null, vendor: null, product: null)])
            .Should().Equal("**CVE-2026-0001**: Product not known. CVSS not known, EPSS not known, no due date.");
    }

    [Fact]
    public void OnlyKevListedCves_EachOnce_SoonestDueFirst_ThenHighestCvss()
    {
        var bullets = KevRoundup.Bullets(
        [
            Kev("CVE-2026-0003", cvss: 7.5m, due: new DateOnly(2026, 12, 1)),
            new CveRecord { CveId = "CVE-2026-0009", IsKev = false, CvssScore = 10m },
            Kev("CVE-2026-0002", cvss: 9.1m, due: new DateOnly(2026, 12, 1)),
            Kev("cve-2026-0002", cvss: 9.1m, due: new DateOnly(2026, 12, 1)),
            Kev("CVE-2026-0001", cvss: 10m, due: null),
        ]);

        bullets.Select(b => b[2..15]).Should().Equal("CVE-2026-0002", "CVE-2026-0003", "CVE-2026-0001");
    }

    [Fact]
    public void PastAnArticlesBullets_TheLastNamesTheRest()
    {
        var records = Enumerable.Range(1, Article.MaxBullets + 5).Select(n => Kev($"CVE-2026-{n:0000}", cvss: 5m)).ToList();

        var bullets = KevRoundup.Bullets(records);

        bullets.Should().HaveCount(Article.MaxBullets);
        bullets[^1].Should().StartWith("And 6 more on KEV: CVE-2026-0030, CVE-2026-0031");
    }
}
