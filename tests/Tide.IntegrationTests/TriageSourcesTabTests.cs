using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>The app, its outbound requests answered by <see cref="Http"/>, for Pull now.</summary>
public sealed class PullApp : TideApp
{
    public FakeHttp Http { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IOutboundHttp>(Http));
    }
}

/// <summary>Triage's Sources tab as the page draws it: the polled sources, the failing count, and Pull now.</summary>
public class TriageSourcesTabTests(PullApp app) : IClassFixture<PullApp>
{
    private static readonly ComponentHarness.SignIn Analyst = new("dev:analyst", "Analyst", "Analyst");

    // Polled sources of this test's own (by key), one failing, one pulled 4 minutes ago, one never pulled.
    private async Task<(Guid Fresh, Guid Failing, Guid Never)> Seed(string key)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var fresh = new Source { Name = $"{key} fresh news", Url = $"https://{key}.example.com/fresh", PollMinutes = 60, LastPolledAtUtc = now.AddMinutes(-4),
            LastSuccessAtUtc = now.AddMinutes(-4), LastNewItems = 3 };
        var failing = new Source { Name = $"{key} failing news", Url = $"https://{key}.example.com/failing", PollMinutes = 60, LastPolledAtUtc = now.AddHours(-2),
            LastError = "The server answered 503 Service Unavailable. " + new string('x', 120), ConsecutiveFailures = 1 };
        var never = new Source { Name = $"{key} new news", Url = $"https://{key}.example.com/never", PollMinutes = 60 };
        db.AddRange(fresh, failing, never,
            new Source { Name = $"{key} paused news", Url = $"https://{key}.example.com/paused", Enabled = false, LastError = "Old failure." });
        await db.SaveChangesAsync();
        return (fresh.Id, failing.Id, never.Id);
    }

    private static string Row(string html, string name)
    {
        var at = html.IndexOf($"<td class=\"fw-semibold\">{name}", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, name + " is listed");
        return html[at..html.IndexOf("</tr>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task SourcesTab_ListsThePolledSources_WithTheFailingCountAndTheWindowOnTheButton()
    {
        const string key = "tab";
        await Seed(key);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
        var html = await r.HtmlOnce($"{key} fresh news");

        html.Should().Contain("tide-pull-table").And.NotContain("tide-filterbar", "the table takes the item list's place")
            .And.NotContain($"{key} paused news", "paused sources aren't polled, so aren't listed");
        html.Should().MatchRegex("class=\"tide-tabbar-item active tide-tabbar-apart\" aria-pressed=\"true\"[^>]*>\\s*<span class=\"bi bi-rss\"[^>]*></span>\\s*<span>Sources</span>\\s*"
            + "<span class=\"tide-tabbar-count tide-tabbar-warn\"><span class=\"bi bi-exclamation-triangle-fill\" aria-hidden=\"true\"></span>\\d+<span class=\"visually-hidden\"> failing</span></span>");

        var fresh = Row(html, $"{key} fresh news");
        fresh.Should().MatchRegex("<abbr title=\"[A-Z][a-z]{2} \\d+, \\d+:\\d\\d [AP]M\">4 min ago</abbr>")
            .And.Contain("3 new items").And.Contain("disabled title=\"Pulled 4 min ago. You can pull it again in 11 min.\"")
            .And.Contain("Again in 11 min").And.NotContain(" Pull now</button>");
        var failing = Row(html, $"{key} failing news");
        failing.Should().Contain("2 h ago").And.Contain("class=\"tide-pull-error\" title=\"The server answered 503 Service Unavailable. xxx")
            .And.Contain("&#x2026;</div>", "the error is shortened, its whole text in the title").And.Contain(" Pull now</button>");
        Row(html, $"{key} new news").Should().Contain("Never").And.Contain("Not pulled yet").And.Contain(" Pull now</button>");
        html.Should().Contain("Polling is off", "scheduled polling is off in the test app, so nothing is due");
    }

    [Fact]
    public async Task PullNow_PullsTheSource_RefreshesTheRowAndTheNewCount_AndSaysSo()
    {
        const string key = "pull";
        var (_, _, never) = await Seed(key);
        app.Http.Set($"https://{key}.example.com/never",
            $"<rss version=\"2.0\"><channel><title>N</title><item><title>{key} story one</title><link>https://{key}.example.com/1</link></item>"
            + $"<item><title>{key} another headline entirely</title><link>https://{key}.example.com/2</link></item></channel></rss>");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
        await r.HtmlOnce($"{key} new news");
        var newBefore = NewCount(await r.Html());

        await r.Click(t => t.Pull(never));

        var html = await r.Html();
        Row(html, $"{key} new news").Should().Contain("just now").And.Contain("2 new items").And.Contain("Again in 15 min");
        NewCount(html).Should().Be(newBefore + 2, "the New tab's count is read again");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message == $"Pulled {key} new news: 2 new items.");

        await r.Click(t => t.Pull(never));
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should()
            .Contain(t => t.Message == "Pulled just now. You can pull it again in 15 min.", "pressed again (another tab's stale button), it's refused");
    }

    [Fact]
    public async Task PullNow_AFailedPull_SaysTheError()
    {
        const string key = "fails";
        var (_, _, never) = await Seed(key);
        app.Http.Responses[$"https://{key}.example.com/never"] = new HttpFetch(FetchStatus.Failed, Error: "Timed out.");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
        await r.HtmlOnce($"{key} new news");

        await r.Click(t => t.Pull(never));

        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message == $"Pulled {key} new news, but it failed: Timed out.");
        Row(await r.Html(), $"{key} new news").Should().Contain("Timed out.");
    }

    [Fact]
    public async Task SourcesTab_TheKeysDoNothing_AndTheOtherTabsComeBack()
    {
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage", @as: Analyst);
        await r.Click(async t =>
        {
            await t.ShowSources();
            await t.OnTriageKey("next");   // renders, as the tab's click would
        });
        (await r.Html()).Should().Contain("aria-pressed=\"true\"><span class=\"bi bi-rss\"");
        foreach (var key in new[] { "next", "prev", "shortlist", "dismiss", "promote", "read", "restore" })
            await r.Click(t => t.OnTriageKey(key));
        (await r.Html()).Should().NotContain("tide-promote-form").And.NotContain("tide-reading");

        await r.Click(async t =>
        {
            await t.SetStatus(FeedItemStatus.Dismissed);
            await t.OnTriageKey("prev");
        });
        var html = await r.Html();
        html.Should().Contain("tide-filterbar").And.NotContain("tide-pull-table");
        html.Should().MatchRegex("class=\"tide-tabbar-item active\" aria-pressed=\"true\"[^>]*>\\s*<span>Dismissed</span>");
    }

    [Fact]
    public async Task AReader_CantOpenTriage_SoNotItsSourcesTab()
    {
        using var reader = new ReaderApp();
        var client = reader.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync("/triage?tab=sources");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied?from=");
    }

    [Fact]
    public async Task AnActionOwner_CantOpenTriage_SoNotItsSourcesTab()
    {
        using var owner = new ActionOwnerApp();
        var client = owner.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync("/triage?tab=sources");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied?from=");
    }

    // The New tab's count, as the tab bar shows it.
    private static int NewCount(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, "<span>New</span>\\s*<span class=\"tide-tabbar-count[^\"]*\">(\\d+)</span>");
        m.Success.Should().BeTrue();
        return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ActionOwnerApp : TideApp
    {
        protected override string? Role => "ActionOwner";
    }
}
