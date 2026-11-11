using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The side column of Today and of another day's brief, pinned as markup: what each role sees beside the brief (the
/// queue, the escalated list, the actions card, the edition check, the meeting, coming up, the articles not yet on a
/// brief) in each state of the brief, with no brief yet, a draft, ready, a live meeting, presented and sent. Also the
/// number of database commands each draw makes. The markup is normalised (Blazor's markers, ids, dates and times) and
/// compared with Golden/today-side.txt. To accept a deliberate change, run the tests with TIDE_UPDATE_GOLDEN=1 and
/// review the diff.
/// </summary>
public partial class TodaySideColumnTests(CountingApp app) : IClassFixture<CountingApp>
{
    private static readonly SignIn Analyst = new("dev:side-analyst", "Side Analyst", "Analyst");
    private static readonly SignIn TeamAnalyst = new("dev:robin", "Robin", "Analyst");   // on the rotation the sample data fills
    private static readonly SignIn Owner = new("dev:side-owner", "Side Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:side-viewer", "Side Viewer", "Viewer");
    private static readonly SignIn[] People = [Analyst, TeamAnalyst, Owner, Viewer];

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    // Another day's brief, a week back, so /briefs/{day} draws the page as a past day's (not Today's).
    private DateOnly Past => Enumerable.Range(0, 5).Aggregate(Today, (d, _) => WorkingDays.Weekdays.Previous(d));

    [Fact]
    public async Task TheSideColumn_IsTheSame_ForEveryRoleAndEveryStateOfTheBrief()
    {
        await Arrange();
        var meetings = app.Services.GetRequiredService<LiveMeetings>();
        var golden = new StringBuilder();
        var seen = new Dictionary<string, string>();

        async Task Capture(string state)
        {
            foreach (var who in People)
            {
                foreach (var (route, day, asToday) in new[] { ("today", Today, true), ("day", Past, false) })
                {
                    var key = $"{state} / {who.Roles[0]} {who.Name} / {route}";
                    app.Commands.Reset();
                    await using var r = await RenderAsync<BriefPage>(app, asToday ? "/" : $"/briefs/{day:yyyy-MM-dd}",
                        new() { ["Day"] = $"{day:yyyy-MM-dd}", ["AsToday"] = asToday }, who);
                    var reads = app.Commands.Count;
                    var side = Normalise(Side(await r.Html()), app.Services.GetRequiredService<OrgClock>().Zone);
                    golden.Append("=== ").Append(key).Append(" (").Append(reads).Append(" commands)\n");
                    if (seen.TryGetValue(side, out var same)) golden.Append("same as ").Append(same).Append('\n');
                    else
                    {
                        seen[side] = key;
                        golden.Append(side).Append('\n');
                    }
                }
            }
        }

        await SetState(BriefStatus.Draft);
        await Capture("draft");
        await SetState(BriefStatus.Ready);
        await Capture("ready");
        var seats = new List<IMeetingSeat>();
        foreach (var (day, id) in await BriefIds())
            seats.Add(meetings.Start(day, id, "dev:side-presenter", "Presenter", "c-side-" + day, Guid.NewGuid(), 0));
        try { await Capture("live meeting"); }
        finally { foreach (var seat in seats) meetings.End(seat); }
        await SetState(BriefStatus.Presented);
        await Capture("presented");
        await SetState(BriefStatus.Sent);
        await Capture("sent");
        await NoBriefs();
        await Capture("no brief yet");

        var actual = golden.ToString();
        if (Environment.GetEnvironmentVariable("TIDE_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", "today-side.txt"), actual);
            return;
        }
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "today-side.txt")).Replace("\r\n", "\n");
        actual.Should().Be(expected);
    }

    // A team with Robin and the owner on it, with an escalated overdue action nobody has, one of each of theirs; another
    // day's brief with one article; meeting notes and an attendee on both briefs.
    private async Task Arrange()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var teams = sp.GetRequiredService<TeamService>();
        var actions = sp.GetRequiredService<ActionService>();
        var team = await teams.AddAsync("Side Team", null, null);
        foreach (var who in new[] { TeamAnalyst, Owner }) await teams.AddMemberAsync(team, who.UserId, who.Name);
        var today = actions.Today;
        async Task<int> Add(string title, SignIn? to, DateOnly due, ActionKind kind) =>
            await actions.CreateAsync(new ActionForm
            {
                Title = $"Side: {title}", Kind = kind, TeamId = team, AssigneeId = to?.UserId, AssigneeName = to?.Name, DueDate = due,
            });
        var late = await Add("nobody's, overdue", null, today.AddDays(-3), ActionKind.IocSweep);
        await Add("the owner's", Owner, today.AddDays(20), ActionKind.Block);
        await Add("Robin's", TeamAnalyst, today.AddDays(20), ActionKind.ThreatHunt);
        await sp.GetRequiredService<BriefService>().GetOrStartAsync(Past);

        var db = sp.GetRequiredService<AppDbContext>();
        var escalated = await db.Actions.SingleAsync(a => a.Number == late);
        (escalated.EscalatedForDue, escalated.EscalatedAtUtc) = (escalated.DueDate, DateTimeOffset.UtcNow);
        var past = await db.Briefs.Include(b => b.Items).SingleAsync(b => b.BriefDate == Past);
        past.Intro = "A quiet week.";
        // The sample articles share a moment; a minute apart, the lists of them come in one order every run.
        var articles = await db.Articles.OrderBy(a => a.Title).ToListAsync();
        for (var i = 0; i < articles.Count; i++) articles[i].CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i);
        past.Items.Add(new BriefItem { ArticleId = (await db.Articles.OrderBy(a => a.Title).FirstAsync()).Id, SortOrder = 0 });
        await db.SaveChangesAsync();
        foreach (var (_, id) in await BriefIds())
        {
            db.MeetingAttendees.Add(new MeetingAttendee { BriefId = id, Name = "In the room" });
            var first = (await db.Briefs.Include(b => b.Items).SingleAsync(b => b.Id == id)).Items.OrderBy(i => i.SortOrder).First().ArticleId;
            db.MeetingNotes.Add(new MeetingNote { BriefId = id, ArticleId = first, AuthorId = "dev:sam", AuthorName = "Sam", AtUtc = DateTimeOffset.UtcNow, Text = "Patch Friday." });
            db.MeetingNotes.Add(new MeetingNote { BriefId = id, AuthorId = "dev:sam", AuthorName = "Sam", AtUtc = DateTimeOffset.UtcNow, Text = "Thanks all." });
        }
        await db.SaveChangesAsync();
    }

    private async Task<List<(DateOnly Day, Guid Id)>> BriefIds()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var days = new[] { Today, Past };
        return [.. (await db.Briefs.Where(b => days.Contains(b.BriefDate)).ToListAsync()).Select(b => (b.BriefDate, b.Id))];
    }

    private async Task SetState(BriefStatus status)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var days = new[] { Today, Past };
        foreach (var brief in await db.Briefs.Where(b => days.Contains(b.BriefDate)).ToListAsync())
        {
            brief.Status = status;
            var held = status is BriefStatus.Presented or BriefStatus.Sent ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            (brief.PresentedAtUtc, brief.MeetingHeldAtUtc) = (held, held);
        }
        await db.SaveChangesAsync();
    }

    // Both briefs moved to long-gone days: today and the other day have none.
    private async Task NoBriefs()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var days = new[] { Today, Past };
        foreach (var brief in await db.Briefs.Where(b => days.Contains(b.BriefDate)).ToListAsync())
            brief.BriefDate = brief.BriefDate.AddYears(-3);
        await db.SaveChangesAsync();
    }

    /// <summary>The side column (the second column of the page's grid), and the queue and escalated cards wherever
    /// they are drawn (an action owner's lead the page).</summary>
    internal static string Side(string html)
    {
        var parts = new List<string>();
        var grid = html.IndexOf("<div class=\"tide-today-grid\">", StringComparison.Ordinal);
        if (grid >= 0)
        {
            var first = Element(html, html.IndexOf("<div", grid + 1, StringComparison.Ordinal), "div");
            var secondAt = html.IndexOf("<div", first.End, StringComparison.Ordinal);
            parts.Add(html[secondAt..Element(html, secondAt, "div").End]);
            foreach (var id in new[] { "escalated-h", "team-queue-h" })
            {
                var label = html.IndexOf($"aria-labelledby=\"{id}\"", first.Start, StringComparison.Ordinal);
                if (label < 0 || label > first.End) continue;
                var at = html.LastIndexOf("<section", label, StringComparison.Ordinal);
                parts.Add(html[at..Element(html, at, "section").End]);
            }
        }
        return parts.Count == 0 ? "(no grid)" : string.Join("\n---\n", parts);
    }

    // The element of this tag opening at <paramref name="at"/>, to its matching close.
    private static (int Start, int End) Element(string html, int at, string tag)
    {
        var depth = 0;
        foreach (Match m in Regex.Matches(html[at..], $"<{tag}[\\s>]|</{tag}>"))
        {
            depth += m.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0) return (at, at + m.Index + m.Length);
        }
        throw new InvalidOperationException($"No close for the {tag} at {at}.");
    }

    /// <summary>The markup with what differs from run to run, or with Blazor's own bookkeeping, taken out.</summary>
    internal static string Normalise(string html, TimeZoneInfo zone)
    {
        html = BlazorMarker().Replace(html, "");
        // A run of white space draws as one space, however it is indented.
        html = Space().Replace(html, " ");
        html = html.Replace(zone.StandardName, "{zone}", StringComparison.Ordinal).Replace(zone.DaylightName, "{zone}", StringComparison.Ordinal);
        html = Id().Replace(html, "{id}");
        html = LongDay().Replace(html, "{day}");
        html = When().Replace(html, "{when}");
        html = ShortDay().Replace(html, "{shortday}");
        html = Time().Replace(html, "{time}");
        html = IsoDay().Replace(html, "{iso}");
        html = ActionNumber().Replace(html, "{action}");
        // One line per card, heading, row and list item, so a difference reads in a diff.
        return Break().Replace(html, "\n$0");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"<(section|h2|h3|li|dt|div class=""tide-queue-row)[\s>]")]
    private static partial Regex Break();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex BlazorMarker();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|(?<=hist-)[0-9a-f]{8}")]
    private static partial Regex Id();

    [GeneratedRegex("(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), (January|February|March|April|May|June|July|August|September|October|November|December) \\d{1,2}, \\d{4}")]
    private static partial Regex LongDay();

    [GeneratedRegex("(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \\d{1,2}, \\d{1,2}:\\d{2} (AM|PM)")]
    private static partial Regex When();

    [GeneratedRegex("(Mon|Tue|Wed|Thu|Fri|Sat|Sun) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \\d{1,2}(, \\d{4})?")]
    private static partial Regex ShortDay();

    [GeneratedRegex("\\d{1,2}:\\d{2} (AM|PM)")]
    private static partial Regex Time();

    [GeneratedRegex("\\d{4}-\\d{2}-\\d{2}")]
    private static partial Regex IsoDay();

    [GeneratedRegex("actions/\\d+|A-\\d+")]
    private static partial Regex ActionNumber();
}
