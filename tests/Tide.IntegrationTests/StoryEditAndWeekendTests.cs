using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Intake;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.TestSupport;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>A story's Edit form, changed and not saved, isn't lost to a stray link: leaving asks first, as the article
/// editor and the brief do.</summary>
public class StoryEditGuardTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Rendered<Stories>> Editing()
    {
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<StoryService>().CreateAsync("Guarded story " + Guid.NewGuid().ToString("N")[..6], "As saved.");
        var r = await RenderAsync<Stories>(app, $"/stories/{id}", new() { ["Id"] = (Guid?)id });
        await r.Click(c => { c.StartEdit(); return Task.CompletedTask; });
        return r;
    }

    // Typing in the Name box, as its input event would: the value, then the page drawn again.
    private static Task Type(Rendered<Stories> r, string name) => r.Click(c =>
    {
        c.TypedName = name;
        typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(c, null);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task TheEditForm_Unchanged_LeavesWithoutAsking()
    {
        await using var r = await Editing();
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Guard.MayLeave())).Should().BeTrue();
        (await r.Html()).Should().NotContain("Leave with unsaved changes?");
    }

    [Fact]
    public async Task TheEditForm_Changed_AsksBeforeLeaving_AndDiscardClosesIt()
    {
        await using var r = await Editing();
        await Type(r, "A name being typed");
        r.Component.EditUnsaved.Should().BeTrue();

        Task<bool>? leaving = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { leaving = (Task<bool>)r.Pressed(c => c.Guard.MayLeave()); });
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("Leave with unsaved changes?"));
        html.Should().Contain("You've changed this story and not saved it.").And.Contain(">Save and leave</button>");
        leaving!.IsCompleted.Should().BeFalse("the move waits for an answer");

        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Guard.Leave.Close(LeaveDialog.Choice.Discard));
        (await leaving).Should().BeTrue();
        r.Component.EditUnsaved.Should().BeFalse("what was typed is dropped");
        r.Component.Detail!.Story.Name.Should().StartWith("Guarded story", "nothing was saved");
    }

    [Fact]
    public async Task SaveAndLeave_SavesTheStoryFirst()
    {
        await using var r = await Editing();
        await Type(r, "Saved on the way out");

        Task<bool>? leaving = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { leaving = (Task<bool>)r.Pressed(c => c.Guard.MayLeave()); });
        await r.HtmlOnce("Leave with unsaved changes?");
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Guard.Leave.Close(LeaveDialog.Choice.Save));

        (await leaving!).Should().BeTrue();
        await using var scope = SignedIn(app, SignIn.Analyst);
        (await scope.ServiceProvider.GetRequiredService<StoryService>().GetAsync(r.Component.Detail!.Story.Id))!.Story.Name.Should().Be("Saved on the way out");
    }
}

/// <summary>The app on a Saturday (the coming one, 11:00 New York time, running on in real time): today's brief is the
/// next working day's.</summary>
public class WeekendApp : TideApp
{
    protected override TimeProvider? Time { get; } = Saturday();

    private static ShiftedClock Saturday()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = TimeProvider.System.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var local = today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7).ToDateTime(new TimeOnly(11, 0));
        return new ShiftedClock(new DateTimeOffset(local, zone.GetUtcOffset(local)) - now);
    }
}

/// <summary>On a weekend, the lookup and the article editor agree on which brief is today's.</summary>
public class WeekendTodayTests(WeekendApp app) : IClassFixture<WeekendApp>
{
    [Fact]
    public async Task OnAWeekend_TheLookupCallsTodaysBrief_WhatTheEditorDoes()
    {
        var clock = app.Services.GetRequiredService<OrgClock>();
        clock.CurrentBriefDate.Should().NotBe(clock.Today, "it's a weekend: today's brief is the next working day's");
        var domain = $"weekend-{Guid.NewGuid():N}"[..20] + ".example.net";
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            id = await articles.CreateAsync("On the weekend's brief", "Vendor", null, TlpLevel.Amber);
            var form = ArticleForm.From((await articles.GetAsync(id))!);
            form.Bluf = "Summary.";
            form.Indicators = [new IndicatorForm { Type = IndicatorType.Domain, Value = domain }];
            await articles.SaveAsync(form);
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            await briefs.AddArticleAsync((await briefs.GetOrStartAsync(clock.CurrentBriefDate)).Id, id);
        }

        await using (var editor = await RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id }))
            (await editor.HtmlOnce("On today's brief")).Should().Contain("On today's brief");

        await using var lookup = await RenderAsync<Search>(app, "/search?q=" + Uri.EscapeDataString(domain));
        WebUtility.HtmlDecode(await lookup.Html()).Should().Contain("today's brief</a>, not presented yet")
            .And.Contain($"href=\"briefs/{clock.CurrentBriefDate:yyyy-MM-dd}\"");
    }
}
