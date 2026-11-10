using AwesomeAssertions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>Today's step strip (BriefSteps), its edition check (EditionChecks) and a dropped row (BriefOrder).</summary>
public class BriefProgressTests
{
    private static Article Ready(string title, TlpLevel tlp = TlpLevel.Clear)
    {
        var a = new Article { Title = title, Tlp = tlp, Bluf = "x", Classification = Classification.Informational };
        a.Bullets.Add(new ArticleBullet { Text = "a bullet", Tlp = tlp });
        return a;
    }

    private static Article NoBluf(string title) => new() { Title = title, Classification = Classification.Informational };

    private static Brief BriefWith(BriefStatus status, params Article[] articles)
    {
        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 29), Status = status };
        for (var i = 0; i < articles.Length; i++)
            brief.Items.Add(new BriefItem { ArticleId = articles[i].Id, Article = articles[i], SortOrder = i });
        return brief;
    }

    private static (StepState State, string Detail) Step(IReadOnlyList<BriefStep> steps, BriefStepKind kind)
    {
        var step = steps.Single(s => s.Kind == kind);
        return (step.State, step.Detail);
    }

    [Fact]
    public void ADraftWithGaps_IsOnWrite_WithTheFeedAndCountsInWords()
    {
        var steps = BriefSteps.For(BriefWith(BriefStatus.Draft, Ready("a"), NoBluf("b"), NoBluf("c")), 4, 2, "10:30 AM");

        steps.Select(s => s.Label).Should().Equal("Triage", "Write", "Order", "Present", "Send");
        Step(steps, BriefStepKind.Triage).Should().Be((StepState.Attention, "4 new in the feed"));
        Step(steps, BriefStepKind.Write).Should().Be((StepState.Current, "2 of 3 need work"));
        Step(steps, BriefStepKind.Order).Should().Be((StepState.Todo, "Not marked ready · 2 numbers missing"));
        Step(steps, BriefStepKind.Present).Should().Be((StepState.Todo, "Meets 10:30 AM"));
        Step(steps, BriefStepKind.Send).Should().Be((StepState.Todo, "2 editions"));
        steps.Count(s => s.State == StepState.Current).Should().Be(1);
    }

    [Fact]
    public void EverythingWritten_MovesOnToOrder_AndThenPresent()
    {
        var draft = BriefSteps.For(BriefWith(BriefStatus.Draft, Ready("a")), 0, 0, "9:00 AM");
        Step(draft, BriefStepKind.Triage).Should().Be((StepState.Done, "Feed clear"));
        Step(draft, BriefStepKind.Write).Should().Be((StepState.Done, "1 of 1 ready"));
        Step(draft, BriefStepKind.Order).State.Should().Be(StepState.Current);

        var ready = BriefSteps.For(BriefWith(BriefStatus.Ready, Ready("a")), null, 1, "9:00 AM");
        Step(ready, BriefStepKind.Triage).Should().Be((StepState.Todo, "Sorting the feed"), "someone who doesn't triage gets no count");
        Step(ready, BriefStepKind.Order).Should().Be((StepState.Attention, "Marked ready · 1 number missing"));
        Step(ready, BriefStepKind.Present).State.Should().Be(StepState.Current);
    }

    [Fact]
    public void AnEmptyBrief_IsOnWrite_AndASentOne_IsDone()
    {
        Step(BriefSteps.For(BriefWith(BriefStatus.Draft), 0, 0, "9:00 AM"), BriefStepKind.Write)
            .Should().Be((StepState.Current, "No articles yet"));

        var sent = BriefWith(BriefStatus.Sent, Ready("a"), Ready("b"));
        sent.Items.First().Discussed = true;
        var steps = BriefSteps.For(sent, 0, 3, "9:00 AM");
        Step(steps, BriefStepKind.Order).State.Should().Be(StepState.Done, "numbers missing after the meeting no longer hold the brief up");
        Step(steps, BriefStepKind.Present).Should().Be((StepState.Done, "1 of 2 discussed"));
        Step(steps, BriefStepKind.Send).Should().Be((StepState.Done, "Sent"));
        steps.Should().NotContain(s => s.State == StepState.Current);
    }

    private static BriefSend Sent(Edition edition, SendStatus status, int minutes, bool test = false) =>
        new() { Edition = edition, Status = status, IsTest = test, SentAtUtc = new DateTimeOffset(2026, 10, 29, 14, minutes, 0, TimeSpan.Zero) };

    private static (StepState State, string Detail) SendStep(BriefStatus status, params BriefSend[] sends) =>
        Step(BriefSteps.For(BriefWith(status, Ready("a")), 0, 0, "9:00 AM", sends), BriefStepKind.Send);

    [Fact]
    public void TheSendStep_ReadsTheSendLog()
    {
        SendStep(BriefStatus.Presented).Should().Be((StepState.Current, "2 editions"));
        SendStep(BriefStatus.Presented, Sent(Edition.Team, SendStatus.Sent, 1, test: true))
            .Should().Be((StepState.Current, "Test sent · 2 editions"), "a test copy doesn't count as sent");
        SendStep(BriefStatus.Sent, Sent(Edition.Team, SendStatus.Sent, 2))
            .Should().Be((StepState.Current, "1 of 2 sent"), "the other edition is still to go");
        SendStep(BriefStatus.Sent, Sent(Edition.Team, SendStatus.Sent, 2), Sent(Edition.Public, SendStatus.Sent, 3))
            .Should().Be((StepState.Done, "Sent"));
        SendStep(BriefStatus.Presented, Sent(Edition.Public, SendStatus.Failed, 4)).Should().Be((StepState.Attention, "Last send failed"));
        SendStep(BriefStatus.Sent, Sent(Edition.Team, SendStatus.Sent, 2), Sent(Edition.Public, SendStatus.Unknown, 5))
            .Should().Be((StepState.Attention, "May have gone: check"));
        SendStep(BriefStatus.Presented, new BriefSend { Edition = Edition.Team, Status = SendStatus.Sending, SentAtUtc = DateTimeOffset.UtcNow })
            .Should().Be((StepState.Current, "Sending…"));
        SendStep(BriefStatus.Presented, Sent(Edition.Team, SendStatus.Sending, 6))
            .Should().Be((StepState.Attention, "May have gone: check"), "one left sending past the in-flight time never finishes");
        SendStep(BriefStatus.Sent).Should().Be((StepState.Done, "Sent"), "a brief marked sent before the log was kept");
    }

    [Fact]
    public void TheSendStep_DoesntWaitForAnEditionWithNothingInIt()
    {
        var brief = BriefWith(BriefStatus.Sent, Ready("a"));
        BriefSteps.Send(brief, [Sent(Edition.Team, SendStatus.Sent, 2)], [Edition.Public])
            .Should().Be(new BriefStep(BriefStepKind.Send, "Send", "Sent · nothing in the other edition", StepState.Done));
        BriefSteps.Send(brief, [Sent(Edition.Team, SendStatus.Sent, 2)], []).Detail.Should().Be("1 of 2 sent");
    }

    [Fact]
    public void AGapNoticedAfterMarkingReady_NeedsAttention()
    {
        Step(BriefSteps.For(BriefWith(BriefStatus.Ready, NoBluf("b")), 0, 0, "9:00 AM"), BriefStepKind.Write)
            .Should().Be((StepState.Attention, "1 of 1 need work"));
    }

    [Fact]
    public void EditionCheck_SaysWhatEachEditionCarries_FromTheEditionsOwnCut()
    {
        var open = Ready("open");
        var mixed = Ready("mixed");
        mixed.Bullets.Add(new ArticleBullet { Text = "isac only", Tlp = TlpLevel.Amber, SortOrder = 1 });
        var amber = Ready("amber", TlpLevel.Amber);
        var brief = BriefWith(BriefStatus.Draft, open, mixed, amber);
        brief.Intro = "A quiet day.";
        brief.IntroTlp = TlpLevel.Amber;

        var team = EditionChecks.Rows(brief, Edition.Team, TlpLevel.AmberStrict, hasNumbers: true, missingNumbers: 1);
        team.Select(r => (r.Title, r.Fit)).Should().Equal(
            ("Title slide line", EditionFit.Included), ("Today's numbers", EditionFit.Part),
            ("open", EditionFit.Included), ("mixed", EditionFit.Included), ("amber", EditionFit.Included));
        team[1].Why.Should().Be("1 number not entered");

        var company = EditionChecks.Rows(brief, Edition.Public, TlpLevel.Green, hasNumbers: true);
        company.Select(r => (r.Title, r.Fit, r.Why)).Should().Equal(
            ("Title slide line", EditionFit.LeftOut, "The line is TLP:AMBER"),
            ("open", EditionFit.Included, null),
            ("mixed", EditionFit.Part, "1 part above TLP:GREEN left out"),
            ("amber", EditionFit.LeftOut, "The article is TLP:AMBER"));
        company.Where(r => r.ArticleId is not null).Select(r => r.ArticleId).Should().Equal(open.Id, mixed.Id, amber.Id);
    }

    [Fact]
    public void EditionCheck_LeavesOutWhatIsntThere()
    {
        var rows = EditionChecks.Rows(BriefWith(BriefStatus.Draft, Ready("only")), Edition.Team, TlpLevel.AmberStrict);
        rows.Should().ContainSingle().Which.Should().Be(new EditionCheckRow(rows[0].ArticleId, "only", EditionFit.Included));
        EditionChecks.Rows(BriefWith(BriefStatus.Draft), Edition.Team, TlpLevel.AmberStrict, hasNumbers: true)
            .Should().ContainSingle().Which.Fit.Should().Be(EditionFit.Included, "all the numbers are in");
    }

    // A threat with nothing else missing, whose "Why it matters" is marked as given.
    private static Article Threat(TlpLevel tlp = TlpLevel.Clear, TlpLevel why = TlpLevel.Clear, string? whyText = "We run two of these.")
    {
        var a = Ready("threat", tlp);
        a.Classification = Classification.Threat;
        a.Priority = Priority.P2;
        a.RecommendedActions = "Patch.";
        a.SourceName = "Vendor";
        a.Tags.Add(new ArticleTag { TagId = Guid.NewGuid() });
        a.WhyItMatters = whyText;
        a.WhyItMattersTlp = why;
        return a;
    }

    [Fact]
    public void WhyItMatters_ThatReachesTheCompanyWideEdition_IsWarnedAbout_WithAFix()
    {
        var check = ArticleChecks.For(Threat(), TlpLevel.Green).Should().ContainSingle().Subject;
        check.Message.Should().Be("Why it matters is TLP:CLEAR and will reach the company-wide edition.");
        check.Blocking.Should().BeFalse("it's a warning: the writer may mean it to go to everyone");
        check.Part.Should().Be(ArticlePart.WhyItMatters);
        check.Short.Should().Be("Why it matters TLP");
        check.Fix.Should().Be(ArticleFix.WhyItMattersAmber);

        ArticleChecks.WhyReachesCompany(Threat(why: TlpLevel.Green), TlpLevel.Green)!.Message
            .Should().Be("Why it matters is TLP:GREEN and will reach the company-wide edition.", "at the ceiling is still carried");
        ArticleChecks.WhyReachesCompany(Threat(TlpLevel.Green, TlpLevel.Clear), TlpLevel.Green)!.Message
            .Should().Contain("TLP:GREEN", "a part is never below its article: the article's TLP is what counts");
        ArticleChecks.WhyReachesCompany(Threat(why: TlpLevel.Green), TlpLevel.Amber)!.Fix
            .Should().BeNull("marking it AMBER wouldn't leave it out of an edition that carries AMBER");
    }

    [Fact]
    public void WhyItMatters_IsntWarnedAbout_WhenTheCompanyWideEditionLeavesItOut_OrItIsntAThreat()
    {
        ArticleChecks.For(Threat(), companyCeiling: null).Should().BeEmpty("without the ceiling the check isn't run");
        ArticleChecks.For(Threat(why: TlpLevel.Amber), TlpLevel.Green).Should().BeEmpty("AMBER is above the company-wide ceiling");
        ArticleChecks.For(Threat(TlpLevel.Amber), TlpLevel.Green).Should().BeEmpty("the whole article is left out");
        ArticleChecks.For(Threat(whyText: "  "), TlpLevel.Green).Should().BeEmpty("there's nothing to reach anyone");
        var info = Threat();
        info.Classification = Classification.Informational;
        info.Priority = null;
        ArticleChecks.For(info, TlpLevel.Green).Should().BeEmpty("informational articles are written for everyone");
    }

    [Fact]
    public void EditionCheck_WarnsOnTheCompanyWideEdition_WhenItCarriesAThreatsWhyItMatters()
    {
        var brief = BriefWith(BriefStatus.Draft, Threat(), Threat(why: TlpLevel.Amber));
        EditionChecks.Rows(brief, Edition.Public, TlpLevel.Green).Select(r => (r.Fit, r.Warning)).Should().Equal(
            (EditionFit.Included, "Why it matters is TLP:CLEAR and will reach the company-wide edition."),
            (EditionFit.Part, null));
        EditionChecks.Rows(brief, Edition.Team, TlpLevel.AmberStrict).Should().OnlyContain(r => r.Warning == null,
            "the team edition is meant to carry it");
    }

    [Fact]
    public void TheTriageStep_SaysHowManyNewItemsAreOnKev()
    {
        var brief = BriefWith(BriefStatus.Draft, Ready("a"));
        Step(BriefSteps.For(brief, 4, 0, "9:00 AM", newOnKev: 2), BriefStepKind.Triage).Should().Be((StepState.Attention, "4 new in the feed · 2 on KEV"));
        Step(BriefSteps.For(brief, 4, 0, "9:00 AM", newOnKev: 0), BriefStepKind.Triage).Should().Be((StepState.Attention, "4 new in the feed"));
        Step(BriefSteps.For(brief, 0, 0, "9:00 AM", newOnKev: 0), BriefStepKind.Triage).Should().Be((StepState.Done, "Feed clear"));
        Step(BriefSteps.For(brief, null, 0, "9:00 AM", newOnKev: 3), BriefStepKind.Triage).Detail.Should().Be("Sorting the feed", "someone who doesn't triage gets no count");
    }

    [Fact]
    public void EditionCheck_ListsOnlyTheExceptions_AndSaysWhenEveryArticleGoesOutWhole()
    {
        var mixed = Ready("mixed");
        mixed.Bullets.Add(new ArticleBullet { Text = "isac only", Tlp = TlpLevel.Amber, SortOrder = 1 });
        var brief = BriefWith(BriefStatus.Draft, Ready("open"), mixed, Ready("amber", TlpLevel.Amber), Threat());
        brief.Intro = "A quiet day.";

        var team = EditionChecks.Rows(brief, Edition.Team, TlpLevel.AmberStrict, hasNumbers: true);
        EditionChecks.Exceptions(team).Should().BeEmpty("the title slide line, the numbers and all four articles go out whole");
        EditionChecks.Whole(team).Should().Be("All 4 articles go out whole.");

        var company = EditionChecks.Rows(brief, Edition.Public, TlpLevel.Green, hasNumbers: true);
        EditionChecks.Exceptions(company).Select(r => (r.Title, r.Fit)).Should().Equal(
            ("mixed", EditionFit.Part), ("amber", EditionFit.LeftOut), ("threat", EditionFit.Included));
        EditionChecks.Exceptions(company)[2].Warning.Should().NotBeNull("an article that goes out whole with a warning is still listed");
        EditionChecks.Whole(company).Should().BeNull();
        company.Should().HaveCount(5, "every article still has its row, for the counts")
            .And.NotContain(r => r.Title == "Today's numbers", "the numbers never go to the company: said once under the check, not a row every day");

        // Numbers still to enter, or a title slide line above the ceiling, are exceptions.
        var late = EditionChecks.Rows(brief, Edition.Team, TlpLevel.AmberStrict, hasNumbers: true, missingNumbers: 2);
        EditionChecks.Exceptions(late).Should().ContainSingle().Which.Title.Should().Be("Today's numbers");
        EditionChecks.Whole(late).Should().Be("All 4 articles go out whole.", "the articles do, whatever else is listed");
        brief.IntroTlp = TlpLevel.Amber;
        EditionChecks.Exceptions(EditionChecks.Rows(brief, Edition.Public, TlpLevel.Green))[0].Title.Should().Be("Title slide line");

        EditionChecks.Whole(EditionChecks.Rows(BriefWith(BriefStatus.Draft, Ready("only")), Edition.Team, TlpLevel.AmberStrict)).Should().Be("The article goes out whole.");
        EditionChecks.Whole(EditionChecks.Rows(BriefWith(BriefStatus.Draft, Ready("a"), Ready("b")), Edition.Team, TlpLevel.AmberStrict)).Should().Be("Both articles go out whole.");
        EditionChecks.Whole(EditionChecks.Rows(BriefWith(BriefStatus.Draft), Edition.Team, TlpLevel.AmberStrict, hasNumbers: true)).Should().BeNull("there are no articles to speak of");
    }

    [Fact]
    public void ADroppedRow_LandsBeforeOrAfterItsTarget()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Guid[] ids = [a, b, c];
        BriefOrder.Dropped(ids, c, a, after: false).Should().Equal(c, a, b);
        BriefOrder.Dropped(ids, a, c, after: true).Should().Equal(b, c, a);
        BriefOrder.Dropped(ids, a, b, after: true).Should().Equal(b, a, c);
        BriefOrder.Dropped(ids, a, a, after: true).Should().Equal(ids);
        BriefOrder.Dropped(ids, Guid.NewGuid(), a, after: false).Should().Equal(ids, "a row that isn't on the brief changes nothing");
    }

    [Fact]
    public void AnArticlesGaps_HaveShortNames_ForTheRunningOrder()
    {
        var a = new Article { Title = "t", Classification = Classification.Threat };
        ArticleChecks.For(a).Select(c => c.Short).Should().Equal(
            "Bottom line", "A bullet", "Priority", "Recommended actions", "Scorecard tag", "Source");
        new ArticleCheck("Something else.", false, ArticlePart.Assessment).Short.Should().Be("Something else.");
        BriefSteps.Numbers(1).Should().Be("1 number");
        BriefSteps.Numbers(3).Should().Be("3 numbers");
    }
}
