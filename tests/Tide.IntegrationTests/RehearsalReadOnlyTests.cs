using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A rehearsal changes nothing, whichever part of present mode a click lands in: the presenter's rail, the slides, or a
/// card opened over or beside a slide (an action's, a product's, the indicators'). Every component present mode can
/// draw that injects a service that saves is accounted for here.
/// </summary>
public partial class RehearsalReadOnlyTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    // A brief with one article and an open action raised from it, as SignIn.Analyst (on every team, so the card would
    // offer its status and checklist outside a rehearsal).
    private async Task<(DateOnly Day, int Action)> Brief(int daysBack)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var team = (await sp.GetRequiredService<TeamService>().ListAsync())[0].Id;
        var brief = await sp.GetRequiredService<BriefService>().GetOrStartAsync(day);
        var article = await sp.GetRequiredService<ArticleService>().CreateAsync($"Rehearsed article {daysBack}", "Vendor", null, TlpLevel.Clear);
        await sp.GetRequiredService<BriefService>().AddArticleAsync(brief.Id, article);
        var number = await sp.GetRequiredService<ActionService>().QuickAddAsync(article, $"Rehearsed sweep {daysBack}", ActionKind.IocSweep, team, null, day);
        return (day, number);
    }

    private async Task<(int Events, int Audit, string Status)> Saved(int number)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var action = await db.Actions.AsNoTracking().SingleAsync(a => a.Number == number);
        return (await db.ActionEvents.CountAsync(), await db.AuditLog.CountAsync(), action.Status.ToString());
    }

    [Fact]
    public async Task InRehearsal_AnActionsCard_IsReadOnly_AndNothingItShowsChangesAnything()
    {
        var (day, number) = await Brief(0);
        var before = await Saved(number);
        await using var r = await Rehearse(app, day, SignIn.Analyst);
        await Do(r, p => p.ShowAction(number));
        var html = await r.Html();
        html.Should().Contain("tide-present-action", "the card opens, to read");
        html.Should().NotContain("tide-status-badge").And.NotContain("tide-status-opt").And.NotContain("data-status=", "no status to change");
        html.Should().NotContain("Take it").And.NotContain("Add checklist item");
        Checkbox().Matches(html).Where(m => !m.Value.Contains("disabled", StringComparison.Ordinal))
            .Should().BeEmpty("no checklist item can be ticked");
        await Do(r, p => p.OnActionChanged());
        await Do(r, p => p.CloseAction());
        (await Saved(number)).Should().Be(before, "a rehearsal saves nothing and records nothing");
    }

    [Fact]
    public async Task OutsideARehearsal_TheSameCard_StillOffersItsStatus()
    {
        var (day, number) = await Brief(2);
        await using var r = await Open(app, day);
        await Do(r, p => p.ShowAction(number));
        (await r.Html()).Should().Contain("tide-status-opt", "the presenter, with no meeting, works the action from its card");
    }

    // Services whose methods save, record or broadcast something: a component that injects one can change data.
    private static readonly Type[] Writers =
    [
        typeof(ActionService), typeof(BriefService), typeof(ArticleService), typeof(MeetingService),
        typeof(Tide.Application.Metrics.MetricsService), typeof(Tide.Application.Stack.StackService), typeof(TeamService),
        typeof(PlaybookService), typeof(Tide.Application.Preferences.UserPreferenceService),
        typeof(Tide.Web.Services.LiveMeetings), typeof(Tide.Web.Services.Presence),
    ];

    [Fact]
    public void EveryComponentPresentModeDraws_ThatCanSave_HonoursARehearsal()
    {
        var web = typeof(Present).Assembly;
        var components = web.GetTypes().Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var root = Root();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src", "Tide.Web", "Components"), "*.razor", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileNameWithoutExtension).ToDictionary(g => g.Key!, g => File.ReadAllText(g.First()));

        // Present and everything it draws, and everything those draw.
        var reached = new HashSet<string>(StringComparer.Ordinal) { "Present" };
        var queue = new Queue<string>(reached);
        while (queue.TryDequeue(out var name))
        {
            if (!sources.TryGetValue(name, out var markup)) continue;
            foreach (Match m in Tag().Matches(markup))
                if (components.ContainsKey(m.Groups[1].Value) && reached.Add(m.Groups[1].Value)) queue.Enqueue(m.Groups[1].Value);
        }
        reached.Should().Contain(["PresentAction", "ActionStatusControl", "PresentProduct", "PresentIndicators", "PresentCves"]);

        var writing = reached.Where(n => components[n].GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(p => p.GetCustomAttribute<InjectAttribute>() is not null && Writers.Contains(p.PropertyType))).ToList();
        writing.Should().Contain(["Present", "PresentAction", "ActionStatusControl", "ActionChecklist"], "the sweep finds what saves");
        // Present itself refuses in a rehearsal (InCharge, Run); everything it draws that can save takes the read-only
        // cascade it sets then, and saves nothing under it.
        writing.Where(n => n != "Present").Should().OnlyContain(n => TakesReadOnly(components[n]),
            "each component present mode draws that can save must take ReadOnlyCascade, and honour it");
    }

    private static bool TakesReadOnly(Type component) =>
        component.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(p => p.PropertyType == typeof(bool) && p.GetCustomAttribute<CascadingParameterAttribute>()?.Name == ReadOnlyCascade.Name);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [GeneratedRegex(@"<([A-Z][A-Za-z0-9]+)[\s/>]")]
    private static partial Regex Tag();

    [GeneratedRegex("<input type=\"checkbox\"[^>]*>")]
    private static partial Regex Checkbox();
}
