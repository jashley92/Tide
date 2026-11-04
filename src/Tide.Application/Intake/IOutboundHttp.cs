namespace Tide.Application.Intake;

/// <summary>Intake settings (config section <c>Intake</c>).</summary>
public sealed class IntakeOptions
{
    /// <summary>Whether sources are polled at all. Off by default: an install chooses to reach out to the internet.</summary>
    public bool Enabled { get; set; }

    /// <summary>A proxy for outbound requests ("http://proxy.example.org:8080"); blank goes direct. Read at startup.</summary>
    public string Proxy { get; set; } = "";

    /// <summary>
    /// Whether feeds on private addresses (10/8, 172.16/12, 192.168/16, 100.64/10, IPv6 unique-local) may be fetched: off
    /// by default, so a feed can't redirect the poller into the internal network. Loopback and link-local addresses are
    /// never fetched from.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>How long one request may take, in seconds (5 to 120).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>The largest response read, in kilobytes; bigger is refused.</summary>
    public int MaxResponseKB { get; set; } = 8192;

    /// <summary>The shortest poll interval a source may have, in minutes.</summary>
    public int MinPollMinutes { get; set; } = 15;

    /// <summary>Days a feed item that wasn't shortlisted or promoted is kept.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Whether a feed item that reads as sponsored isn't taken in (<see cref="SponsoredFilter"/>). On by default.</summary>
    public bool SkipSponsored { get; set; } = true;

    /// <summary>The phrases that mark an item as sponsored ("Sponsored by; Partner content"); null for <see cref="SponsoredFilter.DefaultMarkers"/>.</summary>
    public string? SponsoredMarkers { get; set; }

    /// <summary>The phrases that contain a marker but aren't sponsorship ("state-sponsored"); null for <see cref="SponsoredFilter.DefaultExceptions"/>.</summary>
    public string? SponsoredExceptions { get; set; }

    /// <summary>The CSS selectors of adverts taken out of an article's page before its text is found
    /// ("ins.adsbygoogle; [data-ad-slot]"); null for <see cref="Assist.ArticleCleanup.DefaultSelectors"/>.</summary>
    public string? AdSelectors { get; set; }

    /// <summary>The lines that only label an advert, dropped from an article's text ("Advertisement; Story continues below*");
    /// null for <see cref="Assist.ArticleCleanup.DefaultLabels"/>.</summary>
    public string? AdLabels { get; set; }

    /// <summary>Days a new item waits untouched before it folds into "Older" (it's kept and searchable). KEV-listed items,
    /// items with an EPSS score of <see cref="IntakeService.AgeExemptEpss"/> or more, and items naming a product on Our
    /// stack never age.</summary>
    public int AgeDays { get; set; } = 7;

    /// <summary>Days an article's text read in triage is kept (1 to 30), for colleagues and the editor to reuse.</summary>
    public int ReadingDays { get; set; } = 7;

    /// <summary>How long reading an article's page (in triage, or for the editor) may take, in seconds (2 to 60), before
    /// it's refused: <see cref="Assist.ArticleText.TimeBudget"/> suits most servers, and a slow one is given longer.</summary>
    public int ReadSeconds { get; set; } = 6;

    /// <summary><see cref="ReadSeconds"/>, as the reader takes it.</summary>
    public TimeSpan ReadBudget => TimeSpan.FromSeconds(Math.Clamp(ReadSeconds, 2, 60));
}

public enum FetchStatus
{
    Ok = 0,

    /// <summary>The server said nothing changed since the validators sent (304).</summary>
    NotModified = 1,
    Failed = 2,
}

/// <summary>A GET's extras: validators for a conditional fetch, and headers (an API key).</summary>
public sealed record OutboundRequest(string? ETag = null, string? LastModified = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>What a GET returned. <see cref="Error"/> is safe to show an administrator.</summary>
public sealed record HttpFetch(FetchStatus Status, byte[]? Content = null, string? ETag = null, string? LastModified = null, string? Error = null);

/// <summary>
/// Outbound GETs to configured addresses only (sources an administrator added, the CVE data services): with a time
/// limit, a size limit, an optional proxy, and a back-off for a host that keeps failing. Never throws for a failure
/// the remote end caused; the result says what went wrong.
/// </summary>
public interface IOutboundHttp
{
    Task<HttpFetch> GetAsync(string url, OutboundRequest? request = null, CancellationToken ct = default);
}
