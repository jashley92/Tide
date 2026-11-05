namespace Tide.Domain.Enums;

/// <summary>The built-in roles. Each maps to AD groups (administered in the app) and grants a fixed permission set.</summary>
public enum AppRole
{
    /// <summary>The CTI team: triages, writes, presents and sends the brief.</summary>
    Analyst = 0,

    /// <summary>Another team's member (Vulnerability Management, Cyber Defense): reads the team edition and works the
    /// actions given to their team.</summary>
    ActionOwner = 1,

    /// <summary>Anyone else in the company: reads the published (public-edition) archive.</summary>
    Reader = 2,

    /// <summary>Administers sources, taxonomy, role mappings and settings. Holds every permission.</summary>
    Admin = 3,
}

/// <summary>The permission atoms authorization runs on. Roles bundle them; policies require one each.</summary>
public enum Permission
{
    ReadPublished = 0,
    ReadTeamEdition = 1,
    WriteArticles = 2,
    PresentBriefs = 3,
    WorkActions = 4,
    Administer = 5,
    EnterMetrics = 6,
}

/// <summary>Whether an article asks something of the organization.</summary>
public enum Classification
{
    /// <summary>Interesting, but no action expected.</summary>
    Informational = 0,

    /// <summary>Something should be done: a hunt, a block, a patch, a config change.</summary>
    Threat = 1,
}

/// <summary>How urgently a threat needs attention. P1 is the most urgent.</summary>
public enum Priority
{
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4,
}

public enum ArticleStatus
{
    /// <summary>Being written.</summary>
    Draft = 0,

    /// <summary>Written and checked; can go on a brief.</summary>
    Ready = 1,

    /// <summary>Shown at a brief's meeting.</summary>
    Presented = 2,
}

public enum BriefStatus
{
    Draft = 0,
    Ready = 1,
    Presented = 2,
    Sent = 3,
}

/// <summary>The kinds of indicator an article can carry. Values are stored; append, never renumber.</summary>
public enum IndicatorType
{
    Account = 0,
    Host = 1,
    IpAddress = 2,
    Domain = 3,
    Url = 4,
    FileHash = 5,
    FileName = 6,
    EmailAddress = 7,
    Process = 8,
    RegistryKey = 9,
    Other = 10,
    CommandLine = 11,

    /// <summary>A cryptocurrency wallet address (a ransom or extortion payment address, say).</summary>
    CryptoWallet = 12,
}

/// <summary>Who a rendering of a brief is for. Stored on a send; append, never renumber.</summary>
public enum Edition
{
    /// <summary>The security teams: everything up to the team ceiling.</summary>
    Team = 0,

    /// <summary>The whole company (the email to everyone, the SharePoint copy): only what's at or under the public
    /// ceiling.</summary>
    Public = 1,
}

/// <summary>What became of one send of an edition.</summary>
public enum SendStatus
{
    /// <summary>Frozen and handed to the relay; the outcome isn't known yet (or the app stopped meanwhile).</summary>
    Sending = 0,

    /// <summary>The relay took it.</summary>
    Sent = 1,

    /// <summary>The relay didn't take it; <c>Error</c> says why. The brief isn't marked sent by it.</summary>
    Failed = 2,

    /// <summary>Sending failed in a way that doesn't say whether the relay took it (the connection dropped mid-send). Not
    /// published and doesn't mark the brief sent, but the next confirmation warns that it may have gone.</summary>
    Unknown = 3,
}

/// <summary>Which way a metric is good. It colours the arrow beside the change, and decides how thresholds apply.</summary>
public enum MetricDirection
{
    HigherIsBetter = 0,
    LowerIsBetter = 1,

    /// <summary>Neither: the arrow is grey, and thresholds apply as for <see cref="LowerIsBetter"/> (at or above).</summary>
    Neutral = 2,
}

/// <summary>How a metric's value is shown.</summary>
public enum MetricFormat
{
    /// <summary>A whole number ("42").</summary>
    Count = 0,

    /// <summary>A percentage ("97.5%").</summary>
    Percent = 1,

    /// <summary>A duration in hours ("3.5 h").</summary>
    Hours = 2,

    /// <summary>Any other number, to two decimal places at most.</summary>
    Number = 3,
}

/// <summary>The chart a metric group's slide in present mode draws under its numbers. Stored; append, never renumber.</summary>
public enum MetricChart
{
    /// <summary>A line per metric over the last 30 days (the default).</summary>
    Trend = 0,

    /// <summary>A donut of the current values, the total in the middle.</summary>
    Share = 1,

    /// <summary>Horizontal bars of the current values, side by side.</summary>
    Bars = 2,

    /// <summary>No chart: the numbers alone.</summary>
    None = 3,

    /// <summary>A small trend per metric over the last 30 days, each on its own scale.</summary>
    SmallMultiples = 4,

    /// <summary>A column per day over the last 30 days, split into each metric's part: the total and the mix.</summary>
    Stacked = 5,

    /// <summary>A bar per metric of today's value over its normal, warning and critical bands.</summary>
    Bullet = 6,
}

/// <summary>Where a metric's value came from. Stored; append, never renumber.</summary>
public enum MetricSource
{
    /// <summary>Typed by an analyst.</summary>
    Manual = 0,

    /// <summary>Read from a CSV file (the drop folder or an administrator's upload); <c>MetricValue.ImportId</c> says which.</summary>
    Imported = 1,
}

/// <summary>How a metrics CSV reached TIDE. Stored; append, never renumber.</summary>
public enum MetricImportOrigin
{
    /// <summary>Picked up from the drop folder (<c>Metrics:Import:DropFolderEnabled</c>).</summary>
    DropFolder = 0,

    /// <summary>Uploaded by an administrator.</summary>
    Upload = 1,
}

/// <summary>What kind of follow-up an action is. Stored; append, never renumber.</summary>
public enum ActionKind
{
    /// <summary>Search logs for an article's indicators (a quick, bounded hunt).</summary>
    IocSweep = 0,

    /// <summary>A hypothesis-led search for activity, beyond known indicators.</summary>
    ThreatHunt = 1,

    /// <summary>Block indicators at a control (proxy, firewall, EDR, mail gateway).</summary>
    Block = 2,

    /// <summary>Patch or otherwise remediate a vulnerability.</summary>
    PatchRemediate = 3,

    /// <summary>Change a configuration or a detection.</summary>
    ConfigChange = 4,

    /// <summary>Tell people: a user notice, a help-desk heads-up.</summary>
    Awareness = 5,

    /// <summary>Ask a vendor or partner a question.</summary>
    VendorInquiry = 6,

    Other = 7,
}

/// <summary>Where an action stands. Stored; append, never renumber.</summary>
public enum ActionStatus
{
    Open = 0,
    InProgress = 1,
    Blocked = 2,
    Done = 3,
    Cancelled = 4,
    NotApplicable = 5,
}

/// <summary>What an action found or achieved. Stored; append, never renumber.</summary>
public enum ActionOutcome
{
    None = 0,
    NoHits = 1,

    /// <summary>Hits found, and handed on (an incident case, the SOC).</summary>
    HitsEscalated = 2,
    Remediated = 3,
    Blocked = 4,
    RiskAccepted = 5,
    NotAffected = 6,
    Answered = 7,
    Other = 8,
}

/// <summary>A hunt query's result. Stored; append, never renumber.</summary>
public enum HuntResult
{
    NotRun = 0,
    NoHits = 1,
    Hits = 2,
}

/// <summary>
/// What a hunt query searches for: an indicator type, split further where a SIEM keeps the values in different
/// fields (IPv4 and IPv6; MD5, SHA-1, SHA-256 and SHA-512). Each has its own query template. Stored; append, never
/// renumber.
/// </summary>
public enum HuntValueKind
{
    Ipv4 = 0,
    Ipv6 = 1,
    Domain = 2,
    Url = 3,
    Md5 = 4,
    Sha1 = 5,
    Sha256 = 6,
    Sha512 = 7,

    /// <summary>A hash that's none of the lengths above.</summary>
    OtherHash = 8,
    FileName = 9,
    EmailAddress = 10,
    Host = 11,
    Account = 12,
    Process = 13,
    RegistryKey = 14,
    CommandLine = 15,
    Other = 16,
}

/// <summary>
/// Whether an article's own page may be fetched for drafting (the editor's "Fetch the article"). Stored; append, never
/// renumber.
/// </summary>
public enum ArticleFetch
{
    /// <summary>TIDE may fetch the page itself, through intake's guarded client. The default.</summary>
    TideOnly = 0,

    /// <summary>An open site: TIDE may fetch it, and when that fails the AI provider may look it up (TLP:CLEAR only,
    /// and only when an administrator has switched web grounding on).</summary>
    Open = 1,

    /// <summary>Never fetched (a portal behind a sign-in, or terms that forbid it): the analyst pastes the text.</summary>
    Never = 2,
}

/// <summary>How a source is read. Stored; append, never renumber.</summary>
public enum SourceKind
{
    /// <summary>An RSS 2.0, RSS 1.0 or Atom feed.</summary>
    Feed = 0,

    /// <summary>The CISA Known Exploited Vulnerabilities catalog (JSON): each newly listed CVE is an item.</summary>
    CisaKev = 1,

    /// <summary>Items another system proposes through the API (EP-75): never polled.</summary>
    Api = 2,
}

/// <summary>How far a source is trusted: the Admiralty (NATO) source-reliability scale.</summary>
public enum SourceReliability
{
    /// <summary>Completely reliable.</summary>
    A = 0,
    /// <summary>Usually reliable.</summary>
    B = 1,
    /// <summary>Fairly reliable.</summary>
    C = 2,
    /// <summary>Not usually reliable.</summary>
    D = 3,
    /// <summary>Unreliable.</summary>
    E = 4,
    /// <summary>Reliability can't be judged.</summary>
    F = 5,
}

/// <summary>Where a feed item stands in triage. Stored; append, never renumber.</summary>
public enum FeedItemStatus
{
    New = 0,
    Shortlisted = 1,
    Dismissed = 2,
    Duplicate = 3,
    Promoted = 4,
}

/// <summary>Where a threat story stands. Stored; append, never renumber.</summary>
public enum StoryStatus
{
    Active = 0,
    Monitoring = 1,
    Closed = 2,
}

/// <summary>What a rotation change does. Stored; append, never renumber.</summary>
public enum RotationChangeKind
{
    /// <summary>The partner takes the requester's days; nothing comes back.</summary>
    Cover = 0,

    /// <summary>The partner takes the requester's days, and the requester takes the partner's.</summary>
    Swap = 1,
}

/// <summary>Where a rotation change stands. Stored; append, never renumber.</summary>
public enum RotationChangeStatus
{
    /// <summary>Waiting for the partner to approve.</summary>
    Pending = 0,
    Approved = 1,
    Declined = 2,
    Cancelled = 3,
}

/// <summary>The colours an action card rule can give (drawn from the app's palette, legible in both themes). Stored; append, never renumber.</summary>
public enum CardColour
{
    Red = 0,
    Amber = 1,
    Green = 2,
    Blue = 3,
    Purple = 4,
    Teal = 5,
    Grey = 6,
}

/// <summary>A card rule's SLA condition. Stored; append, never renumber.</summary>
public enum CardRuleSla
{
    Any = 0,
    Overdue = 1,
    DueSoon = 2,
    OverdueOrDueSoon = 3,
}
