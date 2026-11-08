using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 2 on the day's numbers form (MetricEntry): what else can arrive while a save is still going on a database
/// that answers a moment later (as SQL Server does): the box's note, a chip's jump, a colleague's change, the next box
/// being typed in, and the brief being sent.
/// </summary>
public class EntryRound2BugTests(SlowDatabaseTideApp app) : IClassFixture<SlowDatabaseTideApp>
{
    private static readonly ComponentHarness.SignIn AnalystOnly = new("dev:analyst2", "Ana", "Analyst");

    private sealed record Made(Guid Group, Guid[] Metrics);

    private async Task<Made> Group(string name, params string[] metrics)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
        var group = await svc.AddGroupAsync(name);
        var ids = new List<Guid>();
        foreach (var m in metrics) ids.Add(await svc.SaveMetricAsync(new MetricForm { GroupId = group, Name = m }));
        return new Made(group, [.. ids]);
    }

    private async Task<MetricTile> Tile(Guid id, DateOnly day)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var d = await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day);
        return d.Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == id);
    }

    // A colleague's save, as their own page would make it.
    private async Task Colleague(Guid id, DateOnly day, decimal? value, string? note = null)
    {
        await using var scope = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:robin", "Robin", "Analyst"));
        await scope.ServiceProvider.GetRequiredService<MetricsService>().SaveValueAsync(id, day, value, note);
    }

    private static IReadOnlyList<Change> ByRobin(DateOnly day) => [new Change(ChangeFeed.MetricsDay(day), "dev:robin", "Robin", Guid.NewGuid())];

    private static string Row(string html, Guid metric)
    {
        var at = html.IndexOf("id=\"metric-" + metric.ToString("N") + "\"", StringComparison.Ordinal);
        at.Should().BePositive();
        var end = html.IndexOf("tide-metric-row", at, StringComparison.Ordinal);
        return end < 0 ? html[at..] : html[at..end];
    }

    private static List<ToastService.Toast> Errors<T>(ComponentHarness.Rendered<T> r) =>
        [.. r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Where(t => t.IsError)];

    [Fact]
    public async Task ANoteTyped_WhileItsFirstValueIsStillSaving_IsSavedWithIt()
    {
        var day = new DateOnly(2027, 6, 7);
        var g = await Group("Phishing reports", "Reported");
        var tile = await Tile(g.Metrics[0], day);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        // Type the value, tab to its note, type the note and move on, all before the value has saved.
        await r.Click(async c =>
        {
            c.Entering(tile.Metric.Id, false);
            var value = c.SaveValue(tile, "12");
            c.Entering(tile.Metric.Id, true);
            var note = c.SaveNote(tile, "a campaign");
            await Task.WhenAll(value, note);
        });

        var saved = await Tile(g.Metrics[0], day);
        saved.Value.Should().Be(12);
        saved.Note.Should().Be("a campaign", "the note was typed for the value being saved");
        Errors(r).Should().BeEmpty();
    }

    [Fact]
    public async Task ANoteSaved_WhileANewValueIsStillSaving_DoesntPutTheOldValueBack()
    {
        var day = new DateOnly(2027, 6, 8);
        var g = await Group("Web filter", "Blocked", "Allowed");
        await Colleague(g.Metrics[0], day, 3);
        var tile = await Tile(g.Metrics[0], day);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        await r.Click(async c =>
        {
            var value = c.SaveValue(tile, "9");
            var note = c.SaveNote(tile, "new rule");
            await Task.WhenAll(value, note);
        });

        var saved = await Tile(g.Metrics[0], day);
        saved.Value.Should().Be(9);
        saved.Note.Should().Be("new rule");
        Row(await r.Html(), g.Metrics[0]).Should().Contain("value=\"9\"");
    }

    [Fact]
    public async Task AColleaguesChange_HeardWhileASaveIsGoing_NeitherHidesTheSave_NorBlamesThemForIt()
    {
        var day = new DateOnly(2027, 6, 9);
        var g = await Group("Endpoint alerts", "Isolated", "Cleaned");
        var mine = await Tile(g.Metrics[0], day);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });
        await Colleague(g.Metrics[1], day, 2);

        // Robin's change to the other box is heard just as this form saves its own and moves on to its note.
        await r.Click(async c =>
        {
            c.Entering(mine.Metric.Id, false);
            var saving = c.SaveValue(mine, "4");
            c.Entering(mine.Metric.Id, true);
            await Task.WhenAll(saving, c.OnChangedElsewhere(ByRobin(day)));
        });

        var html = await r.Html();
        Row(html, g.Metrics[0]).Should().Contain("value=\"4\"", "this form's own save is the newest").And.NotContain("while you were editing", "Robin didn't change it");
        Row(html, g.Metrics[1]).Should().Contain("value=\"2\"");
    }

    [Fact]
    public async Task AChipClicked_WhileAValueIsStillSaving_JumpsPastThatBox()
    {
        var day = new DateOnly(2027, 6, 10);
        var g = await Group("Vulnerability scans", "Critical", "High");
        var first = await Tile(g.Metrics[0], day);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        await r.Click(async c =>
        {
            c.Entering(first.Metric.Id, false);
            var saving = c.SaveValue(first, "1");
            await Task.WhenAll(saving, c.Jump(g.Group));
        });

        r.Component.FocusNext.Should().Be(g.Metrics[1], "the first box has its value: the next one is the empty one");
    }

    [Fact]
    public async Task TheNextBoxBeingTypedIn_KeepsItsText_WhenTheSaveBringsInAColleaguesChangeToIt()
    {
        var day = new DateOnly(2027, 6, 11);
        var g = await Group("Identity", "Lockouts", "Resets");
        var first = await Tile(g.Metrics[0], day);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        // Robin enters Resets a moment before this form saves Lockouts and moves on to Resets; their change isn't heard
        // yet (the page hears changes after a pause), but this form's re-read after its save brings it in.
        await Colleague(g.Metrics[1], day, 8);
        await r.Click(async c =>
        {
            c.Entering(first.Metric.Id, false);
            var saving = c.SaveValue(first, "5");
            c.Entering(g.Metrics[1], false);
            await saving;
        });
        Row(await r.Html(), g.Metrics[1]).Should().Contain("value=\"\"", "what's being typed there isn't replaced");

        await r.Click(c => c.OnChangedElsewhere(ByRobin(day)));
        Row(await r.Html(), g.Metrics[1]).Should().Contain("Robin changed Resets while you were editing it");
    }

    [Fact]
    public async Task ABoxBeingTypedIn_WhenTheBriefIsSent_IsntStillHeldOnceTheBriefIsReopened()
    {
        var day = new DateOnly(2027, 6, 14);
        var g = await Group("Mail flow", "Quarantined");
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day }, AnalystOnly);
        await r.Click(c => { c.Entering(g.Metrics[0], false); return Task.CompletedTask; });

        // The brief is sent: the page re-renders the form, and the boxes go (the browser says nothing of the one focused).
        var brief = await SetBrief(day, BriefStatus.Sent, null);
        await r.Click(c => Rerender(c, day));
        (await r.Html()).Should().NotContain("id=\"metric-" + g.Metrics[0].ToString("N") + "\"");

        // Reopened, and Robin enters the number: nobody is typing in that box now.
        await SetBrief(day, BriefStatus.Draft, brief);
        await r.Click(c => Rerender(c, day));
        await Colleague(g.Metrics[0], day, 6);
        await r.Click(c => c.OnChangedElsewhere(ByRobin(day)));

        Row(await r.Html(), g.Metrics[0]).Should().Contain("value=\"6\"").And.NotContain("while you were editing");
    }

    [Fact]
    public async Task ADayFinishedAsNotSent_SaysSo_RatherThanThatItsNumbersWentOut()
    {
        var notSent = new DateOnly(2027, 6, 15);
        var sent = new DateOnly(2027, 6, 16);
        await Group("Firewall changes", "Approved");
        var finished = await SetBrief(notSent, BriefStatus.Sent, null);
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync())
        {
            var brief = db.Briefs.Single(b => b.Id == finished);
            (brief.NotSentAtUtc, brief.NotSentByName, brief.NotSentReason) = (DateTimeOffset.UtcNow, "Ana", "Relay down");
            await db.SaveChangesAsync();
        }
        await SetBrief(sent, BriefStatus.Sent, null);

        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = notSent }, AnalystOnly);
        (await r.Html()).Should().Contain("finished without sending, so its numbers are fixed.").And.Contain("An administrator can correct them.")
            .And.NotContain("numbers that went out");
        await using var s = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = sent }, AnalystOnly);
        (await s.Html()).Should().Contain("these are the numbers that went out").And.NotContain("without sending");
    }

    [Fact]
    public async Task ANumberSavedJustAfterTheBriefWentOut_IsNamedInAToast_WithWhatWasTyped()
    {
        var day = new DateOnly(2027, 6, 21);
        var g = await Group("Proxy blocks", "Blocked", "Allowed");
        var blocked = await Tile(g.Metrics[0], day);
        var allowed = await Tile(g.Metrics[1], day);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day }, AnalystOnly);
        await r.Click(c => c.SaveValue(allowed, "lots"));   // not a number: held in its box
        var errorsBefore = Errors(r).Count;

        await SetBrief(day, BriefStatus.Sent, null);
        await r.Click(c => c.SaveValue(blocked, "12"));

        Errors(r).Skip(errorsBefore).Select(t => t.Message).Should().BeEquivalentTo(
            [MetricEntry.LostMessage("Blocked", "12", false), MetricEntry.LostMessage("Allowed", "lots", false)]);
        Errors(r).Should().NotContain(t => t.Message.Contains("numbers are fixed"), "the refused save isn't reported twice");
        (await r.Html()).Should().Contain("these are the numbers that went out");
        (await Tile(g.Metrics[0], day)).Value.Should().BeNull();
    }

    [Fact]
    public async Task ABoxHoldingWhatIsntANumber_WhenTheBriefIsFinishedAsNotSent_IsNamedOnce()
    {
        var day = new DateOnly(2027, 6, 22);
        var g = await Group("Badge readers", "Denied");
        var tile = await Tile(g.Metrics[0], day);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day }, AnalystOnly);
        await r.Click(c => c.SaveValue(tile, "a few"));
        var errorsBefore = Errors(r).Count;

        var brief = await SetBrief(day, BriefStatus.Sent, null);
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync())
        {
            db.Briefs.Single(b => b.Id == brief).NotSentAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await r.Click(c => Rerender(c, day));
        await r.Click(c => Rerender(c, day));

        Errors(r).Skip(errorsBefore).Select(t => t.Message).Should().Equal(MetricEntry.LostMessage("Denied", "a few", true));
        MetricEntry.LostMessage("Denied", "", true).Should().Be(
            "Denied wasn't saved: the brief was finished as Not sent while you were typing it (you'd cleared it). An administrator can correct it.");
    }

    private static Task Rerender(MetricEntry c, DateOnly day) =>
        c.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Date"] = day }));

    private async Task<Guid> SetBrief(DateOnly day, BriefStatus status, Guid? id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        Brief brief;
        if (id is { } existing) brief = db.Briefs.Single(b => b.Id == existing);
        else db.Briefs.Add(brief = new Brief { BriefDate = day, AnalystId = "x", AnalystName = "x" });
        brief.Status = status;
        await db.SaveChangesAsync();
        return brief.Id;
    }
}
