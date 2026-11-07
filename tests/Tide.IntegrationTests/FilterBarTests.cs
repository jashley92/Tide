using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Intake;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>Reading a rendered filter bar, and doing what its pills and menu would.</summary>
internal static partial class FilterBarHtml
{
    /// <summary>Whether the pill named <paramref name="label"/> is pressed; it must be there.</summary>
    public static bool Pressed(string html, string label)
    {
        var pill = Pill().Matches(html).FirstOrDefault(m => m.Groups[2].Value == label);
        pill.Should().NotBeNull($"a pill named {label} is in the bar");
        return pill!.Groups[1].Value == "true";
    }

    /// <summary>Presses the pill <paramref name="key"/>, as the bar does: its filter set to its values, or cleared when it's on.</summary>
    public static void Press(IReadOnlyList<FilterPill> pills, IReadOnlyList<FilterDef> filters, string key)
    {
        var pill = pills.Single(p => p.Key == key);
        var filter = filters.Single(f => f.Key == pill.FilterKey);
        filter.Apply(pill.Pressed(filter));
    }

    /// <summary>Sets the filter <paramref name="key"/>, as choosing in the menu or removing its chip does.</summary>
    public static void Set(IReadOnlyList<FilterDef> filters, string key, params string[] values) => filters.Single(f => f.Key == key).Apply(values);

    [GeneratedRegex("<button type=\"button\" class=\"tide-chip tide-chip-pick[^\"]*\" aria-pressed=\"(true|false)\"[^>]*>(?:<span class=\"bi bi-check2\" aria-hidden=\"true\"></span>)?([^<]*)</button>")]
    private static partial Regex Pill();
}

/// <summary>
/// EP-87: the shared filter bar. A pill is a preset of a filter (on while the filter holds its values, and that filter's
/// chip then isn't shown), a chip shows for each other filter set and removing it clears it, + Filter lists the filters
/// in the menu and setting one applies it, and Clear clears them. Each filtered list's pills and menu, and that a change
/// goes back to the first page with the address saying so.
/// </summary>
public class FilterBarTests(TideApp app) : IClassFixture<TideApp>
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void FilterValues_SplitJoinAndReadAParameter_AndAPillIsAPreset()
    {
        FilterValues.Split("p1, P1,,p2 ").Should().Equal("p1", "p2");
        FilterValues.Split("  ").Should().BeEmpty();
        FilterValues.Split(null).Should().BeEmpty();
        FilterValues.Join(["p1", "p2"]).Should().Be("p1,p2");
        FilterValues.Join([]).Should().BeNull("an empty filter drops out of the address");
        FilterValues.Of("x").Should().Equal("x");
        FilterValues.Of("").Should().BeEmpty();
        FilterValues.Of(true).Should().Equal("true");
        FilterValues.Of(false).Should().BeEmpty();
        FilterValues.First([]).Should().BeNull();
        FilterValues.Enum<Priority>(["p2"]).Should().Be(Priority.P2);
        FilterValues.Enum<Priority>(["P9"]).Should().BeNull();
        FilterOption.ForEnum<Priority>().Select(o => o.Value).Should().Equal("P1", "P2", "P3", "P4");
        FilterOption.ForEnum<ActionStatus>(s => s == ActionStatus.InProgress ? "In progress" : s.ToString())[1].Label.Should().Be("In progress");

        static void Nothing(IReadOnlyList<string> _) { }
        var team = new FilterDef("team", "Team", FilterKind.Single, Nothing) { Options = [new("ne", "Network Engineering")], Values = ["ne"] };
        team.Text.Should().Be("Team: Network Engineering");
        (team with { Values = ["gone"] }).Shown.Should().Be("gone", "a value no option has is shown as it is");
        new FilterDef("p", "Priority", FilterKind.Multi, Nothing) { Options = [new("P1", "P1"), new("P2", "P2")], Values = ["P1", "P2"] }.Text.Should().Be("Priority: P1, P2");
        (team with { ChipText = "Held by nobody" }).Text.Should().Be("Held by nobody");
        team.IsSet.Should().BeTrue();
        FilterValues.Chips([team, new FilterDef("off", "Off", FilterKind.Single, Nothing)]).Should().Equal(new FilterChipView("Team: Network Engineering", "team"));

        var p12 = new FilterPill("p1p2", "P1–P2", "priority", ["P1", "P2"]);
        var priority = new FilterDef("priority", "Priority", FilterKind.Multi, Nothing);
        p12.OnFor(priority with { Values = ["P2", "P1"] }).Should().BeTrue("in any order");
        p12.OnFor(priority with { Values = ["P1", "P2", "P3"] }).Should().BeFalse("exactly its values");
        p12.OnFor(priority with { Values = ["P1", "P3"] }).Should().BeFalse();
        p12.OnFor(null).Should().BeFalse();
        p12.Pressed(priority).Should().Equal("P1", "P2");
        p12.Pressed(priority with { Values = ["P1", "P2"] }).Should().BeEmpty("pressed while on, it clears its filter");
    }

    private sealed class Heard
    {
        public (string Key, IReadOnlyList<string> Values)? Set;
        public int Changed;
    }

    private static FilterDef[] Defs(Heard h) =>
    [
        new("team", "Team", FilterKind.Single, v => h.Set = ("team", v)) { Options = [new("ne", "Network Engineering"), new("soc", "SOC")], Values = ["ne"] },
        new("priority", "Priority", FilterKind.Multi, v => h.Set = ("priority", v)) { Options = [new("P1", "P1"), new("P2", "P2"), new("P3", "P3")] },
        new("tech", "Technique", FilterKind.Text, v => h.Set = ("tech", v)) { Placeholder = "T1566, or a name", Suggestions = [new("T1566", "Phishing")], Hint = "Under the tactic too." },
        new("grouped", "Tag", FilterKind.Single, v => h.Set = ("grouped", v)) { Options = [new("tag:none", "No tag"), new("tag:a", "Alpha", "Sector")] },
        new("due", "Due", FilterKind.Single, v => h.Set = ("due", v)) { Options = [new("Overdue", "Overdue"), new("ThisWeek", "This week")], Values = ["Overdue"] },
        new("parent", "Part of", FilterKind.Single, v => h.Set = ("parent", v)) { InMenu = false, Values = ["12"], ChipText = "Part of A-12" },
    ];

    private static readonly IReadOnlyList<FilterPill> BarPills =
        [new FilterPill("overdue", "Overdue", "due", ["Overdue"]), new FilterPill("week", "Due this week", "due", ["ThisWeek"]) { Disabled = true, Title = "No week" }];

    private async Task<(ComponentHarness.Rendered<FilterBar> Bar, FilterDef[] Defs)> Bar(Heard heard, Dictionary<string, object?>? more = null)
    {
        var defs = Defs(heard);
        var values = new Dictionary<string, object?>
        {
            ["Pills"] = BarPills,
            ["Filters"] = (IReadOnlyList<FilterDef>)defs,
            ["Count"] = "8 actions",
            ["Active"] = true,
            ["OnChanged"] = EventCallback.Factory.Create(heard, () => heard.Changed++),
        };
        foreach (var (k, v) in more ?? []) values[k] = v;
        return (await ComponentHarness.RenderAsync<FilterBar>(app, "/actions", values), defs);
    }

    [Fact]
    public async Task TheBar_ShowsItsPills_ChipsCountAndClear_AndAPillOnHidesItsFiltersChip()
    {
        var (r, _) = await Bar(new Heard());
        await using var _r = r;
        var html = WebUtility.HtmlDecode(await r.Html());

        html.Should().Contain("role=\"group\" aria-label=\"Quick filters\"");
        FilterBarHtml.Pressed(html, "Overdue").Should().BeTrue("its filter holds its value");
        FilterBarHtml.Pressed(html, "Due this week").Should().BeFalse();
        html.Should().Contain("title=\"No week\" disabled");
        html.Should().NotContain("Due: Overdue", "the pill that's on says it, so its chip isn't shown");
        html.Should().Contain("<button type=\"button\" class=\"tide-chip-edit\" title=\"Change this filter\" aria-haspopup=\"menu\">Team: Network Engineering</button>")
            .And.Contain("aria-label=\"Remove filter Team: Network Engineering\"");
        html.Should().Contain("<span>Part of A-12</span>", "a filter only a link sets is a chip to remove, not to change")
            .And.Contain("aria-label=\"Remove filter Part of A-12\"");
        html.Should().Contain("<span class=\"tide-dt-count\" role=\"status\">8 actions</span>");
        html.Should().Contain(">Clear</button>");
        html.Should().Contain("aria-haspopup=\"menu\" aria-expanded=\"false\"").And.NotContain("role=\"menu\"", "the menu is closed");
    }

    [Fact]
    public async Task APill_SetsItsFilter_OrClearsItWhenOn_AndAChipsRemoveClearsItsFilter()
    {
        var heard = new Heard();
        var (r, _) = await Bar(heard);
        await using var _r = r;
        await Do(r, b => b.Press(BarPills[1]));
        heard.Set!.Value.Key.Should().Be("due");
        heard.Set.Value.Values.Should().Equal(["ThisWeek"], "Due this week in place of Overdue: pills of one filter take turns");
        heard.Changed.Should().Be(1, "the page's refilter runs after");
        await Do(r, b => b.Press(BarPills[0]));
        heard.Set.Value.Values.Should().BeEmpty("Overdue was on, so pressing it clears the filter");
        await Do(r, b => b.Press(new FilterPill("x", "X", "nothing", ["y"])));
        heard.Changed.Should().Be(2, "a pill of no filter does nothing");

        await Do(r, b => b.Remove("team"));
        heard.Set.Value.Should().Be(("team", heard.Set.Value.Values));
        heard.Set.Value.Values.Should().BeEmpty();
        await Do(r, b => b.Remove("nothing"));
        heard.Changed.Should().Be(3);
    }

    [Fact]
    public async Task OnALinkedPage_ChipsAndClearAreLinks_AndClearShowsOnlyWhenActive()
    {
        var (r, _) = await Bar(new Heard(), new()
        {
            ["Chips"] = (IReadOnlyList<FilterChipView>)[new FilterChipView("Oct 2026", "from", "articles?q=x"), new FilterChipView("Due: Overdue", "due", "articles?due=")],
            ["ClearHref"] = "articles?q=x",
        });
        await using var _r = r;
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("<a href=\"articles?q=x\" class=\"tide-chip-remove\" aria-label=\"Remove filter Oct 2026\"");
        html.Should().Contain("<a href=\"articles?q=x\" class=\"tide-filterbar-clear small\">Clear</a>");
        html.Should().NotContain("Team: Network Engineering", "the page's own chips stand in for the filters'");
        html.Should().NotContain("Due: Overdue", "a page's own chip is hidden too while a pill on its filter is on");

        var (none, _) = await Bar(new Heard(), new() { ["Active"] = false, ["Count"] = null });
        await using var _none = none;
        (await none.Html()).Should().NotContain("tide-filterbar-clear").And.NotContain("role=\"status\"");
    }

    [Fact]
    public async Task TheMenu_ListsTheFiltersInIt_AndASingleOneIsSetAsItsChosen()
    {
        var heard = new Heard();
        var (r, defs) = await Bar(heard);
        await using var _r = r;
        await Do(r, b => { b.Toggle(); return Task.CompletedTask; });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("aria-expanded=\"true\"").And.Contain("role=\"menu\" aria-label=\"Add a filter\"");
        var menu = html[html.IndexOf("Add a filter", StringComparison.Ordinal)..];
        foreach (var label in new[] { "Team", "Priority", "Technique", "Tag", "Due" }) menu.Should().Contain($"<span>{label}</span>");
        menu.Should().NotContain("<span>Part of</span>", "set by a link only");
        menu.Should().Contain("<span class=\"tide-filter-item-now\">Network Engineering</span>", "what a filter is set to shows beside it");

        await Do(r, b => { b.Show(defs[0]); return Task.CompletedTask; });
        html = WebUtility.HtmlDecode(await r.Html());
        r.Component.Step.Should().Be(defs[0]);
        html.Should().Contain("role=\"menu\" aria-label=\"Team\"").And.Contain("role=\"menuitemradio\" tabindex=\"-1\" aria-checked=\"true\"")
            .And.Contain("Remove this filter");
        await Do(r, b => b.Pick("soc"));
        heard.Set!.Value.Values.Should().Equal("soc");
        heard.Changed.Should().Be(1);
        r.Component.IsOpen.Should().BeFalse("choosing applies it and closes the menu");

        // Grouped options show under their group's name.
        await Do(r, b => { b.Open(defs[3]); return Task.CompletedTask; });
        WebUtility.HtmlDecode(await r.Html()).Should().Contain("<div class=\"tide-filter-group\" role=\"presentation\">Sector</div>");
        await Do(r, b => { b.Show(null); return Task.CompletedTask; });
        r.Component.Step.Should().BeNull("back to the list of filters");
        r.Component.IsOpen.Should().BeTrue();
        await Do(r, b => { b.Key(new KeyboardEventArgs { Key = "Enter" }); return Task.CompletedTask; });
        r.Component.IsOpen.Should().BeTrue("only Esc closes it");
        await Do(r, b => { b.Key(new KeyboardEventArgs { Key = "Escape" }); return Task.CompletedTask; });
        r.Component.IsOpen.Should().BeFalse();
        await Do(r, b => { b.Toggle(); b.Toggle(); return Task.CompletedTask; });
        r.Component.IsOpen.Should().BeFalse("+ Filter again closes it");
        await Do(r, b => b.Pick("x"));
        await Do(r, b => b.ApplyDraft());
        await Do(r, b => b.ApplyText());
        heard.Changed.Should().Be(1, "with no filter open, nothing is applied");
    }

    [Fact]
    public async Task AMultiFilter_AppliesItsTicksTogether_AndATextOneAsTyped()
    {
        var heard = new Heard();
        var (r, defs) = await Bar(heard);
        await using var _r = r;
        await Do(r, b => { b.Open(defs[1]); return Task.CompletedTask; });
        foreach (var p in new[] { "P3", "P1", "P3", "P2" }) await Do(r, b => b.Pick(p));
        WebUtility.HtmlDecode(await r.Html()).Should().Contain("role=\"menuitemcheckbox\"").And.Contain(">Apply</button>");
        heard.Set.Should().BeNull("ticks wait for Apply");
        await Do(r, b => b.ApplyDraft());
        heard.Set!.Value.Values.Should().Equal(["P1", "P2"], "in the options' order");

        var text = typeof(FilterBar).GetField("_text", Private)!;
        await Do(r, b => { b.Open(defs[2]); return Task.CompletedTask; });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("role=\"dialog\" aria-label=\"Technique\"").And.Contain("placeholder=\"T1566, or a name\"")
            .And.Contain("<option value=\"T1566\">Phishing</option>").And.Contain(">Under the tactic too.</p>");
        // What's typed reaches the bar through the box's binding.
        await Do(r, b => { text.SetValue(b, " T1059 "); return b.ApplyText(); });
        heard.Set.Value.Should().Be(("tech", heard.Set.Value.Values));
        heard.Set.Value.Values.Should().Equal("T1059");
        await Do(r, b => { b.Open(defs[2]); text.SetValue(b, "  "); return b.ApplyText(); });
        heard.Set.Value.Values.Should().BeEmpty("an empty box clears it");
    }

    // Does what a control would, then draws again, as an event handler would.
    private static Task Do<T>(ComponentHarness.Rendered<T> r, Func<T, Task> what) where T : ComponentBase =>
        r.Click(async c =>
        {
            await what(c);
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(c, null);
        });

    // What a page's filter change wrote to the address (outside a circuit, writing it throws, which says where it went).
    private static async Task<string> Went<T>(ComponentHarness.Rendered<T> r, Action<T> what) where T : ComponentBase
    {
        try { await Do(r, c => { what(c); return Task.CompletedTask; }); }
        catch (NavigationException e) { return Uri.UnescapeDataString(e.Location); }
        throw new InvalidOperationException("The change didn't write the address.");
    }

    private static async Task<string> Went<T>(ComponentHarness.Rendered<T> r, Func<T, Task> what) where T : ComponentBase
    {
        try { await Do(r, what); }
        catch (NavigationException e) { return Uri.UnescapeDataString(e.Location); }
        throw new InvalidOperationException("The change didn't write the address.");
    }

    private async Task<(Guid Id, string Name)> AnyTeam()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var team = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Teams.OrderBy(t => t.Name).FirstAsync();
        return (team.Id, team.Name);
    }

    [Fact]
    public async Task Actions_PillsSayTheAddress_AndPressingOneWritesIt_FromThePageOne()
    {
        await using var r = await ComponentHarness.RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&page=2&priority=p2,p1&status=blocked&due=week");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("action"));
        FilterBarHtml.Pressed(html, "P1–P2").Should().BeTrue("the priority filter is exactly P1 and P2");
        FilterBarHtml.Pressed(html, "Blocked").Should().BeTrue();
        FilterBarHtml.Pressed(html, "Tracking").Should().BeFalse();
        FilterBarHtml.Pressed(html, "Due this week").Should().BeTrue();
        FilterBarHtml.Pressed(html, "Overdue").Should().BeFalse();
        html.Should().NotContain("Priority: P1, P2").And.NotContain("Status: Blocked").And.NotContain("Due: This week", "a pill says each");

        Task Press(ActionsBoard a, string key)
        {
            FilterBarHtml.Press(ActionsBoard.Pills, a.Defs, key);
            return a.Refilter();
        }
        var went = await Went(r, a => Press(a, "tracking"));
        went.Should().Contain("status=tracking").And.Contain("priority=p1,p2").And.NotContain("page=", "a change goes back to the first page");
        ((int)typeof(ActionsBoard).GetField("_page", Private)!.GetValue(r.Component)!).Should().Be(1);

        (await Went(r, a => Press(a, "p1p2"))).Should().NotContain("priority=", "P1–P2 again turns it off");
        (await Went(r, a => Press(a, "week"))).Should().NotContain("due=");
        (await Went(r, a => Press(a, "overdue"))).Should().Contain("due=overdue");
        (await Went(r, a => Press(a, "overdue"))).Should().NotContain("due=");
        (await Went(r, a => Press(a, "p1p2"))).Should().Contain("priority=p1,p2");
        (await Went(r, a => Press(a, "blocked"))).Should().Contain("status=blocked", "Blocked in place of Tracking: they take turns");
    }

    [Fact]
    public async Task Actions_TheMenuHoldsTheOtherFilters_ChipsShowThem_AndClearKeepsTheView()
    {
        var (teamId, teamName) = await AnyTeam();
        await using var r = await ComponentHarness.RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&team={teamId}&priority=p3&due=none&status=inprogress&parent=12&assignee=none&q=zzqx");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("action"));
        var menu = r.Component.Defs.Where(d => d.InMenu).Select(d => d.Key).ToList();
        menu.Take(5).Should().Equal("team", "kind", "priority", "due", "status");
        menu.Should().NotContain(["assignee", "parent"], "set by links, so chips only");
        r.Component.Defs.Single(d => d.Key == "team").Options.Should().Contain(o => o.Value == teamId.ToString() && o.Label == teamName, "built as the teams are read");
        foreach (var chip in new[] { $"Team: {teamName}", "Priority: P3", "Due: No due date", "Status: In progress", "Part of A-12", "Held by nobody (the team's)" })
            html.Should().Contain($"aria-label=\"Remove filter {chip}\"");
        FilterBarHtml.Pressed(html, "P1–P2").Should().BeFalse();

        Task Set(ActionsBoard a, string key, params string[] values)
        {
            FilterBarHtml.Set(a.Defs, key, values);
            return a.Refilter();
        }
        (await Went(r, a => Set(a, "parent"))).Should().NotContain("parent=");
        (await Went(r, a => Set(a, "assignee"))).Should().NotContain("assignee=");
        (await Went(r, a => Set(a, "team"))).Should().NotContain("team=");
        (await Went(r, a => Set(a, "team", teamId.ToString()))).Should().Contain($"team={teamId}");
        (await Went(r, a => Set(a, "kind", "Block"))).Should().Contain("kind=block");
        (await Went(r, a => Set(a, "priority", "P3", "p1", "nonsense"))).Should().Contain("priority=p1,p3");
        (await Went(r, a => Set(a, "due", "Overdue"))).Should().Contain("due=overdue");
        (await Went(r, a => Set(a, "status", "Done"))).Should().NotContain("status=", "only the open statuses filter the list");

        var cleared = await Went(r, a => a.ClearFilters());
        cleared.Should().Contain("view=open").And.Contain("layout=list");
        foreach (var name in new[] { "q=", "team=", "kind=", "priority=", "due=", "status=", "rule=", "parent=", "assignee=" }) cleared.Should().NotContain(name);
    }

    [Fact]
    public async Task Triage_PillsSayTheAddress_KevAndNamesACveTakeTurns_AndClearResetsThem()
    {
        IReadOnlyList<Tide.Domain.Entities.Source> sources;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            sources = await scope.ServiceProvider.GetRequiredService<IntakeService>().SourcesAsync(includeRetired: true);
        var source = sources.Count > 0 ? sources[0].Id : (Guid?)null;
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&cve=kev&named=true&page=3{(source is { } s ? $"&source={s}" : "")}");
        var html = WebUtility.HtmlDecode(await r.Html());
        FilterBarHtml.Pressed(html, "KEV").Should().BeTrue();
        FilterBarHtml.Pressed(html, "Names a CVE").Should().BeFalse();
        FilterBarHtml.Pressed(html, "We run it").Should().BeTrue();
        html.Should().NotContain("Remove filter CVEs").And.NotContain("Remove filter We run it", "the pills say them");
        r.Component.Defs.Where(d => d.InMenu).Select(d => d.Key).Should().Equal(["source"], "the source is under + Filter");
        if (source is not null) html.Should().Contain($"aria-label=\"Remove filter Source: {sources[0].Name}\"");

        string Address() => (string)typeof(Triage).GetMethod("PageHref", Private)!.Invoke(r.Component, [1])!;
        int Page() => (int)typeof(Triage).GetField("_page", Private)!.GetValue(r.Component)!;
        Task Press(Triage t, string key)
        {
            FilterBarHtml.Press(t.Pills, t.Defs, key);
            return t.Refilter();
        }
        await Do(r, t => Press(t, "cve"));
        Address().Should().Contain("cve=any").And.NotContain("cve=kev");
        Page().Should().Be(1, "a change goes back to the first page");
        await Do(r, t => Press(t, "cve"));
        Address().Should().NotContain("cve=");
        await Do(r, t => Press(t, "kev"));
        Address().Should().Contain("cve=kev");
        await Do(r, t => Press(t, "named"));
        Address().Should().NotContain("named=");
        await Do(r, t => Press(t, "since"));
        var since = r.Component.Pills.Single(p => p.Key == "since");
        since.OnFor(r.Component.Defs.Single(d => d.Key == "since")).Should().Be(!since.Disabled, "on only once there's been a brief");
        await Do(r, t => { FilterBarHtml.Set(t.Defs, "source", Guid.NewGuid().ToString()); return t.Refilter(); });
        Address().Should().NotContain("source=", "a source TIDE doesn't have isn't a filter");

        await Do(r, t => t.ClearFilters());
        Address().Should().Be("triage?tab=dismissed");
        r.Component.Pills.Should().OnlyContain(p => !p.OnFor(r.Component.Defs.Single(d => d.Key == p.FilterKey)));
    }

    [Fact]
    public async Task SavedQueries_APillPerLanguage_OneAtATime_AndClear()
    {
        await using var r = await ComponentHarness.RenderAsync<SavedQueriesPage>(app, "/hunts/saved?page=2");
        SavedQueriesPage.Pills.Select(p => p.Label).Should().Equal("XQL", "KQL", "Falcon");
        bool On(string key) => SavedQueriesPage.Pills.Single(p => p.Key == key).OnFor(r.Component.Defs.Single());
        Task Press(SavedQueriesPage p, string key)
        {
            FilterBarHtml.Press(SavedQueriesPage.Pills, p.Defs, key);
            p.Refilter();
            return Task.CompletedTask;
        }
        await Do(r, p => Press(p, "kql"));
        r.Component.Page.Should().Be(1);
        r.Component.Shown.Should().OnlyContain(q => q.Query.Dialect == "kql");
        var html = WebUtility.HtmlDecode(await r.Html());
        FilterBarHtml.Pressed(html, "KQL").Should().BeTrue();
        FilterBarHtml.Pressed(html, "XQL").Should().BeFalse();
        html.Should().Contain("id=\"sq-retired\"", "Show retired stays a switch");
        await Do(r, p => Press(p, "fql"));
        On("fql").Should().BeTrue();
        On("kql").Should().BeFalse("one language at a time");
        await Do(r, p => Press(p, "fql"));
        On("fql").Should().BeFalse();
        await Do(r, p => { FilterBarHtml.Press(SavedQueriesPage.Pills, p.Defs, "kql"); p.ClearFilters(); return Task.CompletedTask; });
        On("kql").Should().BeFalse();
        WebUtility.HtmlDecode(await r.Html()).Should().NotContain("tide-filterbar-clear");
    }

    [Fact]
    public async Task OutreachTemplates_ClearEmptiesTheWords()
    {
        await using var r = await ComponentHarness.RenderAsync<OutreachTemplatesPage>(app, "/outreach/templates");
        typeof(OutreachTemplatesPage).GetField("_words", Private)!.SetValue(r.Component, "zzqx");
        WebUtility.HtmlDecode(await r.Html()).Should().Contain("id=\"ot-retired\"");
        await Do(r, p => { p.ClearFilters(); return Task.CompletedTask; });
        typeof(OutreachTemplatesPage).GetField("_words", Private)!.GetValue(r.Component).Should().BeNull();
    }

    [Fact]
    public async Task Articles_DraftAndReadyArePills_TheMenuSetsTheRest_IntoTheAddress()
    {
        await using var r = await ComponentHarness.RenderAsync<Articles>(app, "/articles?status=draft&tactic=TA0001&technique=T1566&page=3");
        var html = WebUtility.HtmlDecode(await r.Html());
        FilterBarHtml.Pressed(html, "Draft").Should().BeTrue();
        html.Should().NotContain("Remove filter Status: Draft", "the Draft pill says it").And.Contain("aria-label=\"Remove filter Tactic: Initial Access\"");
        r.Component.Defs.Select(d => d.Key).Should().Equal("status", "tactic", "technique", "withsubs", "assess", "tag", "from", "to");
        r.Component.Defs.Single(d => d.Key == "tag").Options.Should().Contain(o => o.Value == "tag:none");

        (await Went(r, a => FilterBarHtml.Press(Articles.Pills, a.Defs, "draft"))).Should().NotContain("status=").And.NotContain("page=");
        (await Went(r, a => FilterBarHtml.Press(Articles.Pills, a.Defs, "ready"))).Should().Contain("status=ready");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "status", "Presented"))).Should().Contain("status=presented");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "tactic", "TA0002"))).Should().Contain("tactic=TA0002");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "assess", "P1P2"))).Should().Contain("assess=p1-p2");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "tag", "category:sector"))).Should().Contain("category=sector").And.NotContain("tag=");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "tag", "tag:none"))).Should().Contain("tag=none");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "technique", "t1059 Command"))).Should().Contain("technique=T1059");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "withsubs", "true"))).Should().Contain("withsubs=true");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "from", "2026-10-01"))).Should().Contain("from=2026-10-01");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "to"))).Should().NotContain("to=");
    }

    [Fact]
    public async Task AuditLog_DeletedIsAPill_TheMenuSetsWhoKindActionAndDates()
    {
        await using var r = await ComponentHarness.RenderAsync<AuditLogAdmin>(app, "/admin/system/audit?action=deleted&who=dev:tester&page=2");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("change"));
        FilterBarHtml.Pressed(html, "Deleted").Should().BeTrue();
        html.Should().NotContain("Remove filter Action: deleted", "the Deleted pill says it");
        r.Component.Defs.Select(d => d.Key).Should().Equal("who", "kind", "action", "from", "to");

        (await Went(r, a => FilterBarHtml.Press(AuditLogAdmin.Pills, a.Defs, "deleted"))).Should().NotContain("action=").And.NotContain("page=");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "action", "Created"))).Should().Contain("action=created");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "who"))).Should().NotContain("who=");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "kind", "Article"))).Should().Contain("kind=Article");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "from", "2026-10-01"))).Should().Contain("from=2026-10-01");
        (await Went(r, a => FilterBarHtml.Set(a.Defs, "to", "2026-10-31"))).Should().Contain("to=2026-10-31");
    }
}
