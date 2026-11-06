using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The team's opening note on the pages: its own slide in present mode, after the title and before the numbers, on the
/// team edition only and only when it isn't blank; a meeting's followers landing on it by its key; a colleague's edit
/// showing on the slides and the brief page as it lands. Its own app, since metrics are global.
/// </summary>
public class TeamNotePageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:note-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:note-robin", "Robin Analyst", "Analyst");

    private const string Note = "Phishing trends this week\n\n- Invoice lures are up\n  - Most spoof a payroll vendor\n- Patch window on Thursday";

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private static DateOnly Before(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    // One metric group with a value on every day, so a metrics slide follows the note's (made once).
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static bool _numbers;

    private async Task Numbers()
    {
        await Once.WaitAsync();
        try
        {
            if (_numbers) return;
            await using var scope = SignedIn(app, SignIn.Analyst);
            var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
            var g = await svc.AddGroupAsync("Note test numbers");
            await svc.SaveMetricAsync(new MetricForm { GroupId = g, Name = "Waiting", Direction = MetricDirection.LowerIsBetter });
            _numbers = true;
        }
        finally { Once.Release(); }
    }

    // A past day's brief with one article and the note given (set either way: a new brief starts with the one before's).
    private async Task<(DateOnly Day, Guid Brief)> Brief(int daysBack, string? note)
    {
        await Numbers();
        var day = Before(Today, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        await briefs.AddArticleAsync(brief.Id, await scope.ServiceProvider.GetRequiredService<ArticleService>()
            .CreateAsync($"Note test article {daysBack}", "Vendor", null, TlpLevel.Clear));
        await briefs.SetTeamNoteAsync(brief.Id, note);
        return (day, brief.Id);
    }

    private async Task SaveAs(SignIn who, Guid brief, string? note)
    {
        await using var scope = SignedIn(app, who);
        await scope.ServiceProvider.GetRequiredService<BriefService>().SetTeamNoteAsync(brief, note);
    }

    private static Task Edition(Rendered<Present> r, Edition edition) => r.Click(p =>
    {
        p.SetEdition(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = edition.ToString() });
        return p.OnPresentKey("render");
    });

    // The slides' titles in order, as the progress bar lists them.
    private static List<string> Titles(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, "<nav class=\"tide-progress\"[\\s\\S]*?</nav>").Single().Value
            .Split("title=\"").Skip(1).Select(t => System.Net.WebUtility.HtmlDecode(t[..t.IndexOf('"')])).ToList();

    [Fact]
    public async Task TheNote_IsTheSlideAfterTheTitle_BeforeTheNumbers_InLargeType_WithItsBulletsNested()
    {
        var (day, _) = await Brief(60, Note);
        await using var r = await Open(app, day);
        var titles = Titles(await r.Html());
        titles.Take(3).Should().Equal("Title", "Opening note", "Numbers: Note test numbers");
        titles.Should().ContainSingle(t => t == "Opening note");

        await r.Click(p => p.OnPresentKey("next"));
        var html = await r.Html();
        html.Should().Contain("class=\"tide-slide-note\"").And.Contain("<h2>Before we start</h2>").And.Contain("team edition only")
            .And.Contain("Slide 2 of").And.Contain(": Opening note");
        System.Text.RegularExpressions.Regex.IsMatch(html, @"<li>Invoice lures are up\s*<ul>\s*<li>Most spoof a payroll vendor</li>\s*</ul>\s*</li>")
            .Should().BeTrue("the sub-bullet is a list inside its bullet");

        await r.Click(p => p.OnPresentKey("next"));
        (await r.Html()).Should().Contain("<h2>Note test numbers</h2>", "the numbers follow the note");
    }

    [Fact]
    public async Task ABlankNote_HasNoSlide()
    {
        var (day, _) = await Brief(62, "  ");
        await using var r = await Open(app, day);
        var html = await r.Html();
        Titles(html).Should().NotContain("Opening note").And.HaveElementAt(1, "Numbers: Note test numbers");
        html.Should().NotContain("tide-slide-note");
    }

    [Fact]
    public async Task TheCompanyWideEdition_HasNoNoteSlide_AndSwitchingEditionsKeepsTheSlideShown()
    {
        var (day, _) = await Brief(64, Note);
        await using var r = await Open(app, day);
        await r.Click(p => p.OnPresentKey("next"));   // the note's slide

        await Edition(r, Domain.Enums.Edition.Public);
        var html = await r.Html();
        Titles(html).Should().NotContain("Opening note").And.NotContain(t => t.StartsWith("Numbers"));
        html.Should().Contain("Slide 1 of", "the note's slide gave way to the title").And.NotContain("Invoice lures").And.NotContain("payroll vendor");

        // On the article's slide, the edition switched either way stays on that article.
        await r.Click(p => p.OnPresentKey("next"));
        (await r.Html()).Should().Contain("<h2>Note test article 64</h2>");
        await Edition(r, Domain.Enums.Edition.Team);
        html = await r.Html();
        html.Should().Contain("<h2>Note test article 64</h2>").And.Contain("Opening note");
        await r.Click(p => p.OnPresentKey("first"));
        await r.Click(p => p.OnPresentKey("next"));
        (await r.Html()).Should().Contain("Invoice lures are up", "the note's slide is back on the team edition");
    }

    [Fact]
    public async Task SwitchingToTheCompanyWideEdition_OnTheNumbers_ShowsTheSlideInTheirPlace_NotTheOneWithTheirNumber()
    {
        var (day, _) = await Brief(65, Note);
        await using var r = await Open(app, day);
        await r.Click(p => p.OnPresentKey("next"));
        await r.Click(p => p.OnPresentKey("next"));
        (await r.Html()).Should().Contain("<h2>Note test numbers</h2>");

        await Edition(r, Domain.Enums.Edition.Public);
        (await r.Html()).Should().Contain("Slide 2 of 3").And.Contain("<h2>Note test article 65</h2>", "the first article, where the numbers were, not the wrap-up at slide 3");
    }

    [Fact]
    public async Task AFollower_LandsOnTheNote_ByItsKey()
    {
        var (day, _) = await Brief(66, Note);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        try
        {
            await pat.Click(p => p.OnPresentKey("next"));
            Meetings.Get(day)!.Should().Match<LiveMeetingView>(v => v.Slide == 1 && v.SlideKey == Present.NoteKey);

            await using var owner = await Open(app, day, Owner);
            await Do(owner, p => p.JoinIfLive());
            (await owner.Html()).Should().Contain("<h2>Before we start</h2>").And.Contain("Invoice lures are up").And.Contain("Following Tester");

            await pat.Click(p => p.OnPresentKey("next"));
            Meetings.Get(day)!.SlideKey.Should().StartWith(MetricSlides.KeyPrefix, "the numbers come after the note");
            await Do(owner, p => p.Follow());
            (await owner.Html()).Should().Contain("<h2>Note test numbers</h2>");
        }
        finally { await Do(pat, p => p.EndMeeting()); }
    }

    [Fact]
    public async Task AColleaguesEdit_ShowsOnTheSlide_AndClearingTheNoteTakesTheSlideAway()
    {
        var (day, brief) = await Brief(68, Note);
        await using var r = await Open(app, day);
        await r.Click(p => p.OnPresentKey("next"));

        await SaveAs(Robin, brief, "- Payroll lure wave\n  - Report it with the button");
        (await r.HtmlOnce("Payroll lure wave")).Should().Contain("Report it with the button").And.Contain("<h2>Before we start</h2>")
            .And.NotContain("Invoice lures");

        await SaveAs(Robin, brief, null);
        string html = "";
        for (var i = 0; i < 50 && (html = await r.Html()).Contains("tide-slide-note"); i++) await Task.Delay(100);
        html.Should().NotContain("tide-slide-note").And.Contain("Slide 1 of", "the slide shown went away: the title, where it was");
        Titles(html).Should().NotContain("Opening note");
    }

    [Fact]
    public async Task TheBriefPage_EditsTheNote_ForAnalysts_ShowsItToOthers_AndHearsAColleaguesEdit()
    {
        var (day, brief) = await Brief(70, Note);
        var path = $"/briefs/{day:yyyy-MM-dd}";
        await using var page = await RenderAsync<BriefPage>(app, path, new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        var html = await page.Html();
        html.Should().Contain("id=\"team-note-h\"").And.Contain("id=\"team-note\"").And.Contain("Invoice lures are up").And.Contain("Save note");

        await using (var owner = await RenderAsync<BriefPage>(app, path, new() { ["Day"] = $"{day:yyyy-MM-dd}" }, Owner))
        {
            var theirs = await owner.Html();
            theirs.Should().Contain("id=\"team-note-h\"").And.NotContain("id=\"team-note\"", "an action owner reads it, and can't edit it");
            System.Text.RegularExpressions.Regex.IsMatch(theirs, @"<li>Invoice lures are up\s*<ul>\s*<li>Most spoof a payroll vendor</li>").Should().BeTrue();
        }

        await SaveAs(Robin, brief, "- Tabletop exercise on Friday");
        (await page.HtmlOnce("Tabletop exercise on Friday")).Should().Contain("Tabletop exercise on Friday").And.NotContain("Invoice lures", "the page read the note again");
    }
}
