using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>Raising an article's recommended actions in the meeting: the lines, the kind each suggests, and the priority.</summary>
public class RecommendedActionTests
{
    [Theory]
    [InlineData("Patch GitLab to 17.3.2 on both instances", ActionKind.PatchRemediate)]
    [InlineData("Upgrade the VPN appliances", ActionKind.PatchRemediate)]
    [InlineData("Apply the latest update", ActionKind.PatchRemediate)]
    [InlineData("Fix the exposed bucket policy", ActionKind.PatchRemediate)]
    [InlineData("Apply the vendor hotfix to both gateways today", ActionKind.PatchRemediate)]
    [InlineData("Hunt for php-fpm spawning a shell", ActionKind.ThreatHunt)]
    [InlineData("Search logs for logins from the new ASN", ActionKind.ThreatHunt)]
    [InlineData("Sweep proxy logs for the domains", ActionKind.IocSweep)]
    [InlineData("Check for the IOCs in EDR", ActionKind.IocSweep)]
    [InlineData("Look for the indicators in DNS", ActionKind.IocSweep)]
    [InlineData("Block the C2 domains at the proxy", ActionKind.Block)]
    [InlineData("Block the indicators at the mail gateway", ActionKind.Block)]
    [InlineData("Disable legacy authentication", ActionKind.ConfigChange)]
    [InlineData("Enforce MFA on the admin portal", ActionKind.ConfigChange)]
    [InlineData("Review the config of the edge firewall", ActionKind.ConfigChange)]
    [InlineData("Notify the service desk", ActionKind.Awareness)]
    [InlineData("Send an awareness note", ActionKind.Awareness)]
    [InlineData("Warn users about the lure", ActionKind.Awareness)]
    [InlineData("Ask Okta whether we are affected", ActionKind.Outreach)]
    [InlineData("Confirm exposure with the vendor", ActionKind.Outreach)]
    [InlineData("Check with the ISAC for sightings", ActionKind.Outreach)]
    [InlineData("Reach out to the site owner about the defacement", ActionKind.Outreach)]
    [InlineData("Brief the CISO", ActionKind.Other)]
    [InlineData("", ActionKind.Other)]
    public void GuessKind_ReadsTheLinesWording(string line, ActionKind kind) => RecommendedActions.GuessKind(line).Should().Be(kind);

    [Fact]
    public void GuessKind_MatchesWordStarts_NotTheMiddleOfAWord()
    {
        RecommendedActions.GuessKind("Track the task to closure").Should().Be(ActionKind.Other, "\"task\" isn't \"ask\"");
        RecommendedActions.GuessKind("Search for the new ASN").Should().Be(ActionKind.Other, "\"search\" needs \"logs\" after it");
        RecommendedActions.GuessKind(null).Should().Be(ActionKind.Other);
    }

    [Fact]
    public void Lines_AreTheListsItems_AsPlainText_OnceEach()
    {
        RecommendedActions.Lines("- **Patch** GitLab\n* Block `bad.example`\n\n1. Notify users\n- patch gitlab\n").Should()
            .Equal("Patch GitLab", "Block bad.example", "Notify users");
        RecommendedActions.Lines(null).Should().BeEmpty();
        RecommendedActions.Lines("- " + new string('x', 400)).Single().Length.Should().Be(ActionItem.MaxTitle);
    }

    [Fact]
    public void Raised_MatchesATitle_IgnoringCaseAndSpacing()
    {
        RecommendedActions.Raised("Patch GitLab", "patch  gitlab").Should().BeTrue();
        RecommendedActions.Raised("Patch GitLab", "Patch GitLab now").Should().BeFalse();
    }

    [Theory]
    [InlineData(Priority.P1, ActionKind.IocSweep, Priority.P1)]
    [InlineData(Priority.P1, ActionKind.PatchRemediate, Priority.P1)]
    [InlineData(Priority.P1, ActionKind.Block, Priority.P1)]
    [InlineData(Priority.P1, ActionKind.Awareness, Priority.P2)]
    [InlineData(Priority.P1, ActionKind.Outreach, Priority.P2)]
    [InlineData(Priority.P1, ActionKind.Other, Priority.P2)]
    [InlineData(Priority.P2, ActionKind.Awareness, Priority.P2)]
    [InlineData(Priority.P4, ActionKind.Awareness, Priority.P4)]
    [InlineData(Priority.P3, ActionKind.IocSweep, Priority.P3)]
    public void QuickPriority_IsTheArticles_CappedAtP2ForTellingAskingAndOther(Priority article, ActionKind kind, Priority expected) =>
        ActionService.QuickPriority(article, kind).Should().Be(expected);

    [Theory]
    [InlineData(ActionKind.IocSweep, Priority.P2)]
    [InlineData(ActionKind.Block, Priority.P2)]
    [InlineData(ActionKind.PatchRemediate, Priority.P3)]
    [InlineData(ActionKind.Awareness, Priority.P3)]
    public void QuickPriority_WithoutAnArticlePriority_FallsBackToTheKind(ActionKind kind, Priority expected) =>
        ActionService.QuickPriority(null, kind).Should().Be(expected);

    private sealed record Act(string Title, string? Line);

    [Fact]
    public void EachLine_HasTheActionRaisedFromIt_ByItsStoredLine_NotItsTitle()
    {
        const string md = "- Patch GitLab on both instances\n- Hunt for php-fpm spawning a shell\n- Tell the service desk";
        var patch = new Act("Patch the two GitLab hosts", "patch gitlab on  both instances");   // its title edited since
        var byHand = new Act("Tell the service desk", null);                                   // same words, raised by hand
        var gone = new Act("Block the old C2", "Block the C2 at the proxy");                    // its line removed since
        var again = new Act("Patch GitLab again", "Patch GitLab on both instances");            // the line's second action

        var (lines, others) = RecommendedActions.Match(md, [patch, byHand, gone, again], r => r.Line);

        lines.Select(l => l.Line).Should().Equal("Patch GitLab on both instances", "Hunt for php-fpm spawning a shell", "Tell the service desk");
        lines.Select(l => l.Action).Should().Equal(patch, null, null);
        others.Should().Equal(byHand, gone, again);
        RecommendedActions.Unraised(md, [patch, byHand, gone, again], r => r.Line)
            .Should().Equal("Hunt for php-fpm spawning a shell", "Tell the service desk");
    }

    [Fact]
    public void NoRecommendations_LeaveEveryActionAmongTheOthers()
    {
        var (lines, others) = RecommendedActions.Match(null, [new Act("Sweep", "Sweep the logs")], r => r.Line);
        lines.Should().BeEmpty();
        others.Should().ContainSingle();
    }
}

/// <summary>What an action can close with, by kind, and how late an overdue one is.</summary>
public class ActionOutcomeTests
{
    [Fact]
    public void Outcomes_FollowTheKind_AlwaysWithOther()
    {
        ActionService.OutcomesFor(ActionKind.ThreatHunt, ActionStatus.Done).Should().Equal(ActionOutcome.NoHits, ActionOutcome.HitsEscalated, ActionOutcome.Other);
        ActionService.OutcomesFor(ActionKind.PatchRemediate, ActionStatus.Done)
            .Should().Equal(ActionOutcome.Remediated, ActionOutcome.NotAffected, ActionOutcome.RiskAccepted, ActionOutcome.Other);
        ActionService.OutcomesFor(ActionKind.ConfigChange, ActionStatus.Done).Should().Equal(ActionService.OutcomesFor(ActionKind.PatchRemediate, ActionStatus.Done));
        ActionService.OutcomesFor(ActionKind.Awareness, ActionStatus.Done).Should().Equal(ActionOutcome.Remediated, ActionOutcome.NotAffected, ActionOutcome.Other);
        foreach (var kind in Enum.GetValues<ActionKind>())
            ActionService.KindOutcomes(kind).Should().EndWith(ActionOutcome.Other).And.NotContain(ActionOutcome.None);
        ActionService.OutcomesFor(ActionKind.Other, ActionStatus.Done).Should().HaveCount(Enum.GetValues<ActionOutcome>().Length - 1);
    }

    [Fact]
    public void Outcomes_KeepTheCurrentOne_AndCancellingMayHaveNone()
    {
        ActionService.OutcomesFor(ActionKind.PatchRemediate, ActionStatus.Done, ActionOutcome.Answered)
            .Should().StartWith(ActionOutcome.Answered, "an action closed before the choices narrowed keeps its outcome");
        ActionService.OutcomesFor(ActionKind.PatchRemediate, ActionStatus.Done, ActionOutcome.Remediated).Should().HaveCount(4);
        ActionService.OutcomesFor(ActionKind.Block, ActionStatus.Cancelled).Should().StartWith(ActionOutcome.None).And.Contain(ActionOutcome.Blocked);
        ActionService.OutcomesFor(ActionKind.Block, ActionStatus.InProgress).Should().Equal(ActionOutcome.None);
    }

    [Fact]
    public void Late_CountsCalendarDaysPastDue()
    {
        var due = new DateOnly(2026, 10, 27);
        ActionSla.Late(due, new DateOnly(2026, 10, 30)).Should().Be(3);
        ActionSla.Late(due, due).Should().Be(0);
        ActionSla.Late(due, new DateOnly(2026, 10, 26)).Should().Be(0);
        ActionSla.Late(null, due).Should().Be(0);
        ActionSla.Late(due, null).Should().Be(0);
    }
}

/// <summary>What a generated hunt says about itself, for closing a clean sweep: its window and what it left out.</summary>
public class HuntQueryFactsTests
{
    [Fact]
    public void EveryBuiltInTemplate_InEveryLanguage_EnforcesTheWindow()
    {
        foreach (var d in QueryDialects.All)
            foreach (var (kind, template) in d.Defaults)
                template.Should().Contain(QueryDialects.DaysPlaceholder, $"{d.Name}'s {kind} query looks back over the window it claims");
    }

    [Fact]
    public void WindowAndLeftOut_ReadFromTheGeneratedQuery()
    {
        ArticleIndicator[] values = [new() { Type = IndicatorType.IpAddress, Value = "203.0.113.5" }, new() { Type = IndicatorType.Url, Value = "https://evil.example/a" }];
        var kql = HuntQueryBuilder.Build(QueryDialects.Find("kql")!, values, days: 45);
        HuntQueryBuilder.WindowOf(kql).Should().Be(45, "every KQL block sets its window");
        var q = HuntQueryBuilder.Build(QueryDialects.Find("fql")!, values, days: 45);
        HuntQueryBuilder.WindowOf(q).Should().Be(45, "Falcon's queries set their window too");
        q.Should().StartWith("// Generated by TIDE from 2 indicator(s), looking back 45 days.").And.Contain("setTimeInterval(start=45d)");
        HuntQueryBuilder.LeftOut(q).Should().Be(1, "CrowdStrike has no URL template");
        var xql = HuntQueryBuilder.Build(QueryDialects.Find("xql")!, values, days: 45);
        HuntQueryBuilder.WindowOf(xql).Should().Be(45, "every XQL block sets its window");
        xql.Should().Contain("config timeframe = 45d");

        // A template an administrator saved without the window claims none: the number recorded is the window enforced.
        var saved = new Dictionary<HuntValueKind, string> { [HuntValueKind.Ipv4] = "dataset = xdr_data | filter action_remote_ip in ({{values}})" };
        HuntQueryBuilder.WindowOf(HuntQueryBuilder.Build(QueryDialects.Find("xql")!, values.Take(1), saved, days: 45)).Should().BeNull();

        HuntQueryBuilder.WindowOf("// Generated by TIDE from 2 indicator(s). Check the dataset").Should().BeNull("an older query doesn't say");
        HuntQueryBuilder.WindowOf("").Should().BeNull();
        HuntQueryBuilder.LeftOut("// a: x; 2 value(s) not included.\n// b: y; 3 value(s) not included.").Should().Be(5);
        HuntQueryBuilder.LeftOut(null).Should().Be(0);
    }
}

/// <summary>The brief page header's one primary button.</summary>
public class BriefNextTests
{
    [Theory]
    [InlineData(BriefStatus.Draft, 3, BriefNext.MarkReady)]
    [InlineData(BriefStatus.Draft, 0, BriefNext.None)]
    [InlineData(BriefStatus.Ready, 3, BriefNext.Present)]
    [InlineData(BriefStatus.Presented, 3, BriefNext.Send)]
    [InlineData(BriefStatus.Sent, 3, BriefNext.ViewAsSent)]
    public void TheHeadersPrimaryButton_FollowsTheBrief(BriefStatus status, int articles, BriefNext next) =>
        BriefSteps.Next(status, articles).Should().Be(next);

}
