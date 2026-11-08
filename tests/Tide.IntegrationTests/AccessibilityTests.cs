using System.Globalization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The semantics a keyboard or screen reader depends on: what a tab bar is (links, tabs or toggles), the triage rows'
/// one tab stop, a heading for every page to land on, and avatar colours white initials can be read on.
/// </summary>
public class AccessibilityTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private static readonly TabBarItem[] Items = [new("One", "", Key: "1"), new("Two", "", Key: "2", Count: 4)];

    private static EventCallback<string?> Ignore => EventCallback.Factory.Create<string?>(new object(), (string? _) => { });

    [Fact]
    public async Task ATabBarWithAPanel_IsATablist_WithTheCurrentTabTheOneTabStop()
    {
        var html = await Render<TabBar>(new()
        {
            ["Items"] = Items, ["Label"] = "Brief", ["Active"] = "2", ["PanelId"] = "p", ["OnSelect"] = Ignore,
        });

        html.Should().Contain("role=\"tablist\" aria-label=\"Brief\"").And.NotContain("<nav").And.NotContain("aria-pressed");
        html.Should().Contain("role=\"tab\" id=\"p-tab-1\" class=\"tide-tabbar-item \" aria-selected=\"false\" aria-controls=\"p\" tabindex=\"-1\"");
        html.Should().Contain("role=\"tab\" id=\"p-tab-2\" class=\"tide-tabbar-item active\" aria-selected=\"true\" aria-controls=\"p\" tabindex=\"0\"");
        html.Should().Contain("<span class=\"tide-tabbar-count\">4</span>");
        TabBar.TabId("p", "2").Should().Be("p-tab-2");
    }

    [Fact]
    public async Task ATabBarThatFiltersAList_IsAGroupOfToggleButtons()
    {
        var html = await Render<TabBar>(new() { ["Items"] = Items, ["Label"] = "Inbox", ["Active"] = "1", ["OnSelect"] = Ignore });

        html.Should().Contain("role=\"group\" aria-label=\"Inbox\"").And.NotContain("role=\"tab");
        html.Should().Contain("class=\"tide-tabbar-item active\" aria-pressed=\"true\"")
            .And.Contain("class=\"tide-tabbar-item \" aria-pressed=\"false\"");
    }

    [Fact]
    public async Task ATabBarOfLinks_IsANav_WithTheCurrentPageMarked()
    {
        var html = await Render<TabBar>(new()
        {
            ["Items"] = (TabBarItem[])[new("One", "one", Key: "1"), new("Two", "two", Key: "2")], ["Label"] = "Administration", ["Active"] = "2",
        });

        html.Should().StartWith("<nav class=\"tide-tabbar tide-tabbar-page\" aria-label=\"Administration\">")
            .And.Contain("href=\"two\" aria-current=\"page\"").And.NotContain("role=\"tab");
    }

    [Fact]
    public async Task ThePages_UseTheRightKindOfTabBar()
    {
        var client = app.CreateClient();

        // Query languages switch the templates shown in place: tabs, and the panel they switch.
        var queries = await client.GetStringAsync("/admin/queries");
        queries.Should().Contain("role=\"tablist\" aria-label=\"Query language\"")
            .And.Contain("role=\"tabpanel\" id=\"query-panel\" aria-labelledby=\"query-panel-tab-");

        // The Actions views and Triage's inboxes only choose what the list shows: toggles.
        (await client.GetStringAsync("/actions")).Should().Contain("role=\"group\" aria-label=\"Views\"").And.Contain("aria-pressed=\"true\"");
        (await client.GetStringAsync("/triage")).Should().Contain("role=\"group\" aria-label=\"Inbox\"").And.Contain("aria-pressed=\"true\"");

        // The brief's running order and its preview of both editions switch one panel in place: tabs. The preview's
        // Team | Company-wide switch for a narrow screen only picks which edition shows: toggles.
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        (await client.GetStringAsync($"/briefs/{day:yyyy-MM-dd}?tab=preview")).Should().Contain("role=\"tablist\" aria-label=\"Brief\"")
            .And.Contain("role=\"tabpanel\" id=\"brief-panel\" aria-labelledby=\"brief-panel-tab-1\"")
            .And.Contain("role=\"group\" aria-label=\"Edition to show\"").And.Contain("aria-pressed=\"true\"");

        // A Trends report is its own address: links.
        (await client.GetStringAsync("/trends")).Should().Contain("<nav class=\"tide-tabbar tide-tabbar-page\" aria-label=\"Report\">")
            .And.Contain("aria-current=\"page\"");
    }

    [Fact]
    public async Task TriageRows_AreOneTabStop_TheSelectedRow()
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var source = new Source { Name = "Roving", Url = "https://roving.example/feed" };
            db.Add(source);
            var now = DateTimeOffset.UtcNow.AddMinutes(5);   // newest, so these two lead the inbox
            FeedItem Item(string t, DateTimeOffset at) => new()
            {
                Source = source, Title = t, Url = "https://roving.example/" + t, UrlHash = "roving-" + t, PublishedAtUtc = at, FetchedAtUtc = at, Status = FeedItemStatus.New,
            };
            db.AddRange(Item("roving-first", now), Item("roving-second", now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        var html = await app.CreateClient().GetStringAsync("/triage");

        RowTag(html, "roving-first").Should().Contain("selected").And.Contain("aria-current=\"true\"").And.Contain("tabindex=\"0\"");
        RowTag(html, "roving-second").Should().Contain("tabindex=\"-1\"").And.NotContain("aria-current");
        html.Split("tabindex=\"0\"").Length.Should().Be(2, "only the selected row is in the tab order");
    }

    // The <li> that opens the row with this title (its aria-label).
    private static string RowTag(string html, string title)
    {
        var label = html.IndexOf($"aria-label=\"{title}\"", StringComparison.Ordinal);
        label.Should().BePositive();
        var start = html.LastIndexOf("<li", label, StringComparison.Ordinal);
        return html[start..html.IndexOf('>', label)];
    }

    [Theory]
    [InlineData("/admin", "Overview")]
    [InlineData("/admin/teams", "Teams &amp; SLA")]
    [InlineData("/admin/queries", "Query templates")]
    public async Task AnAdminPage_ItsTitleIsTheOneH1(string path, string title)
    {
        var html = await app.CreateClient().GetStringAsync(path);

        html.Should().Contain($"<h1 class=\"tide-admin-title\">{title}</h1>")
            .And.Contain("<p class=\"h1 tide-admin-name\">Administration</p>");
        html.Split("<h1").Length.Should().Be(2);
    }

    [Fact]
    public async Task Present_HasAHiddenH1_ForFocusToLandOn()
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

        var html = await app.CreateClient().GetStringAsync($"/present/{day:yyyy-MM-dd}");

        html.Should().Contain($"<h1 class=\"visually-hidden\">Presenting the brief for {Ui.ShortDay(day)}</h1>");
    }

    [Fact]
    public async Task EveryAvatarColour_GivesWhiteInitials45To1()
    {
        var css = await app.CreateClient().GetStringAsync("/app.css");

        css.Should().Contain("--tide-on-avatar: #ffffff;");
        for (var n = 1; n <= Ui.AvatarSwatches; n++)
        {
            var hex = Token(css, $"--tide-avatar-{n}");
            Contrast(hex, "#ffffff").Should().BeGreaterThanOrEqualTo(4.5, $"--tide-avatar-{n} is {hex}");
            css.Should().Contain($".tide-avatar-{n} {{ --presence-bg: var(--tide-avatar-{n}); }}");
        }
        css.Should().NotContain($"--tide-avatar-{Ui.AvatarSwatches + 1}:");
    }

    [Fact]
    public void AnAvatarColour_IsOneOfTheEight_TheSameEveryTime_AndFollowsTheHue()
    {
        var seeds = Enumerable.Range(0, 500).Select(i => $"user-{i}").Append(null).Append("").ToList();

        seeds.Select(Ui.AvatarSwatch).Should().OnlyContain(n => n >= 1 && n <= Ui.AvatarSwatches);
        seeds.Select(Ui.AvatarSwatch).Distinct().Should().HaveCount(Ui.AvatarSwatches, "500 people use every colour");
        Ui.AvatarSwatch("robin").Should().Be(Ui.AvatarSwatch("robin"));
        foreach (var s in seeds) Ui.AvatarSwatch(s).Should().Be(Ui.AvatarHue(s) / 45 + 1);
    }

    // A token's value as the stylesheet defines it the first time (in :root): "#b42335".
    private static string Token(string css, string name)
    {
        var at = css.IndexOf(name + ":", StringComparison.Ordinal);
        at.Should().BePositive($"app.css defines {name}");
        var start = at + name.Length + 1;
        return css[start..css.IndexOf(';', start)].Trim();
    }

    // WCAG 2.x contrast ratio of two #rrggbb colours.
    private static double Contrast(string a, string b)
    {
        double L(string hex)
        {
            double Channel(int i)
            {
                var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
        }
        var (la, lb) = (L(a), L(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
