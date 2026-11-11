using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;
using Tide.Web.Components.LibraryRail;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The Library's context rail: the selected entry's facts, what an action fills in, the entry tried on an open action
/// and where it was used. Nothing becomes more visible: the rail lists only actions the person may read, offers only
/// those they work, previews without writing, and adds only through the action page's own checked step.
/// </summary>
public class LibraryRailTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:librail-viewer", "Lib Viewer", "Viewer");
    private static readonly SignIn Owner = new("dev:librail-owner", "Lib Owner", "ActionOwner");
    private static readonly SignIn Reader = new("dev:librail-reader", "Lib Reader", "Reader");
    private const string Rail = "<aside id=\"tide-rail\"";

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task As(Func<IServiceProvider, Task> what, SignIn? who = null) => As(async sp => { await what(sp); return 0; }, who);

    private Task<Guid> Team(string name) => As(sp => sp.GetRequiredService<TeamService>().AddAsync(name, null, null));

    /// <summary>An action of <paramref name="kind"/> for <paramref name="team"/>, raised from an article with one IP; its number and id.</summary>
    private Task<(int Number, Guid Id)> Raise(Guid team, string title, ActionKind kind = ActionKind.IocSweep, string ip = "203.0.113.77") => As(async sp =>
    {
        var articles = sp.GetRequiredService<ArticleService>();
        var actions = sp.GetRequiredService<ActionService>();
        var article = await articles.CreateAsync(title + " (article)", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Bluf = "Exploited.";
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = ip }];
        await articles.SaveAsync(form);
        var number = await actions.CreateAsync(new ActionForm { Title = title, Kind = kind, TeamId = team, ArticleId = article });
        return (number, (await actions.GetAsync(number))!.Action.Id);
    });

    private Task<Guid> Query(string name) => As(sp => sp.GetRequiredService<SavedQueryService>().SaveAsync(new SavedQueryForm
    {
        Name = name, Dialect = "kql", Description = name + " for the rail",
        Text = "DeviceNetworkEvents\n| where Timestamp > ago({{days}}d)\n| where RemoteIP in ({{values}})",
    }));

    /// <summary>What trying an entry must leave alone: the action's hunts, history and checklist, and the audit log.</summary>
    private Task<(int Hunts, int Events, int Steps, int Audit)> Trace(Guid action) => As(async sp =>
    {
        await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        return (await db.HuntQueries.CountAsync(h => h.ActionId == action), await db.ActionEvents.CountAsync(e => e.ActionId == action),
            await db.ActionChecklistItems.CountAsync(i => i.ActionId == action), await db.AuditLog.CountAsync());
    });

    private Task<Rendered<LibraryEntryRail>> RailFor(LibraryKind kind, Guid entry, string name, SignIn? who = null, ActionKind? playbookFor = null) =>
        RenderAsync<LibraryEntryRail>(app, "/library", new()
        {
            ["Kind"] = kind, ["EntryId"] = entry, ["Name"] = name, ["Noun"] = "entry", ["For"] = playbookFor,
        }, who);

    /// <summary>The page's markup and its rail's, split where MainLayout draws the rail.</summary>
    private static (string Page, string Rail) Split(string html)
    {
        html = WebUtility.HtmlDecode(html);
        var at = html.IndexOf(Rail, StringComparison.Ordinal);
        at.Should().BePositive("the Library tab has a context rail");
        return (html[..at], html[at..]);
    }

    [Fact]
    public async Task TheSelectedQuery_ShowsItsFacts_WhatItFillsIn_ItTriedOnAnOpenAction_AndWhereItWasUsed_InTheRail()
    {
        var team = await Team("Rail query team");
        var query = await Query("Rail RDP sweep");
        var (number, _) = await Raise(team, "Rail sweep the VPN logs");

        await using var page = await RenderAsync<SavedQueriesPage>(app, $"/library/queries?row={query}");
        (await page.Until(_ => page.Html().Result.Contains("lib-try-text"))).Should().BeTrue();
        var (inPage, rail) = Split(await page.Html());

        rail.Should().Contain("aria-label=\"About this query\"").And.Contain("<dt>Platform</dt>").And.Contain("<dt>Saved by</dt>").And.Contain("<dt>Used</dt>");
        rail.Should().Contain("Fills in").And.Contain("{{days}}").And.Contain("the action's window").And.Contain("{{values}}").And.Contain("its indicators");
        rail.Should().Contain("Try it on an open action").And.Contain($"A-{number} Rail sweep the VPN logs");
        rail.Should().Contain("203.0.113.77", "the query is filled in for the action chosen").And.Contain("Copy filled in").And.Contain($"Add to A-{number}");
        rail.Should().Contain("Used on").And.Contain("No actions yet");
        inPage.Should().NotContain("Try it on an open action");
        // The page keeps the entry, its heading large, and says that TIDE never runs it and what's under More.
        inPage.Should().Contain("class=\"tide-library-entry-name\"").And.Contain("TIDE never runs a query itself. Retire and Delete are under More.");
    }

    [Fact]
    public async Task AViewer_SeesTheFactsAndUses_ButIsOfferedNoActionToTryItOn()
    {
        var team = await Team("Rail viewer team");
        var query = await Query("Rail viewer sweep");
        var (number, id) = await Raise(team, "Rail viewer action");
        await As(sp => sp.GetRequiredService<ActionService>().AddSavedHuntAsync(id, query));

        await using var page = await RenderAsync<SavedQueriesPage>(app, $"/library/queries?row={query}", @as: Viewer);
        var (_, rail) = Split(await page.Html());

        rail.Should().Contain("<dt>Platform</dt>").And.Contain("Fills in").And.Contain($"A-{number}").And.Contain("not run yet");
        rail.Should().NotContain("Try it on an open action").And.NotContain("Add to A-").And.NotContain("lib-try-action");
        (await As(sp => sp.GetRequiredService<LibraryUseService>().WorkableAsync(), Viewer)).Should().BeEmpty();
    }

    [Fact]
    public async Task SomeoneWhoWorksActions_IsOfferedOnlyTheirTeamsOpenActions_OfTheKindTheEntryIsFor()
    {
        var mine = await Team("Rail owner team");
        var theirs = await Team("Rail other team");
        await As(sp => sp.GetRequiredService<TeamService>().AddMemberAsync(mine, Owner.UserId, Owner.Name));
        var (myNumber, _) = await Raise(mine, "Rail owner sweep");
        var (theirNumber, _) = await Raise(theirs, "Rail other sweep");
        var (patchNumber, _) = await Raise(mine, "Rail owner patch", ActionKind.PatchRemediate);
        var (closedNumber, closedId) = await Raise(mine, "Rail owner closed sweep");
        await As(sp => sp.GetRequiredService<ActionService>().SetStatusAsync(closedId, ActionStatus.Done, ActionOutcome.NoHits, null));
        var query = await Query("Rail owner query");

        var workable = await As(sp => sp.GetRequiredService<LibraryUseService>().WorkableAsync(), Owner);
        workable.Select(a => a.Number).Should().Contain([myNumber, patchNumber]).And.NotContain([theirNumber, closedNumber]);

        await using var rail = await RailFor(LibraryKind.SavedQuery, query, "Rail owner query", Owner);
        rail.Component.Offered.Select(a => a.Number).Should().Contain(myNumber).And.NotContain([theirNumber, patchNumber, closedNumber]);
        var html = WebUtility.HtmlDecode(await rail.Html());
        html.Should().Contain($"A-{myNumber} Rail owner sweep").And.NotContain("Rail other sweep");

        // An analyst works every team's.
        (await As(sp => sp.GetRequiredService<LibraryUseService>().WorkableAsync())).Select(a => a.Number).Should().Contain([myNumber, theirNumber]);
    }

    [Fact]
    public async Task UsedOn_ListsTheActionsItWasAddedTo_WithWhatTheyFound_ButNotADeletedOne_AndOnlyForSomeoneWhoReadsActions()
    {
        var team = await Team("Rail used team");
        var query = await Query("Rail used query");
        var (kept, keptId) = await Raise(team, "Rail used kept");
        var (gone, goneId) = await Raise(team, "Rail used deleted");
        var actions = (Func<IServiceProvider, ActionService>)(sp => sp.GetRequiredService<ActionService>());
        await As(async sp =>
        {
            var hunt = await actions(sp).AddSavedHuntAsync(keptId, query);
            await actions(sp).RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 3, null, null);
            await actions(sp).AddSavedHuntAsync(goneId, query);
            await actions(sp).DeleteAsync(goneId, "Raised twice.");
        });

        foreach (var who in new[] { SignIn.Analyst, Viewer, Owner })
        {
            var uses = await As(sp => sp.GetRequiredService<LibraryUseService>().UsedOnAsync(LibraryKind.SavedQuery, query), who);
            uses.Total.Should().Be(1);
            uses.Rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new LibraryUse(kept, "Rail used kept", ActionStatus.Open, "3 hits"));
            uses.Rows.Select(u => u.Number).Should().NotContain(gone);
        }

        var refused = () => As(sp => sp.GetRequiredService<LibraryUseService>().UsedOnAsync(LibraryKind.SavedQuery, query), Reader);
        await refused.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ChoosingAnAction_PreviewsTheQueryFilledIn_AndWritesNothing()
    {
        var team = await Team("Rail preview team");
        var query = await Query("Rail preview query");
        var (other, otherId) = await Raise(team, "Rail preview other", ip: "198.51.100.9");
        var (number, id) = await Raise(team, "Rail preview sweep");
        var before = (await Trace(id), await Trace(otherId));

        await using var rail = await RailFor(LibraryKind.SavedQuery, query, "Rail preview query");
        rail.Component.Text.Should().Contain("203.0.113.77").And.Contain("ago(");
        await rail.Click(c => c.Choose(otherId));
        rail.Component.Text.Should().Contain("198.51.100.9");

        (await Trace(id), await Trace(otherId)).Should().Be(before, "nothing is saved or logged until Add");
        (await As(sp => sp.GetRequiredService<ActionService>().GetAsync(number)))!.Action.Hunts.Should().BeEmpty();
        _ = other;
    }

    [Fact]
    public async Task AddToTheAction_RunsTheActionPagesOwnStep_AndUsedOnSaysSo()
    {
        var team = await Team("Rail add team");
        var query = await Query("Rail add query");
        var (number, id) = await Raise(team, "Rail add sweep");

        await using var rail = await RailFor(LibraryKind.SavedQuery, query, "Rail add query");
        await rail.Click(c => c.Add());

        var detail = (await As(sp => sp.GetRequiredService<ActionService>().GetAsync(number)))!;
        detail.Action.Hunts.Should().ContainSingle().Which.SavedQueryId.Should().Be(query);
        detail.Action.Events.Should().Contain(e => e.Text.Contains("Added the saved") && e.Text.Contains("Rail add query"));
        WebUtility.HtmlDecode(await rail.Html()).Should().Contain("Used on (1)").And.Contain($"A-{number}");
        rail.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message == $"Rail add query added to A-{number}.");
        _ = id;
    }

    [Fact]
    public async Task AddToTheAction_IsRefused_ForSomeoneWhoNoLongerWorksIt_AndWritesNothing()
    {
        var team = await Team("Rail refused team");
        await As(sp => sp.GetRequiredService<TeamService>().AddMemberAsync(team, Owner.UserId, Owner.Name));
        var query = await Query("Rail refused query");
        var (_, id) = await Raise(team, "Rail refused sweep");

        await using var rail = await RailFor(LibraryKind.SavedQuery, query, "Rail refused query", Owner);
        rail.Component.Offered.Should().Contain(a => a.Id == id);
        // Taken off the team after the preview: Add is refused by the action's own check.
        await As(sp => sp.GetRequiredService<TeamService>().RemoveMemberAsync(team, Owner.UserId));
        var before = await Trace(id);
        await rail.Click(c => c.Add());

        (await Trace(id)).Should().Be(before);
        rail.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.IsError);
    }

    [Fact]
    public async Task AddToTheAction_IsRefused_WhenTheQueryWasRetiredSinceThePreview_AndWritesNothing()
    {
        var team = await Team("Rail stale team");
        var query = await Query("Rail stale query");
        var (_, id) = await Raise(team, "Rail stale sweep");

        await using var rail = await RailFor(LibraryKind.SavedQuery, query, "Rail stale query");
        rail.Component.Text.Should().NotBeNull();
        await As(sp => sp.GetRequiredService<SavedQueryService>().SetRetiredAsync(query, true));
        var before = await Trace(id);
        await rail.Click(c => c.Add());

        (await Trace(id)).Should().Be(before);
        rail.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.IsError && t.Message.Contains("retired"));
    }

    /// <summary>A colleague, in another tab, edits the action's title (its edit version moves).</summary>
    private Task Retitle(int number) => As(async sp =>
    {
        var actions = sp.GetRequiredService<ActionService>();
        var form = ActionForm.From((await actions.GetAsync(number))!.Action);
        form.Title += " (edited)";
        await actions.SaveAsync(form);
    });

    /// <summary>A colleague, in another tab, adds an indicator to the action's article.</summary>
    private Task AddIndicator(int number, string ip) => As(async sp =>
    {
        var articles = sp.GetRequiredService<ArticleService>();
        var article = (await sp.GetRequiredService<ActionService>().GetAsync(number))!.Action.ArticleId!.Value;
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Indicators.Add(new() { Type = IndicatorType.IpAddress, Value = ip });
        await articles.SaveAsync(form);
    });

    [Theory]
    [InlineData("indicator")]
    [InlineData("title")]
    public async Task AddToTheAction_IsRefusedAsStale_WhenTheActionChangedSinceThePreview_AndWritesNothing(string change)
    {
        var team = await Team("Rail stale preview team " + change);
        var query = await Query("Rail stale preview query " + change);
        var (number, id) = await Raise(team, "Rail stale preview sweep " + change);

        await using var rail = await RailFor(LibraryKind.SavedQuery, query, "Rail stale preview query");
        rail.Component.Text.Should().NotContain("198.51.100.200");
        if (change == "indicator") await AddIndicator(number, "198.51.100.200");
        else await Retitle(number);
        var before = await Trace(id);
        await rail.Click(c => c.Add());

        (await Trace(id)).Should().Be(before, "what would be added isn't what the person saw");
        rail.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.IsError && t.Message.Contains("Someone else changed this action"));
        if (change == "indicator") rail.Component.Text.Should().Contain("198.51.100.200", "the preview is read again");
        // Tried again on what's shown now, it's added.
        await rail.Click(c => c.Add());
        (await Trace(id)).Hunts.Should().Be(before.Hunts + 1);
    }

    [Theory]
    [InlineData("step")]
    [InlineData("title")]
    public async Task ApplyingAPlaybook_IsRefusedAsStale_WhenTheActionChangedSinceThePreview(string change)
    {
        var team = await Team("Rail stale playbook team " + change);
        var playbook = await As(sp => sp.GetRequiredService<PlaybookService>().SaveAsync(new PlaybookForm
        {
            Name = "Rail stale playbook " + change, Kind = ActionKind.Block, Steps = ["Block it at the proxy", "Block it at the firewall"],
        }));
        var (number, id) = await Raise(team, "Rail stale playbook block " + change, ActionKind.Block);

        await using var rail = await RailFor(LibraryKind.Playbook, playbook, "Rail stale playbook", playbookFor: ActionKind.Block);
        rail.Component.Steps.Should().HaveCount(2);
        if (change == "step") await As(sp => sp.GetRequiredService<ActionService>().AddChecklistItemAsync(id, "Block it at the proxy"));
        else await Retitle(number);
        var before = await Trace(id);
        await rail.Click(c => c.Add());

        (await Trace(id)).Should().Be(before);
        rail.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.IsError && t.Message.Contains("Someone else changed this action"));
    }

    [Fact]
    public async Task UsedOn_ListsTheNewestUseFirst_NotTheNewestAction()
    {
        var team = await Team("Rail order team");
        var query = await Query("Rail order query");
        var (older, olderId) = await Raise(team, "Rail order older action");
        var (newer, newerId) = await Raise(team, "Rail order newer action");
        await As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().AddSavedHuntAsync(newerId, query);
            await Task.Delay(20);
            await sp.GetRequiredService<ActionService>().AddSavedHuntAsync(olderId, query);
        });

        var uses = await As(sp => sp.GetRequiredService<LibraryUseService>().UsedOnAsync(LibraryKind.SavedQuery, query));
        uses.Rows.Select(u => u.Number).Should().Equal(older, newer);
    }

    [Fact]
    public async Task CopyFilledIn_ForATemplate_CopiesTheSubjectLine_ABlankLine_AndTheBody()
    {
        var team = await Team("Rail copy team");
        var template = await As(sp => sp.GetRequiredService<OutreachTemplateService>().SaveAsync(new OutreachTemplateForm
        {
            Name = "Rail copy ask", Subject = "Asking about {{action.number}}", Body = "Hello,\nAre you affected?", Ceiling = TlpLevel.Clear,
        }));
        var (number, _) = await Raise(team, "Rail copy outreach", ActionKind.Outreach);

        await using var rail = await RailFor(LibraryKind.OutreachTemplate, template, "Rail copy ask");
        var html = WebUtility.HtmlDecode(await rail.Html());
        var copied = System.Text.RegularExpressions.Regex.Match(html, "<pre id=\"lib-try-text\"[^>]*>(.*?)</pre>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        copied.Should().StartWith($"Subject: Asking about A-{number}\n\n").And.Contain("Are you affected?");
        html.Should().Contain("data-copy-target=\"lib-try-text\"");
    }

    [Fact]
    public async Task APlaybook_IsTriedOnActionsOfItsKind_PreviewingTheStepsItWouldAdd_ThenApplied()
    {
        var team = await Team("Rail playbook team");
        var playbook = await As(sp => sp.GetRequiredService<PlaybookService>().SaveAsync(new PlaybookForm
        {
            Name = "Rail block playbook", Kind = ActionKind.Block, Steps = ["Block at the proxy", "Block at the firewall"],
        }));
        var (sweep, _) = await Raise(team, "Rail playbook sweep");
        var (number, id) = await Raise(team, "Rail playbook block", ActionKind.Block);
        var before = await Trace(id);

        await using var rail = await RailFor(LibraryKind.Playbook, playbook, "Rail block playbook", playbookFor: ActionKind.Block);
        rail.Component.Offered.Select(a => a.Number).Should().Contain(number).And.NotContain(sweep);
        rail.Component.Steps.Should().Equal("Block at the proxy", "Block at the firewall");
        (await Trace(id)).Should().Be(before, "the preview writes nothing");
        WebUtility.HtmlDecode(await rail.Html()).Should().Contain($"Add to A-{number}").And.NotContain("Copy filled in").And.NotContain("Fills in");

        await rail.Click(c => c.Add());
        (await As(sp => sp.GetRequiredService<ActionService>().GetAsync(number)))!.Action.Checklist.Select(i => i.Text)
            .Should().Contain(["Block at the proxy", "Block at the firewall"]);
        rail.Component.Steps.Should().BeEmpty("they're on its checklist now");
    }

    [Fact]
    public async Task ATemplate_IsTriedOnOutreachActions_FilledIn_ToCopy_WithNothingAdded()
    {
        var team = await Team("Rail outreach team");
        var template = await As(sp => sp.GetRequiredService<OutreachTemplateService>().SaveAsync(new OutreachTemplateForm
        {
            Name = "Rail vendor ask", Subject = "About {{action.number}}", Body = "Do you see {{indicators}}?", Ceiling = TlpLevel.Clear,
        }));
        var (number, id) = await Raise(team, "Rail outreach ask", ActionKind.Outreach);
        var before = await Trace(id);

        await using var rail = await RailFor(LibraryKind.OutreachTemplate, template, "Rail vendor ask");
        rail.Component.Offered.Should().OnlyContain(a => a.Kind == ActionKind.Outreach);
        rail.Component.Text.Should().Contain("203.0.113");
        var html = WebUtility.HtmlDecode(await rail.Html());
        html.Should().Contain($"About A-{number}").And.Contain("Copy filled in").And.Contain($"Write it on A-{number}").And.NotContain("Add to A-");
        (await Trace(id)).Should().Be(before);
    }

    [Fact]
    public async Task ARetiredEntry_IsNotTried()
    {
        var team = await Team("Rail retired team");
        var query = await Query("Rail retired query");
        await Raise(team, "Rail retired sweep");
        await As(sp => sp.GetRequiredService<SavedQueryService>().SetRetiredAsync(query, true));

        await using var rail = await RailFor(LibraryKind.SavedQuery, query, "Rail retired query");
        await rail.Click(c => c.SetParametersAsync(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Kind"] = LibraryKind.SavedQuery, ["EntryId"] = query, ["Name"] = "Rail retired query", ["Noun"] = "entry", ["Retired"] = true,
        })));
        WebUtility.HtmlDecode(await rail.Html()).Should().NotContain("Try it on an open action");
    }

    [Fact]
    public async Task JOnThePagesLastEntry_TurnsToTheNextPage_AndKOnItsFirst_BackToThePreviousOnesLast()
    {
        for (var i = 0; i < LibraryPage<SavedQueryRow>.PageSize + 2; i++) await Query($"Rail paging {i:00}");
        await using var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries");
        var first = r.Component.OnPage[0];

        await r.Click(c => { c.Turn(RowMove.NextPage); return Task.CompletedTask; });
        r.Component.Page.Should().Be(2);
        r.Component.Selected.Should().BeSameAs(r.Component.OnPage[0]);

        await r.Click(c => { c.Turn(RowMove.PreviousPage); return Task.CompletedTask; });
        r.Component.Page.Should().Be(1);
        r.Component.Selected.Should().BeSameAs(r.Component.OnPage[^1]);
        r.Component.OnPage[0].Should().BeSameAs(first);

        // The rows are drawn as every list's: one selected, aria-current, the list's one tab stop.
        var html = await r.Html();
        System.Text.RegularExpressions.Regex.Matches(html, "aria-current=\"true\"").Count.Should().Be(1);
        System.Text.RegularExpressions.Regex.Matches(html, "class=\"tide-library-row selected\"[^>]*tabindex=\"0\"").Count.Should().Be(1);
    }

    [Fact]
    public async Task ALongGroupedList_FoldsAfterTheSelectedEntrysGroup_SayingWhatsFolded()
    {
        for (var i = 0; i < 12; i++) await As(sp => sp.GetRequiredService<SavedQueryService>().SaveAsync(new SavedQueryForm
        {
            Name = $"Rail fold {i:00}", Dialect = i < 6 ? "xql" : i < 9 ? "kql" : "fql", Text = "dataset = xdr_data | filter x in ({{values}})",
        }));
        await using var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries?q=rail+fold", new() { ["Words"] = "rail fold" });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("tide-library-fold").And.Contain("1 more KQL, then Falcon · 3");
    }

    [Fact]
    public void TheLibrarysKeys_AreTheSharedRowKeys_AndTheOldScriptIsGone()
    {
        var keys = Shortcuts.For("library/queries").First(g => g.Title == "Library").Keys;
        keys.Select(k => k.Keys[0]).Should().Equal("↓", "J", "K", "Enter");
        var web = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tide.Web");
        File.Exists(Path.Combine(web, "wwwroot", "js", "library.js")).Should().BeFalse();
        File.ReadAllText(Path.Combine(web, "Components", "App.razor")).Should().NotContain("library.js").And.Contain("js/rows.js");
    }
}
