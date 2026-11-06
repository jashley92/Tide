using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// The "What each role can do" table (<see cref="Capabilities"/>) against what TIDE really enforces: each row is held to
/// the service it names, called as each role, or to the [Authorize] policy of the page it is, so a permission changed
/// without the table (or the table without the permission) fails here. docs/INSTALL.md carries the same table.
/// </summary>
public class CapabilitiesTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed record Setup(Guid Brief, Guid Article, Guid ActionId, Guid Metric, Guid Team, Guid RemovedFile);

    // Each row a service call can settle: run as a role, refused (ForbiddenException) or not.
    private static readonly Dictionary<string, Func<TestDb, Setup, Task>> Probes = new()
    {
        ["Read the company-wide archive (published briefs)"] = (db, _) => db.Distribution().PublishedAsync(),
        ["Read the team edition: briefs, the brief archive, Today"] = (db, _) => db.Briefs().ListAsync(),
        ["Add meeting notes"] = (db, s) => db.Meetings().AddNoteAsync(s.Brief, null, "A note"),
        ["Present, start and end a meeting"] = (db, s) => db.Meetings().AddAttendeeAsync(s.Brief, "Someone in the room"),
        ["Build a brief (its articles, order, team note)"] = (db, s) => db.Briefs().SetTeamNoteAsync(s.Brief, "Morning"),
        ["Reopen a brief"] = (db, s) => db.Briefs().ReopenAsync(s.Brief),
        ["Send a brief, finish one not sent, mark it posted"] = (db, _) => db.Distribution().FinishNotSentAsync(Day, "No meeting"),
        ["Read the brief rotation"] = (db, _) => db.Rotation().CalendarAsync(Day, Day),
        ["Ask for a rotation swap or cover"] = (db, _) => db.Rotation().RequestAsync(new RotationRequest { PartnerId = "S-1-5-21-OTHER", GiveFrom = Day, GiveTo = Day }),
        ["Read articles, stories and search"] = (db, _) => db.Articles().ListAsync(),
        ["Triage the inbox"] = (db, _) => db.Intake().DismissManyAsync([Guid.NewGuid()], null),
        ["Write and edit articles and stories"] = (db, _) => db.Articles().CreateAsync("Another story"),
        ["Read actions: their pages, the board, Workload"] = (db, _) => db.Actions().ListAsync(new ActionFilter()),
        ["Comment on actions"] = (db, s) => db.Actions().AddCommentAsync(s.ActionId, "A word"),
        ["Raise actions and edit every field"] = (db, s) => db.Actions().CreateAsync(new ActionForm { Title = "More", Kind = ActionKind.Other, TeamId = s.Team }),
        ["Work actions: status, outcome, assignee, checklist, hunts"] = (db, s) => db.Actions().TakeAsync(s.ActionId),
        ["Add and describe files"] = (db, s) => db.ActionFiles().AddAsync(s.ActionId, [new ActionFileUpload("a.log", Samples.Text("log"), "text/plain")]),
        ["Restore removed files"] = (db, s) => db.ActionFiles().RestoreAsync(s.RemovedFile),
        ["Read the saved hunt queries"] = (db, _) => db.SavedQueries().ListAsync(),
        ["Add indicators for an action's hunts"] = (db, s) => db.Actions().AddIndicatorsAsync(s.ActionId, [new IndicatorForm { Type = IndicatorType.Domain, Value = "evil.example" }]),
        ["Save, edit, retire and restore saved hunt queries"] = (db, _) => db.SavedQueries().SaveAsync(new SavedQueryForm
        {
            Name = "Probe " + Guid.NewGuid().ToString("N")[..6], Dialect = "kql", Text = "DeviceProcessEvents",
        }),
        ["Delete saved hunt queries"] = (db, _) => db.SavedQueries().DeleteAsync(Guid.NewGuid()),
        ["Choose my own notifications (reminders, digests, rotation emails)"] = (db, _) => db.NotificationPrefs().GetMineAsync(),
        ["Enter the day's numbers"] = (db, s) => db.Metrics().SaveValueAsync(s.Metric, Day, 3, null),
        ["Read the metrics history"] = (db, _) => db.Metrics().HistoryAsync(Day, Day),
        ["Read Our stack and adversaries"] = (db, _) => db.Stack().ListAsync(),
        ["Edit Our stack and adversaries"] = (db, _) => db.Stack().SaveAsync(new StackForm { Name = "Contoso " + Guid.NewGuid().ToString("N")[..6] }),
        ["Reports, trends and exports"] = (db, _) => db.Trends().ArticlesAsync([], new Dictionary<Guid, DateOnly>()),
    };

    // Each row a page's policy settles.
    private static readonly Dictionary<string, Type[]> Pages = new()
    {
        ["Join and follow a live meeting"] = [typeof(Present)],
        ["Raise a hand in a meeting"] = [typeof(Present)],
        ["Read actions: their pages, the board, Workload"] = [typeof(ActionPage), typeof(ActionsBoard), typeof(Workload)],
        ["Read the saved hunt queries"] = [typeof(SavedQueriesPage)],
        ["Triage the inbox"] = [typeof(Triage)],
        ["Review articles"] = [typeof(ReviewPage)],
        ["Read the team edition: briefs, the brief archive, Today"] = [typeof(BriefPage), typeof(BriefsArchive)],
        ["Read articles, stories and search"] = [typeof(Articles), typeof(Stories), typeof(Search)],
        ["Read the metrics history"] = [typeof(MetricsHistory)],
        ["Read Our stack and adversaries"] = [typeof(StackPage), typeof(AdversariesPage)],
        ["Reports, trends and exports"] = [typeof(Reports), typeof(Trends)],
        ["Coverage of detection rules"] = [typeof(CoveragePage)],
        ["Read the brief rotation"] = [typeof(RotationPage)],
        ["Choose my own notifications (reminders, digests, rotation emails)"] = [typeof(MyNotifications)],
        ["Read the company-wide archive (published briefs)"] = [typeof(Published)],
    };

    // Rows whose qualifier the probes above can't set up (own team's, own files): held to their grants' permissions only.
    private static readonly HashSet<string> GrantsOnly = ["Remove files"];

    private async Task<Setup> Seed()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var article = await _db.Articles().CreateAsync("Edge VPN zero-day");
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        var action = (await _db.Actions().GetAsync(number))!.Action.Id;
        var file = (await _db.ActionFiles().AddAsync(action, [new ActionFileUpload("old.log", Samples.Text("old"), "text/plain")])).Single().FileId;
        await _db.ActionFiles().RemoveAsync(file, "Wrong host");
        var group = await _db.Metrics().AddGroupAsync("Email");
        var metric = await _db.Metrics().SaveMetricAsync(new MetricForm { GroupId = group, Name = "Phish reported" });
        return new Setup(brief.Id, article, action, metric, team, file);
    }

    [Fact]
    public async Task EachRow_MatchesTheServiceCheck_ForEachRole()
    {
        var s = await Seed();
        var wrong = new List<string>();
        foreach (var role in Capabilities.Columns)
        foreach (var c in Capabilities.All.Where(c => Probes.ContainsKey(c.Activity)))
        {
            var cell = c.For(role);
            if (cell is { Length: > 0 }) continue;   // qualified ("own team's", "only in a live meeting"): the role tests hold those
            (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-PROBE-" + role, "Probe " + role, new HashSet<AppRole> { role });
            var refused = false;
            try { await Probes[c.Activity](_db, s); }
            catch (ForbiddenException) { refused = true; }
            catch (Exception) { /* allowed through the check, then stopped by something else: not a refusal */ }
            if (refused != (cell is null)) wrong.Add($"{role}: {c.Activity} says {Capabilities.Cell(cell)}, the service {(refused ? "refused" : "allowed")} it");
        }
        wrong.Should().BeEmpty();
    }

    [Fact]
    public void EachPageRow_NeedsWhatItsPagesPolicyRequires()
    {
        foreach (var (activity, pages) in Pages)
        {
            var row = Capabilities.All.Single(c => c.Activity == activity);
            foreach (var page in pages)
            {
                var policy = page.GetCustomAttribute<AuthorizeAttribute>()!.Policy!;
                row.Grants[0].Needs.Should().Equal([Policies.Required[policy]], $"{activity} is {page.Name}, whose policy is {policy}");
            }
        }
    }

    [Fact]
    public void EveryAdminPage_IsAdministersAndSoIsEveryAdministrationRow()
    {
        var adminPages = typeof(Present).Assembly.GetTypes()
            .Where(t => t.Namespace == "Tide.Web.Components.Pages.Admin" && t.GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>().Any()).ToList();
        adminPages.Should().NotBeEmpty();
        foreach (var page in adminPages)
            page.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(Policies.Administer, page.Name);
        foreach (var row in Capabilities.All.Where(c => c.Group == Capabilities.Administration))
            row.Grants.Should().ContainSingle().Which.Needs.Should().Equal([Permission.Administer]);
    }

    [Fact]
    public void EveryRow_IsHeldToACheckHere()
    {
        Capabilities.All.Where(c => c.Group != Capabilities.Administration && !Probes.ContainsKey(c.Activity) && !Pages.ContainsKey(c.Activity)
                                    && !GrantsOnly.Contains(c.Activity))
            .Select(c => c.Activity).Should().BeEmpty("a new row needs a probe or a page here");
        Capabilities.All.Select(c => c.Activity).Should().OnlyHaveUniqueItems();
        Capabilities.All.Select(c => c.Group).Distinct().Should().Equal(
            Capabilities.Briefs, Capabilities.Articles, Capabilities.Actions, Capabilities.Numbers, Capabilities.Stack, Capabilities.Reports, Capabilities.Administration);
    }

    [Fact]
    public void TheRolesCells_ReadAsTheRolesAreMeant()
    {
        string? Cell(string activity, AppRole role) => Capabilities.All.Single(c => c.Activity == activity).For(role);
        Cell("Work actions: status, outcome, assignee, checklist, hunts", AppRole.ActionOwner).Should().Be("own team's");
        Cell("Work actions: status, outcome, assignee, checklist, hunts", AppRole.Viewer).Should().BeNull();
        Cell("Remove files", AppRole.ActionOwner).Should().Be("own files");
        Cell("Read the team edition: briefs, the brief archive, Today", AppRole.Visitor).Should().Contain("live meeting");
        Cell("Read actions: their pages, the board, Workload", AppRole.Viewer).Should().Be("");
        Cell("Raise a hand in a meeting", AppRole.Visitor).Should().Be("");
        Cell("Add meeting notes", AppRole.Visitor).Should().BeNull();
        Cell("Add meeting notes", AppRole.Viewer).Should().Be("");
        Cell("Join and follow a live meeting", AppRole.Reader).Should().BeNull();
        Capabilities.All.Should().OnlyContain(c => c.For(AppRole.Admin) == "", "an administrator holds every permission");
    }

    [Fact]
    public void TheInstallGuidesTable_IsTheCodesTable()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Tide.sln"))) dir = Path.GetDirectoryName(dir)!;
        var doc = File.ReadAllText(Path.Combine(dir, "docs", "INSTALL.md")).ReplaceLineEndings("\n");
        const string start = "<!-- capabilities:start (generated from Capabilities.Markdown(); CapabilitiesTests checks it) -->\n";
        const string end = "<!-- capabilities:end -->";
        var from = doc.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(0);
        var to = doc.IndexOf(end, from, StringComparison.Ordinal);
        var table = doc[(from + start.Length)..to];
        // TIDE_UPDATE_DOCS=1 writes the code's table into the guide, as TIDE_UPDATE_GOLDEN does the golden files.
        if (Environment.GetEnvironmentVariable("TIDE_UPDATE_DOCS") == "1" && table != Capabilities.Markdown())
        {
            File.WriteAllText(Path.Combine(dir, "docs", "INSTALL.md"), doc[..(from + start.Length)] + Capabilities.Markdown() + doc[to..]);
            return;
        }
        table.Should().Be(Capabilities.Markdown(), "docs/INSTALL.md's table is Capabilities.Markdown(); run with TIDE_UPDATE_DOCS=1 to write it");
    }
}
