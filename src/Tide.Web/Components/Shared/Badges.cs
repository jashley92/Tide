using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Tide.Application.Content;
using Tide.Domain.Enums;

namespace Tide.Web.Components.Shared;

/// <summary>What a badge's colour says. A badge always has its words too, so the tone only backs them up.</summary>
public enum BadgeTone { Neutral, Info, Ok, Warn, Danger, Purple }

/// <summary>A badge, the one shape every pill in the app takes (TLP markings aside): a tone, tinted by default or
/// <see cref="Solid"/> for what wants doing now. The badges below are this with their meaning built in; use it
/// directly for a one-off label ("Retired").</summary>
public sealed class Badge : ComponentBase
{
    [Parameter] public BadgeTone Tone { get; set; }
    [Parameter] public bool Solid { get; set; }

    /// <summary>A Bootstrap icon ("bi-lock-fill") before the words.</summary>
    [Parameter] public string? Icon { get; set; }
    [Parameter] public string? Title { get; set; }

    /// <summary>Layout classes for where it sits ("ms-auto"), or <c>tide-badge-wrap</c> to let long words wrap.</summary>
    [Parameter] public string? Class { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b) => Write(b, Tone, Solid, null, Icon, Title, Class, ChildContent);

    /// <summary>The classes for a badge: <c>tide-badge tide-badge-{tone}</c>, and <c>tide-badge-solid</c> when filled.</summary>
    public static string Css(BadgeTone tone, bool solid = false, string? extra = null) =>
        "tide-badge " + ToneClass(tone) + (solid ? " tide-badge-solid" : "")
        + (string.IsNullOrWhiteSpace(extra) ? "" : " " + extra);

    /// <summary>The tone alone (<c>tide-badge-ok</c>): its colours as <c>--tide-badge-fg</c> and <c>--tide-badge-bg</c>, for
    /// something tinted like a badge that isn't one (a lineage node, a timeline bar).</summary>
    public static string ToneClass(BadgeTone tone) => "tide-badge-" + tone.ToString().ToLowerInvariant();

    /// <summary>Writes a badge into another component's tree. A caller writing more than one puts each in a region of
    /// its own (<see cref="RenderTreeBuilder.OpenRegion"/>), since they share these sequence numbers.</summary>
    internal static void Write(RenderTreeBuilder b, BadgeTone tone, bool solid, string? text,
        string? icon = null, string? title = null, string? extra = null, RenderFragment? content = null)
    {
        b.OpenElement(0, "span");
        b.AddAttribute(1, "class", Css(tone, solid, extra));
        if (title is not null) b.AddAttribute(2, "title", title);
        if (icon is not null)
        {
            b.OpenElement(3, "span");
            b.AddAttribute(4, "class", "bi " + icon);
            b.AddAttribute(5, "aria-hidden", "true");
            b.CloseElement();
        }
        b.AddContent(6, text);
        b.AddContent(7, content);
        b.CloseElement();
    }
}

/// <summary>A TLP marking in its standard colour on black ("TLP:AMBER").</summary>
public sealed class TlpBadge : ComponentBase
{
    [Parameter, EditorRequired] public TlpLevel Level { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "span");
        b.AddAttribute(1, "class", "tide-tlp tide-tlp-" + Level.ToString().ToLowerInvariant());
        b.AddAttribute(2, "title", Tlp.Meaning(Level));
        b.AddContent(3, Tlp.Label(Level));
        b.CloseElement();
    }
}

/// <summary>Threat or Informational, with the priority beside a threat.</summary>
public sealed class ClassBadge : ComponentBase
{
    [Parameter, EditorRequired] public Classification Classification { get; set; }
    [Parameter] public Priority? Priority { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        var threat = Classification == Classification.Threat;
        b.OpenRegion(0);
        Badge.Write(b, threat ? BadgeTone.Danger : BadgeTone.Info, false,
            threat ? "Threat" : "Informational", threat ? "bi-exclamation-triangle-fill" : "bi-info-circle");
        b.CloseRegion();
        if (threat && Priority is { } p)
        {
            b.AddContent(1, " ");
            b.OpenRegion(2);
            PriorityBadge.Write(b, p);
            b.CloseRegion();
        }
    }
}

/// <summary>A threat's priority, P1 to P4: filled for P1, which wants doing now, red for P2 and amber below.</summary>
public sealed class PriorityBadge : ComponentBase
{
    [Parameter, EditorRequired] public Priority Priority { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b) => Write(b, Priority);

    internal static void Write(RenderTreeBuilder b, Priority p) =>
        Badge.Write(b, p is Priority.P1 or Priority.P2 ? BadgeTone.Danger : BadgeTone.Warn, p == Priority.P1,
            p.ToString(), title: "Priority " + p);
}

/// <summary>An article's or brief's status as a quiet pill.</summary>
public sealed class StatusBadge : ComponentBase
{
    [Parameter, EditorRequired] public string Status { get; set; } = "";

    protected override void BuildRenderTree(RenderTreeBuilder b) => Badge.Write(b, ToneOf(Status), false, Status);

    /// <summary>Ready (to go on a brief) is the good news; presented, sent and distributed are done with; a SharePoint copy
    /// still to post wants doing; a failed send is trouble.</summary>
    public static BadgeTone ToneOf(string status) => status.ToLowerInvariant() switch
    {
        "ready" => BadgeTone.Ok,
        "presented" or "sent" or "distributed" => BadgeTone.Info,
        "sharepoint to post" => BadgeTone.Warn,
        "failed" => BadgeTone.Danger,
        _ => BadgeTone.Neutral,
    };
}

/// <summary>Analyst-written Markdown, rendered safe (see <see cref="Application.Content.IMarkdownService"/>). A link
/// from the server's root (<c>[x](/actions/12)</c>) is given the app's sub-path, so it reaches TIDE under <c>/tide</c>.</summary>
public sealed class Md : ComponentBase
{
    [Inject] private Application.Content.IMarkdownService Markdown { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Parameter] public string? Text { get; set; }
    [Parameter] public bool Inline { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        var links = LinkRoot.FromBaseUri(Nav.BaseUri);
        b.OpenElement(0, Inline ? "span" : "div");
        b.AddAttribute(1, "class", "tide-md");
        b.AddMarkupContent(2, Inline ? Markdown.ToInlineHtml(Text, links) : Markdown.ToHtml(Text, links));
        b.CloseElement();
    }
}

/// <summary>An action's standing against its due date: overdue, due soon, on track, met or missed.</summary>
public sealed class SlaBadge : ComponentBase
{
    [Parameter, EditorRequired] public Application.Actions.SlaState State { get; set; }
    [Parameter] public DateOnly? Due { get; set; }

    /// <summary>Today, for how late an overdue action is ("Overdue · 3 days"); without it, just "Overdue".</summary>
    [Parameter] public DateOnly? Today { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        var text = State switch
        {
            Application.Actions.SlaState.Overdue => "Overdue" + Application.Actions.ActionSla.Late(Due, Today) switch
            {
                > 0 and var days => $" · {days} day{(days == 1 ? "" : "s")}",
                _ => "",
            },
            Application.Actions.SlaState.DueSoon => "Due soon",
            Application.Actions.SlaState.OnTrack => "On track",
            Application.Actions.SlaState.Met => "Met",
            Application.Actions.SlaState.Missed => "Missed",
            _ => "No due date",
        };
        // The states that need attention carry an icon as well as a colour.
        var icon = State switch
        {
            Application.Actions.SlaState.Overdue or Application.Actions.SlaState.Missed => "bi-exclamation-triangle-fill",
            Application.Actions.SlaState.DueSoon => "bi-clock-history",
            _ => null,
        };
        // Overdue is the one to act on now, so it's filled; a missed date is history, so it's only tinted.
        var tone = State switch
        {
            Application.Actions.SlaState.Overdue or Application.Actions.SlaState.Missed => BadgeTone.Danger,
            Application.Actions.SlaState.DueSoon => BadgeTone.Warn,
            Application.Actions.SlaState.OnTrack or Application.Actions.SlaState.Met => BadgeTone.Ok,
            _ => BadgeTone.Neutral,
        };
        Badge.Write(b, tone, State == Application.Actions.SlaState.Overdue, text, icon,
            Due is { } d ? "Due " + Ui.Day(d) : null);
    }
}

/// <summary>
/// A lineage part's state in the tones the SLA badge uses: on track is its green, due soon its amber and overdue its red;
/// closed is the blue of things done with (as <see cref="StatusBadge"/> has it), and tracking the Tracking status's purple.
/// </summary>
public static class LineageTones
{
    public static BadgeTone Of(Application.Actions.LineageState s) => s switch
    {
        Application.Actions.LineageState.Done => BadgeTone.Info,
        Application.Actions.LineageState.DueSoon => BadgeTone.Warn,
        Application.Actions.LineageState.Overdue => BadgeTone.Danger,
        Application.Actions.LineageState.Tracking => BadgeTone.Purple,
        _ => BadgeTone.Ok,
    };
}

/// <summary>"Escalated", filled red, on an escalated action; nothing otherwise. Its title says when.</summary>
public sealed class EscalatedBadge : ComponentBase
{
    [Inject] private Application.Org.OrgClock Clock { get; set; } = default!;

    /// <summary>When it was escalated, while it still is (<see cref="Domain.Entities.ActionItem.EscalatedSince"/>,
    /// <c>ActionRow.EscalatedSince</c>); null draws nothing.</summary>
    [Parameter] public DateTimeOffset? At { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (At is not { } at) return;
        Badge.Write(b, BadgeTone.Danger, true, "Escalated", "bi-arrow-up-circle-fill", "Escalated " + Ui.When(Clock, at));
    }
}

/// <summary>An action's status as a pill.</summary>
public sealed class ActionStatusBadge : ComponentBase
{
    [Parameter, EditorRequired] public ActionStatus Status { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b) =>
        Badge.Write(b, Status switch
        {
            ActionStatus.InProgress => BadgeTone.Info,
            ActionStatus.Blocked => BadgeTone.Warn,
            ActionStatus.Tracking => BadgeTone.Purple,
            ActionStatus.Done => BadgeTone.Ok,
            _ => BadgeTone.Neutral,
        }, false, Application.Actions.ActionService.StatusName(Status));
}

/// <summary>How an action's hunts went, or one hunt did: hits, no hits, or not run yet. Nothing when it has no hunt.
/// <see cref="OfOneHunt"/> drops the word "Hunt" (it sits on the hunt itself) and gives the hit count when known.</summary>
public sealed class HuntBadge : ComponentBase
{
    [Parameter] public HuntResult? Result { get; set; }
    [Parameter] public bool OfOneHunt { get; set; }
    [Parameter] public int? Count { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Result is not { } r) return;
        var text = (r, OfOneHunt) switch
        {
            (HuntResult.Hits, true) => Count is { } c ? "Hits: " + c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Hits",
            (HuntResult.Hits, false) => "Hunt: hits",
            (HuntResult.NoHits, true) => "No hits",
            (HuntResult.NoHits, false) => "Hunt: no hits",
            (_, true) => "Not run yet",
            _ => "Hunt not run",
        };
        Badge.Write(b, r switch
        {
            HuntResult.Hits => BadgeTone.Danger,
            HuntResult.NoHits => BadgeTone.Ok,
            _ => BadgeTone.Neutral,
        }, false, text);
    }
}

/// <summary>Display helpers shared by pages.</summary>
public static class Ui
{
    /// <summary>"1 tag", "3 tags" (<see cref="Words.Plural"/>).</summary>
    public static string Plural(int n, string one, string? many = null) => Words.Plural(n, one, many);

    /// <summary>How many avatar colours there are: the <c>--tide-avatar-1</c> to <c>-8</c> tokens in app.css.</summary>
    public const int AvatarSwatches = 8;

    /// <summary>
    /// A person's own colour for their presence avatar (1 to <see cref="AvatarSwatches"/>, the <c>tide-avatar-N</c>
    /// class), the same every time and for everyone: the swatch for the eighth of the colour wheel their
    /// <see cref="AvatarHue"/> falls in, so each keeps roughly the hue they had.
    /// </summary>
    public static int AvatarSwatch(string? seed) => AvatarHue(seed) * AvatarSwatches / 360 + 1;

    /// <summary>A person's own hue (0-359), from their id: <see cref="AvatarSwatch"/> picks by it.</summary>
    public static int AvatarHue(string? seed)
    {
        if (string.IsNullOrEmpty(seed)) return 210;
        unchecked
        {
            var h = 17;
            foreach (var ch in seed) h = h * 31 + ch;
            return ((h % 360) + 360) % 360;
        }
    }

    /// <summary>A day as an address or key holds it ("2026-10-30"): always the Gregorian calendar, whatever the culture,
    /// so a link built under (say) th-TH still names the right day.</summary>
    public static string IsoDay(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string BriefUrl(DateOnly d) => "briefs/" + IsoDay(d);

    public static string PresentUrl(DateOnly d) => "present/" + IsoDay(d);

    public static string ImageUrl(Guid fileId) => $"files/{fileId}/inline";

    public static string DownloadUrl(Guid fileId) => $"files/{fileId}/download";

    /// <summary>The file inside a zip with the password "infected".</summary>
    public static string ZipUrl(Guid fileId) => $"files/{fileId}/zip";

    /// <summary>A SHA-256 as the page shows it at a glance: its first 12 characters and an ellipsis.</summary>
    public static string ShortHash(string sha256) => sha256.Length <= 12 ? sha256 : sha256[..12] + "…";

    /// <summary>A file's size as people read it ("840 KB", "3.2 MB").</summary>
    public static string Size(long bytes) => Application.Files.FileSizes.Text(bytes);

    /// <summary>A card rule's colour as a CSS class suffix (tide-rule-red).</summary>
    public static string RuleClass(CardColour c) => "tide-rule-" + c.ToString().ToLowerInvariant();

    public static string ActionUrl(int number) => $"actions/{number}";

    /// <summary>An action's address with a query (<c>do=recordupdate</c>), or its plain address for none.</summary>
    public static string ActionUrl(int number, string? query) => string.IsNullOrEmpty(query) ? ActionUrl(number) : $"{ActionUrl(number)}?{query}";

    public static string ActionLabel(int number) => $"A-{number}";

    public static string Day(DateOnly d) => d.ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Text cut to <paramref name="max"/> characters with an ellipsis (a breadcrumb's title).</summary>
    public static string Clip(string? text, int max = 48) => text is null ? "" : text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    public static string ShortDay(DateOnly d) => d.ToString("ddd MMM d", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A UTC instant in the organization's zone, e.g. "Oct 24, 9:41 AM".</summary>
    public static string When(Application.Org.OrgClock clock, DateTimeOffset utc) => clock.ToLocal(utc).ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Who saved a team library's entry (a saved query, an outreach template) and when, and who last changed it: "Saved by
    /// Robin, Oct 24, 9:41 AM · changed by Sam, Oct 25, 2:10 PM". The names are the ones kept with it, as they were then.
    /// </summary>
    public static string Provenance(Application.Org.OrgClock clock, Domain.Common.AuditableEntity e, string createdByName, string? modifiedByName) =>
        $"Saved by {createdByName}, {When(clock, e.CreatedAtUtc)}"
        + (e.ModifiedAtUtc is { } changed ? $" · changed by {modifiedByName ?? "someone"}, {When(clock, changed)}" : "");

    /// <summary>A state ARIA reads as a word (aria-pressed, aria-expanded): "true" or "false". Bound to a bool, Blazor would
    /// write a bare attribute when on and drop it when off, so a toggle stops being one.</summary>
    public static string Aria(bool on) => on ? "true" : "false";

    /// <summary>The time of day only, for something on the day shown (a meeting note).</summary>
    public static string Time(Application.Org.OrgClock clock, DateTimeOffset utc) => clock.ToLocal(utc).ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseDay(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out day);

    /// <summary>A day as a date box and the address hold it ("2026-10-01"); null for none.</summary>
    public static string? IsoDay(DateOnly? d) => d is { } day ? IsoDay(day) : null;

    /// <summary>A day from a date box or the address; null for none or anything else.</summary>
    public static DateOnly? ParseDay(string? text) => TryParseDay(text, out var d) ? d : null;

    public static string IndicatorLabel(IndicatorType t) => Application.Indicators.IndicatorLabels.For(t);

    /// <summary>The indicator types an analyst picks from (the rest are for later imports).</summary>
    public static readonly IndicatorType[] PickableIndicatorTypes =
    [
        IndicatorType.IpAddress, IndicatorType.Domain, IndicatorType.Url, IndicatorType.FileHash, IndicatorType.EmailAddress,
        IndicatorType.FileName, IndicatorType.Host, IndicatorType.Account, IndicatorType.Process, IndicatorType.RegistryKey,
        IndicatorType.CommandLine, IndicatorType.CryptoWallet, IndicatorType.Other,
    ];

    public static readonly TlpLevel[] TlpLevels = Enum.GetValues<TlpLevel>();

    /// <summary>A tactic's Bootstrap icon.</summary>
    public static string TacticGlyph(MitreTactic t) => t switch
    {
        MitreTactic.Reconnaissance => "bi-binoculars",
        MitreTactic.ResourceDevelopment => "bi-tools",
        MitreTactic.InitialAccess => "bi-door-open",
        MitreTactic.Execution => "bi-terminal",
        MitreTactic.Persistence => "bi-pin-angle",
        MitreTactic.PrivilegeEscalation => "bi-arrow-up-circle",
        MitreTactic.Stealth => "bi-incognito",
        MitreTactic.DefenseImpairment => "bi-shield-slash",
        MitreTactic.CredentialAccess => "bi-key",
        MitreTactic.Discovery => "bi-search",
        MitreTactic.LateralMovement => "bi-arrows-move",
        MitreTactic.Collection => "bi-collection",
        MitreTactic.CommandAndControl => "bi-broadcast-pin",
        MitreTactic.Exfiltration => "bi-box-arrow-up",
        MitreTactic.Impact => "bi-exclamation-octagon",
        _ => "bi-question-circle"
    };

    /// <summary>A tactic's accent colour, a palette laid out along the kill chain (cool early, red at impact).
    /// The picker tints its column headers with it, so it reads in both themes.</summary>
    public static string TacticColor(MitreTactic t) => t switch
    {
        MitreTactic.Reconnaissance => "#6c757d",
        MitreTactic.ResourceDevelopment => "#6f42c1",
        MitreTactic.InitialAccess => "#0d6efd",
        MitreTactic.Execution => "#0dcaf0",
        MitreTactic.Persistence => "#20c997",
        MitreTactic.PrivilegeEscalation => "#198754",
        MitreTactic.Stealth => "#84cc16",
        MitreTactic.DefenseImpairment => "#65a30d",
        MitreTactic.CredentialAccess => "#ffc107",
        MitreTactic.Discovery => "#fd7e14",
        MitreTactic.LateralMovement => "#f97316",
        MitreTactic.Collection => "#e8590c",
        MitreTactic.CommandAndControl => "#d63384",
        MitreTactic.Exfiltration => "#dc3545",
        MitreTactic.Impact => "#b02a37",
        _ => "#6c757d"
    };
}

/// <summary>A tab in a <see cref="TabBar"/>: a link, current by its address, or by <see cref="Key"/> when it stands for
/// several pages. <see cref="Attention"/> is a short mark beside it ("new": a newer release is out). <see cref="Ticked"/>:
/// its count just changed live (a colleague's work), and ticks to say so. <see cref="Warning"/>: the count is of something
/// wrong, not of items ("2" sources failing), so it's drawn as a warning and this says what it counts to a screen reader
/// ("failing"). <see cref="Apart"/>: the tab is of another kind than those before it, and stands apart from them.</summary>
public sealed record TabBarItem(string Label, string Href, string? Icon = null, string? Key = null, int? Count = null, string? Attention = null,
    bool Ticked = false, string? Warning = null, bool Apart = false);

public enum TabLevel { Page, Sub }

/// <summary>One step of a breadcrumb trail (<see cref="Breadcrumbs"/>); the last is the current page and needs no link.</summary>
public sealed record Crumb(string Label, string? Href = null);

/// <summary>A health check's standing (Diagnostics), with an icon as well as a colour.</summary>
public sealed class CheckBadge : ComponentBase
{
    [Parameter, EditorRequired] public Services.CheckLevel Level { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        var (text, icon, tone) = Level switch
        {
            Services.CheckLevel.Ok => ("OK", "bi-check-circle-fill", BadgeTone.Ok),
            Services.CheckLevel.Info => ("Note", "bi-info-circle", BadgeTone.Info),
            Services.CheckLevel.Attention => ("See to it", "bi-exclamation-circle", BadgeTone.Warn),
            _ => ("Problem", "bi-x-octagon-fill", BadgeTone.Danger),
        };
        Badge.Write(b, tone, false, text, icon);
    }
}

/// <summary>Where a setting's value came from (Setting sources): the app, the installer's file, the environment, or
/// another provider.</summary>
public sealed class SettingSourceBadge : ComponentBase
{
    [Parameter, EditorRequired] public string Source { get; set; } = "";

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        var tone = Source == Services.SystemSettings.SavedInApp ? BadgeTone.Info
            : Source.EndsWith("(installer)", StringComparison.Ordinal) ? BadgeTone.Purple
            : Source == "Environment variable" ? BadgeTone.Warn
            : BadgeTone.Neutral;
        Badge.Write(b, tone, false, Source);
    }
}
