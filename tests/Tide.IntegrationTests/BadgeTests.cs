using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tide.Application.Actions;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>Every pill is one <c>tide-badge</c> in a tone, with its words: the badges on their own, and on the pages.</summary>
public class BadgeTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    [Theory]
    [InlineData(BadgeTone.Neutral, false, null, "tide-badge tide-badge-neutral")]
    [InlineData(BadgeTone.Danger, true, null, "tide-badge tide-badge-danger tide-badge-solid")]
    [InlineData(BadgeTone.Purple, false, "ms-1", "tide-badge tide-badge-purple ms-1")]
    public void Css_IsTheBadge_ItsTone_AndItsWeight(BadgeTone tone, bool solid, string? extra, string expected) =>
        Badge.Css(tone, solid, extra).Should().Be(expected);

    [Fact]
    public async Task ABadge_HasItsWords_AndAnIconOnlyBesideThem()
    {
        RenderFragment words = b => b.AddContent(0, "Set");
        var html = await Render<Badge>(new() { ["Tone"] = BadgeTone.Ok, ["Icon"] = "bi-lock-fill", ["Title"] = "Stored", ["ChildContent"] = words });
        html.Should().Be("<span class=\"tide-badge tide-badge-ok\" title=\"Stored\"><span class=\"bi bi-lock-fill\" aria-hidden=\"true\"></span>Set</span>");
    }

    [Theory]
    [InlineData(Priority.P1, "tide-badge tide-badge-danger tide-badge-solid")]
    [InlineData(Priority.P2, "tide-badge tide-badge-danger")]
    [InlineData(Priority.P4, "tide-badge tide-badge-warn")]
    public async Task APriority_IsFilledOnlyAtP1(Priority p, string css) =>
        (await Render<PriorityBadge>(new() { ["Priority"] = p }))
            .Should().Be($"<span class=\"{css}\" title=\"Priority {p}\">{p}</span>");

    [Fact]
    public async Task AThreat_CarriesItsPriority_AnInformationalDoesNot()
    {
        var threat = await Render<ClassBadge>(new() { ["Classification"] = Classification.Threat, ["Priority"] = (Priority?)Priority.P1 });
        threat.Should().Contain("tide-badge tide-badge-danger\"").And.Contain("Threat").And.Contain(">P1</span>");
        var info = await Render<ClassBadge>(new() { ["Classification"] = Classification.Informational, ["Priority"] = (Priority?)Priority.P1 });
        info.Should().Contain("tide-badge tide-badge-info\"").And.Contain("Informational").And.NotContain("P1");
    }

    [Theory]
    [InlineData("Ready", "tide-badge-ok")]
    [InlineData("Sent", "tide-badge-info")]
    [InlineData("Failed", "tide-badge-danger")]
    [InlineData("Draft", "tide-badge-neutral")]
    public async Task AStatus_KeepsItsMeaning(string status, string tone) =>
        (await Render<StatusBadge>(new() { ["Status"] = status })).Should().Be($"<span class=\"tide-badge {tone}\">{status}</span>");

    [Theory]
    [InlineData(SlaState.Overdue, "tide-badge-danger tide-badge-solid", "Overdue")]
    [InlineData(SlaState.Missed, "tide-badge-danger\"", "Missed")]
    [InlineData(SlaState.DueSoon, "tide-badge-warn", "Due soon")]
    [InlineData(SlaState.Met, "tide-badge-ok", "Met")]
    [InlineData(SlaState.NoDue, "tide-badge-neutral", "No due date")]
    public async Task AnSla_IsFilledOnlyWhenOverdue(SlaState state, string css, string text)
    {
        var html = await Render<SlaBadge>(new() { ["State"] = state, ["Due"] = (DateOnly?)new DateOnly(2026, 10, 29) });
        html.Should().Contain(css).And.Contain(text + "</span>").And.Contain("title=\"Due Thursday, October 29, 2026\"");
    }

    [Theory]
    [InlineData(ActionStatus.InProgress, "tide-badge-info", "In progress")]
    [InlineData(ActionStatus.Blocked, "tide-badge-warn", "Blocked")]
    [InlineData(ActionStatus.Done, "tide-badge-ok", "Done")]
    [InlineData(ActionStatus.Open, "tide-badge-neutral", "Open")]
    public async Task AnActionStatus_KeepsItsMeaning(ActionStatus status, string tone, string text) =>
        (await Render<ActionStatusBadge>(new() { ["Status"] = status })).Should().Be($"<span class=\"tide-badge {tone}\">{text}</span>");

    [Theory]
    [InlineData(HuntResult.Hits, false, null, "tide-badge-danger", "Hunt: hits")]
    [InlineData(HuntResult.Hits, true, 3, "tide-badge-danger", "Hits: 3")]
    [InlineData(HuntResult.Hits, true, null, "tide-badge-danger", "Hits")]
    [InlineData(HuntResult.NoHits, false, null, "tide-badge-ok", "Hunt: no hits")]
    [InlineData(HuntResult.NoHits, true, null, "tide-badge-ok", "No hits")]
    [InlineData(HuntResult.NotRun, false, null, "tide-badge-neutral", "Hunt not run")]
    [InlineData(HuntResult.NotRun, true, null, "tide-badge-neutral", "Not run yet")]
    public async Task AHunt_SaysHowItWent(HuntResult result, bool ofOne, int? count, string tone, string text) =>
        (await Render<HuntBadge>(new() { ["Result"] = (HuntResult?)result, ["OfOneHunt"] = ofOne, ["Count"] = count }))
            .Should().Be($"<span class=\"tide-badge {tone}\">{text}</span>");

    [Fact]
    public async Task NoHunt_IsNoBadge() =>
        (await Render<HuntBadge>(new() { ["Result"] = null })).Should().BeEmpty();

    [Theory]
    [InlineData(CheckLevel.Ok, "tide-badge-ok", "OK")]
    [InlineData(CheckLevel.Info, "tide-badge-info", "Note")]
    [InlineData(CheckLevel.Attention, "tide-badge-warn", "See to it")]
    [InlineData(CheckLevel.Problem, "tide-badge-danger", "Problem")]
    public async Task ACheck_HasAnIconAndItsWords(CheckLevel level, string tone, string text)
    {
        var html = await Render<CheckBadge>(new() { ["Level"] = level });
        html.Should().StartWith($"<span class=\"tide-badge {tone}\"><span class=\"bi ").And.EndWith($"{text}</span>");
    }

    [Theory]
    [InlineData(SystemSettings.SavedInApp, "tide-badge-info")]
    [InlineData("appsettings.Production.json (installer)", "tide-badge-purple")]
    [InlineData("Environment variable", "tide-badge-warn")]
    [InlineData("appsettings.json", "tide-badge-neutral")]
    public async Task ASettingSource_IsToldApartByTone(string source, string tone) =>
        (await Render<SettingSourceBadge>(new() { ["Source"] = source })).Should().Be($"<span class=\"tide-badge {tone}\">{source}</span>");

    [Theory]
    [InlineData("/actions?view=open&layout=list")]
    [InlineData("/actions?view=open&layout=board")]
    public async Task TheActions_ShowAP1AsAFilledBadge_InTableAndBoard(string path)
    {
        var html = await app.CreateClient().GetStringAsync(path);
        html.Should().Contain("<span class=\"tide-badge tide-badge-danger tide-badge-solid\" title=\"Priority P1\">P1</span>")
            .And.NotContain("tide-prio");
    }

    [Fact]
    public async Task AnAction_ShowsItsStatusAsABadge()
    {
        var html = await app.CreateClient().GetStringAsync("/actions/2");
        html.Should().Contain("<span class=\"tide-badge tide-badge-info\">In progress</span>");
    }

    [Fact]
    public async Task TheArticles_ShowAThreatWithItsPriority()
    {
        var html = await app.CreateClient().GetStringAsync("/articles");
        html.Should().Contain("tide-badge tide-badge-danger\"").And.Contain("Threat</span>").And.NotContain("tide-class");
    }

    [Fact]
    public async Task AnAdminPage_ShowsItsOneOffLabelsAsBadges()
    {
        var html = await app.CreateClient().GetStringAsync("/admin/queries");
        html.Should().Contain("<span class=\"tide-badge tide-badge-neutral\">Built-in</span>").And.NotContain("tide-status");
    }
}
