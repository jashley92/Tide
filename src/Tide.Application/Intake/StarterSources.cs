using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>One public source offered on the Sources page's starter list, as it's added.</summary>
public sealed record StarterSource(string Name, SourceKind Kind, string Url, SourceReliability Reliability, int PollMinutes, bool Recommended, string Group);

/// <summary>
/// A short list of public cyber security sources a new install can start from: a recommended dozen or so (the primary
/// sources others report on, news with its own reporting, and the threat research that comes with indicators), and more
/// for a team that wants them. Each was checked to fetch and parse with dated items (November 2026), at its final
/// address rather than one that redirects. All are TLP:CLEAR and added paused, so nothing is polled until someone chooses.
/// </summary>
public static class StarterSources
{
    public const string Government = "Government and CERTs", News = "News", Research = "Threat research";

    private const int Hourly = 60, TwoHourly = 120, FourHourly = 240, SixHourly = 360;

    public static readonly IReadOnlyList<StarterSource> All =
    [
        // Recommended
        new("CISA KEV", SourceKind.CisaKev, "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json", SourceReliability.B, SixHourly, true, Government),
        // About 6 MB a read, so polled less often than the rest.
        new("CISA cybersecurity advisories", SourceKind.Feed, "https://www.cisa.gov/cybersecurity-advisories/all.xml", SourceReliability.B, SixHourly, true, Government),
        new("NCSC UK", SourceKind.Feed, "https://www.ncsc.gov.uk/api/1/services/v1/all-rss-feed.xml", SourceReliability.B, TwoHourly, true, Government),
        new("SANS Internet Storm Center", SourceKind.Feed, "https://isc.sans.edu/rssfeed_full.xml", SourceReliability.B, TwoHourly, true, Government),
        new("BleepingComputer", SourceKind.Feed, "https://www.bleepingcomputer.com/feed/", SourceReliability.C, Hourly, true, News),
        new("The Record", SourceKind.Feed, "https://therecord.media/feed", SourceReliability.C, Hourly, true, News),
        new("SecurityWeek", SourceKind.Feed, "https://www.securityweek.com/feed/", SourceReliability.C, Hourly, true, News),
        new("Krebs on Security", SourceKind.Feed, "https://krebsonsecurity.com/feed/", SourceReliability.C, FourHourly, true, News),
        new("Microsoft Security Blog", SourceKind.Feed, "https://www.microsoft.com/en-us/security/blog/feed/", SourceReliability.B, FourHourly, true, Research),
        // Google's own FeedBurner feed for the blog (its cloud.google.com feed addresses serve the web page).
        new("Google Threat Intelligence", SourceKind.Feed, "https://feeds.feedburner.com/threatintelligence/pvexyqv7v0v", SourceReliability.B, FourHourly, true, Research),
        new("Cisco Talos", SourceKind.Feed, "https://blog.talosintelligence.com/rss/", SourceReliability.B, FourHourly, true, Research),
        new("Palo Alto Unit 42", SourceKind.Feed, "https://unit42.paloaltonetworks.com/feed/", SourceReliability.B, FourHourly, true, Research),
        new("The DFIR Report", SourceKind.Feed, "https://thedfirreport.com/feed/", SourceReliability.B, FourHourly, true, Research),

        // More
        new("CISA news", SourceKind.Feed, "https://www.cisa.gov/news.xml", SourceReliability.B, FourHourly, false, Government),
        new("Canadian Centre for Cyber Security", SourceKind.Feed, "https://www.cyber.gc.ca/api/cccs/atom/v1/get?feed=alerts_advisories&lang=en", SourceReliability.B, FourHourly, false, Government),
        new("The Hacker News", SourceKind.Feed, "https://feeds.feedburner.com/TheHackersNews", SourceReliability.C, Hourly, false, News),
        new("Dark Reading", SourceKind.Feed, "https://www.darkreading.com/rss.xml", SourceReliability.C, Hourly, false, News),
        new("Help Net Security", SourceKind.Feed, "https://www.helpnetsecurity.com/feed/", SourceReliability.C, Hourly, false, News),
        new("CyberScoop", SourceKind.Feed, "https://cyberscoop.com/feed/", SourceReliability.C, Hourly, false, News),
        new("Infosecurity Magazine", SourceKind.Feed, "https://www.infosecurity-magazine.com/rss/news/", SourceReliability.C, Hourly, false, News),
        new("Security Affairs", SourceKind.Feed, "https://securityaffairs.com/feed", SourceReliability.C, Hourly, false, News),
        new("Ars Technica security", SourceKind.Feed, "https://arstechnica.com/security/feed/", SourceReliability.C, TwoHourly, false, News),
        new("The Register security", SourceKind.Feed, "https://api.theregister.com/api/v1/article?orderBy=published&site_id=2&remapper=rss&query=tag:security", SourceReliability.C, TwoHourly, false, News),
        new("Wired security", SourceKind.Feed, "https://www.wired.com/feed/category/security/latest/rss", SourceReliability.C, TwoHourly, false, News),
        new("Schneier on Security", SourceKind.Feed, "https://www.schneier.com/feed/atom/", SourceReliability.C, FourHourly, false, News),
        new("Google Project Zero", SourceKind.Feed, "https://projectzero.google/feed.xml", SourceReliability.B, FourHourly, false, Research),
        new("CrowdStrike", SourceKind.Feed, "https://www.crowdstrike.com/en-us/blog/feed", SourceReliability.B, FourHourly, false, Research),
        new("Kaspersky Securelist", SourceKind.Feed, "https://securelist.com/feed/", SourceReliability.B, FourHourly, false, Research),
        new("ESET WeLiveSecurity", SourceKind.Feed, "https://feeds.feedburner.com/eset/blog", SourceReliability.B, FourHourly, false, Research),
        new("Check Point Research", SourceKind.Feed, "https://research.checkpoint.com/feed/", SourceReliability.B, FourHourly, false, Research),
        new("SentinelOne Labs", SourceKind.Feed, "https://www.sentinelone.com/labs/feed/", SourceReliability.B, FourHourly, false, Research),
        new("Sophos X-Ops", SourceKind.Feed, "https://www.sophos.com/en-us/category/threat-research/feed", SourceReliability.B, FourHourly, false, Research),
        new("Rapid7", SourceKind.Feed, "https://www.rapid7.com/rss.xml", SourceReliability.B, FourHourly, false, Research),
        new("Huntress", SourceKind.Feed, "https://www.huntress.com/blog/rss.xml", SourceReliability.B, FourHourly, false, Research),
        new("Recorded Future", SourceKind.Feed, "https://www.recordedfuture.com/feed", SourceReliability.B, FourHourly, false, Research),
        new("Volexity", SourceKind.Feed, "https://www.volexity.com/feed/", SourceReliability.B, FourHourly, false, Research),
        new("Proofpoint threat insight", SourceKind.Feed, "https://www.proofpoint.com/us/threat-insight-blog.xml", SourceReliability.B, FourHourly, false, Research),
        new("Malwarebytes Labs", SourceKind.Feed, "https://www.malwarebytes.com/blog/feed/index.xml", SourceReliability.B, FourHourly, false, Research),
        new("Fortinet threat research", SourceKind.Feed, "https://feeds.fortinet.com/fortinet/blog/threat-research", SourceReliability.B, FourHourly, false, Research),
        new("Tenable", SourceKind.Feed, "https://www.tenable.com/blog/feed", SourceReliability.B, FourHourly, false, Research),
        new("Qualys threat research", SourceKind.Feed, "https://blog.qualys.com/vulnerabilities-threat-research/feed", SourceReliability.B, FourHourly, false, Research),
        new("Zero Day Initiative advisories", SourceKind.Feed, "https://www.zerodayinitiative.com/rss/published/", SourceReliability.B, FourHourly, false, Research),
    ];
}
