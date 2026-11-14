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

    /// <summary>The User-Agent requests send (<see cref="UserAgents"/>): TIDE's own (the default), a current browser's,
    /// or <see cref="CustomUserAgent"/>. A source can choose its own for its feed and articles.</summary>
    public Domain.Enums.UserAgentChoice UserAgent { get; set; } = Domain.Enums.UserAgentChoice.Tide;

    /// <summary>The string sent when <see cref="UserAgent"/> is Custom; TIDE's own is sent while it's blank.</summary>
    public string? CustomUserAgent { get; set; }

    /// <summary>How long one request may take, in seconds (5 to 120).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>The largest response read, in kilobytes; bigger is refused.</summary>
    public int MaxResponseKB { get; set; } = 8192;

    /// <summary>The shortest poll interval a source may have, in minutes.</summary>
    public int MinPollMinutes { get; set; } = 15;

    /// <summary>The analyst pull-now window, in minutes (at least 1): a source pulled within it, on schedule or by hand,
    /// can't be pulled again from Triage's Sources tab until it's passed. Administrators' Poll now on Sources isn't held to it.</summary>
    public int PullNowMinutes { get; set; } = 15;

    /// <summary>The time of day ("08:00", 24-hour, in the organization's zone) the feeds should have been pulled by, each
    /// working day: from then on, Today's Triage step says so to analysts when one hasn't been (<see cref="FeedFreshness"/>).</summary>
    public string PulledBy { get; set; } = "08:00";

    /// <summary><see cref="PulledBy"/> as a time; 08:00 when it can't be read.</summary>
    public TimeOnly PulledByTime =>
        TimeOnly.TryParseExact(PulledBy?.Trim(), ["HH:mm", "H:mm"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? t : new TimeOnly(8, 0);

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

    /// <summary>How far apart two reports of one event can be published for triage to group them, in hours (12 to 96).</summary>
    public int GroupWindowHours { get; set; } = (int)TriageGroups.DefaultWindow.TotalHours;

    /// <summary>Words triage's grouping ignores besides the built-in common ones ("outage; webinar"); not the intake's
    /// duplicate check (<see cref="TitleMatch"/>).</summary>
    public string? GroupIgnoredWords { get; set; }

    /// <summary>What triage groups by: <see cref="GroupWindowHours"/> and <see cref="GroupIgnoredWords"/> (the splits are
    /// the inbox's to add).</summary>
    public TriageGroupOptions Grouping()
    {
        // The words as parsed, kept until they change: the inbox asks on every load.
        if (_ignored is not { } kept || kept.From != GroupIgnoredWords) _ignored = kept = new(GroupIgnoredWords, TriageGroups.Words(GroupIgnoredWords));
        return new()
        {
            Window = TimeSpan.FromHours(Math.Clamp(GroupWindowHours, TriageGroups.MinWindowHours, TriageGroups.MaxWindowHours)),
            Ignored = kept.Words,
        };
    }

    private sealed record ParsedWords(string? From, IReadOnlySet<string> Words);

    private ParsedWords? _ignored;
}

public enum FetchStatus
{
    Ok = 0,

    /// <summary>The server said nothing changed since the validators sent (304).</summary>
    NotModified = 1,
    Failed = 2,
}

/// <summary>A GET's extras: validators for a conditional fetch, headers (an API key), and the User-Agent to send instead
/// of the global one (a source's own choice, <see cref="UserAgents.For"/>; one that can't be sent is ignored).</summary>
public sealed record OutboundRequest(string? ETag = null, string? LastModified = null, IReadOnlyDictionary<string, string>? Headers = null,
    string? UserAgent = null);

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
