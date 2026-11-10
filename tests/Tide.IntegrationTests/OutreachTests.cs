using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Files;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.Application.Preferences.UserPreferences;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Outreach actions (ADR 0019): the team's templates, kept as the saved queries are; writing the email from an action,
/// held to the template's TLP ceiling; marking it sent, waiting, the follow-up reminder and recording the reply; and the
/// rename from Vendor inquiry keeping old actions, settings, bundles and links working.
/// </summary>
public class OutreachServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Owner = "S-1-5-21-OWNER";

    public OutreachServiceTests() => As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst, AppRole.Admin);

    public void Dispose() => _db.Dispose();

    private void As(string id, string name, params AppRole[] roles) =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = (id, name, roles.ToHashSet());

    private const string Body = """
        Hello {{product.contact}},

        We run {{product.name}} {{product.versions}} ({{product.team}}). Are we affected by this?

        {{article.title}}
        {{article.bottom-line}}
        {{article.link}}
        {{article.cves}}

        Indicators:
        {{indicators}}

        {{sender.name}}, {{sender.team}}
        {{action.number}}: {{action.link}}
        """;

    private Task<Guid> Template(string name = "Are we affected?", TlpLevel ceiling = TlpLevel.Green, string body = Body, string? to = null) =>
        _db.OutreachTemplates().SaveAsync(new OutreachTemplateForm
        {
            Name = name, Subject = "{{product.name}}: are we affected? [{{tlp}}]", Body = body, Ceiling = ceiling, To = to, Description = "To a vendor's PSIRT",
        });

    private async Task<Guid> Team(string name = "Network")
    {
        var team = await _db.Teams().AddAsync(name + " " + Guid.NewGuid().ToString("N")[..4], null, null);
        await _db.Teams().AddMemberAsync(team, Owner, "Olu Owner");
        return team;
    }

    // An article about the gateway, with a key point above GREEN, two indicators, two CVEs (one on KEV), at its TLP.
    private async Task<Guid> Article(TlpLevel tlp = TlpLevel.Green)
    {
        var a = new Article
        {
            Title = "Edge VPN zero-day exploited", AuthorId = "x", AuthorName = "X", Tlp = tlp, Bluf = "A pre-auth flaw is exploited in the wild.",
            SourceUrl = "https://vendor.example/advisory", WhyItMatters = "Both our gateways run it.", WhyItMattersTlp = TlpLevel.Amber,
            Bullets = [new() { Text = "A patch is out", Tlp = TlpLevel.Clear }, new() { Text = "Ours are unpatched", Tlp = TlpLevel.Red, SortOrder = 1 }],
            Indicators = [new() { Type = IndicatorType.Domain, Value = "evil.example" }, new() { Type = IndicatorType.IpAddress, Value = "198.51.100.7" }],
            Cves = [new() { CveId = "CVE-2026-1234" }, new() { CveId = "CVE-2026-0002" }],
        };
        await using var ctx = _db.Context();
        ctx.Articles.Add(a);
        if (!await ctx.CveRecords.AnyAsync(c => c.CveId == "CVE-2026-1234"))
            ctx.CveRecords.Add(new CveRecord { CveId = "CVE-2026-1234", IsKev = true, EpssScore = 0.5m });
        await ctx.SaveChangesAsync();
        return a.Id;
    }

    private async Task<Guid> Product(Guid team, Guid? article = null, string? contact = "psirt@vendor.example")
    {
        var id = await _db.Stack().SaveAsync(new StackForm
        {
            Name = "Edge VPN " + Guid.NewGuid().ToString("N")[..4], Versions = "22.7R2", TeamId = team, ContactName = "Vendor PSIRT", ContactEmail = contact,
        });
        if (article is { } a) await _db.Stack().TagArticleAsync(a, id);
        return id;
    }

    private async Task<ActionItem> Action(Guid team, Guid? article = null, ActionKind kind = ActionKind.Outreach, string title = "Ask the vendor whether we're affected")
    {
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = title, Kind = kind, TeamId = team, ArticleId = article, DueDate = svc.Today.AddDays(7) });
        return (await svc.GetAsync(number))!.Action;
    }

    // An Outreach action from a GREEN article tagged with a product that has a contact.
    private async Task<(ActionItem Action, Guid Product)> Scene(TlpLevel articleTlp = TlpLevel.Green)
    {
        var team = await Team();
        var article = await Article(articleTlp);
        var product = await Product(team, article);
        return (await Action(team, article), product);
    }

    private Task<OutreachMessage> Sent(ActionItem a, string to = "psirt@vendor.example") =>
        _db.Outreach().MarkSentAsync(a.Id, new OutreachSend { To = to, Subject = "Edge VPN: are we affected?", Body = "Hello,\n\nAre we affected?", Tlp = TlpLevel.Green });

    // --- the template library --------------------------------------------------------------------------------------

    [Fact]
    public async Task Templates_AreKeptByWhoeverWorksActions_OneNameEach_AndAViewerOnlyReadsThem()
    {
        var id = await Template(" Are we affected? ");
        var t = (await _db.OutreachTemplates().GetAsync(id))!;
        (t.Name, t.Ceiling, t.CreatedByName, t.Description).Should().Be(("Are we affected?", TlpLevel.Green, "Test Analyst", "To a vendor's PSIRT"));
        await FluentActions.Invoking(() => Template("ARE WE AFFECTED?")).Should().ThrowAsync<ArgumentException>().WithMessage("There's already a template called ARE WE AFFECTED?.");

        As(Owner, "Olu Owner", AppRole.ActionOwner);
        await Template("Ask the ISAC");
        (await _db.OutreachTemplates().ListAsync()).Select(r => r.Template.Name).Should().Equal("Are we affected?", "Ask the ISAC");

        As("S-1-5-21-VIEWER", "Val Viewer", AppRole.Viewer);
        (await _db.OutreachTemplates().ListAsync()).Should().HaveCount(2);
        _db.OutreachTemplates().CanWrite.Should().BeFalse();
        await FluentActions.Invoking(() => Template("Viewer's")).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.OutreachTemplates().SetRetiredAsync(id, true)).Should().ThrowAsync<ForbiddenException>();

        As("S-1-5-21-READER", "Rae Reader", AppRole.Reader);
        await FluentActions.Invoking(() => _db.OutreachTemplates().ListAsync()).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [InlineData("Dear {{vendor.name}}", "*{{vendor.name}} isn't a placeholder TIDE fills*")]
    [InlineData("{{indicators:sha3}}", "*isn't a kind of indicator TIDE knows*")]
    [InlineData(" ", "A template needs a body.")]
    public async Task ATemplate_NamingAPlaceholderTideDoesntFill_IsRefusedWhenSaved(string body, string message) =>
        await FluentActions.Invoking(() => Template(body: body)).Should().ThrowAsync<ArgumentException>().WithMessage(message);

    [Fact]
    public async Task ATemplatesFixedAddress_MustBeOne_AndASubjectIsChecked_Too()
    {
        await FluentActions.Invoking(() => Template(to: "psirt at vendor")).Should().ThrowAsync<ArgumentException>().WithMessage("Not an email address: psirt at vendor");
        await FluentActions.Invoking(() => _db.OutreachTemplates().SaveAsync(new OutreachTemplateForm { Name = "x", Subject = "{{product}}", Body = "b" }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("{{product}} isn't a placeholder*");
        (await _db.OutreachTemplates().GetAsync(await Template(to: " isac@isac.example ")))!.To.Should().Be("isac@isac.example");
    }

    [Fact]
    public async Task AnEdit_BegunBeforeAColleaguesEdit_IsRefused_AndEachWriteIsAudited_AndHeard()
    {
        var id = await Template();
        var t = (await _db.OutreachTemplates().GetAsync(id))!;
        var mine = OutreachTemplateForm.From(t);
        var theirs = OutreachTemplateForm.From(t);
        As("S-1-5-21-TEST-2", "Robin", AppRole.Analyst);
        theirs.Ceiling = TlpLevel.Clear;
        await _db.OutreachTemplates().SaveAsync(theirs);

        As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst);
        mine.Name = "Mine";
        await FluentActions.Invoking(() => _db.OutreachTemplates().SaveAsync(mine)).Should().ThrowAsync<StaleEditException>().WithMessage("Someone else changed this template*");

        var now = (await _db.OutreachTemplates().GetAsync(id))!;
        (now.Name, now.Ceiling, now.ModifiedByName).Should().Be(("Are we affected?", TlpLevel.Clear, "Robin"));
        await using var ctx = _db.Context();
        (await ctx.AuditLog.Where(e => e.EntityType == nameof(OutreachTemplate)).Select(e => e.Action).ToListAsync()).Should().BeEquivalentTo(["Added", "Modified"]);
        Tide.Infrastructure.Persistence.ChangeInterceptor.TopicsOf(now).Should().Equal(ChangeFeed.OutreachTemplates);
        Tide.Infrastructure.Persistence.ChangeInterceptor.TopicsOf(new OutreachMessage { ActionId = id }).Should().Equal(ChangeFeed.Action(id));
    }

    [Fact]
    public async Task ARetiredTemplate_IsntOffered_NorWritten_AndOnlyWhoeverSavedIt_OrAnAdministrator_DeletesIt()
    {
        var (action, _) = await Scene();
        As(Owner, "Olu Owner", AppRole.ActionOwner);
        var id = await Template("Owner's question");
        await _db.Outreach().MarkSentAsync(action.Id, new OutreachSend { TemplateId = id, To = "psirt@vendor.example", Subject = "s", Body = "b" });
        await _db.OutreachTemplates().SetRetiredAsync(id, true);
        (await _db.OutreachTemplates().OfferedAsync()).Should().BeEmpty();
        await FluentActions.Invoking(() => _db.Outreach().WriteAsync(action.Id, id)).Should().ThrowAsync<InvalidOperationException>().WithMessage("Owner's question is retired*");
        (await _db.OutreachTemplates().ListAsync(includeRetired: true)).Should().ContainSingle().Which.Emails.Should().Be(1);

        As("S-1-5-21-ROBIN", "Robin", AppRole.ActionOwner);
        await FluentActions.Invoking(() => _db.OutreachTemplates().DeleteAsync(id)).Should().ThrowAsync<ForbiddenException>().WithMessage("Only the person who saved it*");
        As(Owner, "Olu Owner", AppRole.ActionOwner);
        await _db.OutreachTemplates().DeleteAsync(id);
        await _db.OutreachTemplates().DeleteAsync(id);   // gone already: fine

        var kept = (await _db.Outreach().ForActionAsync(action.Id)).Messages.Should().ContainSingle().Subject;
        (kept.TemplateId, kept.TemplateName).Should().Be(((Guid?)null, "Owner's question"), "an email keeps the name of the template it came from");
    }

    // --- writing the email ------------------------------------------------------------------------------------------

    [Fact]
    public async Task WritingTheEmail_FillsItFromTheArticle_TheIndicators_TheProduct_AndTheSender()
    {
        var (action, product) = await Scene();
        var template = await Template(to: "isac@isac.example");

        var w = await _db.Outreach().WriteAsync(action.Id, template);

        w.Product!.Id.Should().Be(product, "the article is tagged with it");
        w.Draft.Subject.Should().Be($"{w.Product.Name}: are we affected? [TLP:GREEN]");
        var body = w.Draft.Body;
        body.Should().StartWith("TLP:GREEN: Limited disclosure", "the TLP line, as the subject places {{tlp}} but the body doesn't")
            .And.Contain("Hello Vendor PSIRT,").And.Contain($"We run {w.Product.Name} 22.7R2 (Network ")
            .And.Contain("Edge VPN zero-day exploited\nA pre-auth flaw is exploited in the wild.\nhttps://vendor.example/advisory\n")
            .And.Contain("CVE-2026-0002\nCVE-2026-1234 (on CISA's KEV list; EPSS 50%)")
            .And.Contain("Indicators:\n198.51.100[.]7\nevil[.]example\n", "defanged, as a hunt groups them")
            .And.Contain("Test Analyst, Cyber Threat Intelligence", "not on the action's team: the organization's")
            .And.Contain($"A-{action.Number}: https://tide.example.org/actions/{action.Number}");
        w.Draft.Marking.Should().Be(TlpLevel.Green);
        w.Draft.Unfilled.Should().BeEmpty();
        w.Recipients.Select(r => (r.Address, r.Label)).Should().Equal(("isac@isac.example", "the template's address"), ("psirt@vendor.example", $"{w.Product.Name}'s contact, Vendor PSIRT"));
        w.To.Should().Be("isac@isac.example");
        (await _db.Outreach().ForActionAsync(action.Id)).Messages.Should().BeEmpty("writing saves nothing");
    }

    [Fact]
    public async Task WritingTheEmail_HoldsTheArticleToTheTemplatesCeiling_SaysWhatItLeftOut_AndMarksWhatItCouldntFill()
    {
        var (action, _) = await Scene(TlpLevel.Amber);
        var template = await Template(ceiling: TlpLevel.Green, body: "{{article.title}}\n{{indicators}}\n{{article.key-points}}\n{{product.name}}");

        var d = (await _db.Outreach().WriteAsync(action.Id, template)).Draft;

        d.Body.Should().Contain("[[article.title: above TLP:GREEN, left out]]\n[[indicators: above TLP:GREEN, left out]]\n[[article.key-points: above TLP:GREEN, left out]]")
            .And.NotContain("evil").And.NotContain("Edge VPN zero-day");
        d.LeftOut.Should().ContainSingle().Which.Should().StartWith("The article is TLP:AMBER, above this template's TLP:GREEN");
        d.Unfilled.Should().Equal("{{article.title}}", "{{indicators}}", "{{article.key-points}}");
        d.Marking.Should().Be(TlpLevel.Clear);

        var amber = await Template("Internal", TlpLevel.Amber, "{{article.key-points}}\n{{article.why-it-matters}}");
        var inside = (await _db.Outreach().WriteAsync(action.Id, amber)).Draft;
        inside.Body.Should().Contain("- A patch is out\nBoth our gateways run it.").And.NotContain("Ours are unpatched", "that key point is RED");
        inside.LeftOut.Should().Equal("1 key point above TLP:AMBER left out.");
        inside.Marking.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public async Task AnActionWithNoArticle_FillsItsOwnIndicators_TheOnesItsHuntsUse_AndTheProductItNames()
    {
        var team = await Team();
        var product = await Product(team);
        var name = (await _db.Stack().GetAsync(product))!.Product.Name;
        var action = await Action(team, title: $"Ask about {name} sightings");
        await _db.Actions().AddIndicatorsAsync(action.Id, [new IndicatorForm { Type = IndicatorType.Url, Value = "https://evil.example/drop" }]);
        (await _db.Actions().HuntSourcesAsync(action.Id)).Indicators.Should().Be(1);

        var clear = (await _db.Outreach().WriteAsync(action.Id, await Template("Clear", TlpLevel.Clear, "{{indicators}} {{product.name}}"))).Draft;
        clear.Body.Should().Contain("[[indicators: above TLP:CLEAR, left out]] " + name, "the product is named in the action's title");

        var w = await _db.Outreach().WriteAsync(action.Id, await Template("Amber", TlpLevel.Amber, "{{indicators}}\n{{article.title}}"));
        w.Draft.Body.Should().Contain("hxxps://evil[.]example/drop\n[[article.title: nothing to fill]]");
        w.Draft.Marking.Should().Be(TlpLevel.Amber, "the action's own indicators carry no marking, so count as AMBER");
        (await _db.Outreach().ProductsAsync(action.Id)).Select(p => p.Id).Should().Equal(product);
    }

    [Fact]
    public async Task OnlyWhoeverWorksTheAction_WritesItsEmail()
    {
        var (action, _) = await Scene();
        var template = await Template();
        As("S-1-5-21-ROBIN", "Robin", AppRole.ActionOwner);
        await FluentActions.Invoking(() => _db.Outreach().WriteAsync(action.Id, template)).Should().ThrowAsync<ForbiddenException>();
        As(Owner, "Olu Owner", AppRole.ActionOwner);
        (await _db.Outreach().WriteAsync(action.Id, template)).Draft.Body.Should().Contain("Olu Owner, Network", "on the action's team: that team");
    }

    // --- sent, waiting, the reply ----------------------------------------------------------------------------------

    [Fact]
    public async Task MarkingItSent_KeepsWhatWasSent_AndTheActionWaits_ItsHistorySaysSo()
    {
        var (action, _) = await Scene();
        var template = await Template();
        var w = await _db.Outreach().WriteAsync(action.Id, template);

        await _db.Outreach().MarkSentAsync(action.Id, new OutreachSend
        {
            TemplateId = template, To = "psirt@vendor.example; Security@Vendor.example, psirt@vendor.example", Subject = w.Draft.Subject, Body = w.Draft.Body, Tlp = w.Draft.Marking,
        });

        var state = await _db.Outreach().ForActionAsync(action.Id);
        var m = state.Messages.Should().ContainSingle().Subject;
        (m.To, m.Subject, m.Tlp, m.SentByName, m.TemplateName).Should().Be(("psirt@vendor.example; Security@Vendor.example", w.Draft.Subject, TlpLevel.Green, "Test Analyst", "Are we affected?"));
        m.Body.Should().Be(w.Draft.Body.Trim());
        state.Waiting.Should().BeTrue();
        state.Oldest.Should().Be(m);
        var history = (await _db.Actions().GetAsync(action.Number))!.Action.Events.Select(e => e.Text);
        history.Should().Contain($"Sent an email to psirt@vendor.example; Security@Vendor.example (TLP:GREEN): \"{w.Draft.Subject}\", from the template \"Are we affected?\".");
    }

    [Theory]
    [InlineData("psirt", "a body", "Not an email address: psirt")]
    [InlineData(" ", "a body", "Say who it went to: an email address.")]
    [InlineData("psirt@vendor.example", "We run [[product.versions: nothing to fill]].", "The email still has a [[…]] marker*")]
    [InlineData("psirt@vendor.example", "", "Give the email's body.")]
    public async Task MarkingItSent_IsRefused_WithoutAValidAddress_OrWithAMarkerLeft(string to, string body, string message)
    {
        var (action, _) = await Scene();
        await FluentActions.Invoking(() => _db.Outreach().MarkSentAsync(action.Id, new OutreachSend { To = to, Subject = "s", Body = body }))
            .Should().ThrowAsync<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public async Task OnlyAnOutreachAction_KeepsEmails_AndOnlyWhoeverWorksIt()
    {
        var team = await Team();
        var sweep = await Action(team, kind: ActionKind.IocSweep);
        await FluentActions.Invoking(() => Sent(sweep)).Should().ThrowAsync<InvalidOperationException>().WithMessage("Only an Outreach action keeps its emails*");

        var ask = await Action(team);
        As("S-1-5-21-VIEWER", "Val Viewer", AppRole.Viewer);
        await FluentActions.Invoking(() => Sent(ask)).Should().ThrowAsync<ForbiddenException>();
        (await _db.Outreach().ForActionAsync(ask.Id)).Messages.Should().BeEmpty("a Viewer reads it");
        As(Owner, "Olu Owner", AppRole.ActionOwner);
        (await Sent(ask)).SentByName.Should().Be("Olu Owner");
    }

    [Fact]
    public async Task RecordingTheReply_ClosesTheActionWithAnOutcome_InOneStep_ByTheRuleEveryCloseFollows()
    {
        var (action, _) = await Scene();
        var m = await Sent(action);
        await FluentActions.Invoking(() => _db.Outreach().RecordReplyAsync(m.Id, "n", close: true, ActionOutcome.NoHits))
            .Should().ThrowAsync<ArgumentException>().WithMessage("An Outreach action closes with one of: Answered, Not affected, Other.");

        var file = (await _db.ActionFiles().AddAsync(action.Id, [new ActionFileUpload("reply.eml", Samples.Text("From: psirt"), "message/rfc822", "Reply")]))[0];
        await _db.Outreach().RecordReplyAsync(m.Id, "  Not affected: fixed in 22.7R1.  ", close: true, ActionOutcome.NotAffected, file.FileId);

        var a = (await _db.Actions().GetAsync(action.Number))!.Action;
        (a.Status, a.Outcome, a.OutcomeNote).Should().Be((ActionStatus.Done, ActionOutcome.NotAffected, "Not affected: fixed in 22.7R1."));
        a.Events.Select(e => e.Text).Should().Contain("Reply from psirt@vendor.example recorded (with reply.eml). Status: Open → Done (Not affected): Not affected: fixed in 22.7R1.");
        var replied = (await _db.Outreach().ForActionAsync(action.Id)).Messages.Single();
        (replied.IsWaiting, replied.ReplyNotes, replied.ReplyFileId, replied.ReplyRecordedByName).Should().Be((false, "Not affected: fixed in 22.7R1.", (Guid?)file.FileId, "Test Analyst"));

        await FluentActions.Invoking(() => _db.Outreach().RecordReplyAsync(m.Id, null, close: false)).Should().ThrowAsync<StaleEditException>().WithMessage("Test Analyst already recorded the reply*");
        await FluentActions.Invoking(() => _db.Outreach().WithdrawAsync(m.Id)).Should().ThrowAsync<InvalidOperationException>().WithMessage("Its reply is recorded, so it stays.");
    }

    [Fact]
    public async Task RecordingTheReply_WithoutClosing_LeavesTheActionWaitingOnTheOther_AndNotSentAfterAll_TakesOneBack()
    {
        var (action, _) = await Scene();
        var vendor = await Sent(action);
        var isac = await Sent(action, "isac@isac.example");
        await FluentActions.Invoking(() => _db.Outreach().RecordReplyAsync(vendor.Id, null, close: false, fileId: Guid.NewGuid()))
            .Should().ThrowAsync<ArgumentException>().WithMessage("That file isn't on this action.");

        await _db.Outreach().RecordReplyAsync(vendor.Id, "They're checking.", close: false);
        var state = await _db.Outreach().ForActionAsync(action.Id);
        state.Oldest!.Id.Should().Be(isac.Id, "the ISAC's is still waiting");
        (await _db.Actions().GetAsync(action.Number))!.Action.Status.Should().Be(ActionStatus.Open);

        await _db.Outreach().WithdrawAsync(isac.Id);
        (await _db.Outreach().ForActionAsync(action.Id)).Waiting.Should().BeFalse();
        (await _db.Actions().GetAsync(action.Number))!.Action.Events.Select(e => e.Text).Should()
            .Contain("Reply from psirt@vendor.example recorded: They're checking.")
            .And.Contain("Took back the email to isac@isac.example (\"Edge VPN: are we affected?\"): not sent after all.");
    }

    // --- the follow-up reminder ---------------------------------------------------------------------------------

    private static OrgClock At(int day, int hourUtc = 14) =>
        new(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 11, day, hourUtc, 0, 0, TimeSpan.Zero)));

    private async Task SentOn(Guid messageId, DateTimeOffset when)
    {
        await using var ctx = _db.Context();
        (await ctx.OutreachMessages.SingleAsync(m => m.Id == messageId)).SentAtUtc = when;
        await ctx.SaveChangesAsync();
    }

    private async Task Person(string sid, string email)
    {
        await using var ctx = _db.Context();
        ctx.Users.Add(new AppUser { Sid = sid, DisplayName = sid, Email = email, RolesCsv = "ActionOwner", LastSeenUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task TheFollowUp_RemindsWhoeverSentIt_OnceItsWaitedTheBusinessDays_AndOnlyOnce()
    {
        var (action, _) = await Scene();
        await Person(Owner, "olu@example.org");
        As(Owner, "Olu Owner", AppRole.ActionOwner);
        var m = await Sent(action);
        await SentOn(m.Id, new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero));   // Monday
        var options = new ReminderOptions { OutreachFollowUp = { Enabled = true, Days = 5 } };

        await _db.Reminders(options, At(6)).RunDueAsync();   // Friday: four business days
        _db.Email.Sent.Should().BeEmpty();
        var waiting = (await _db.Outreach(options, At(6)).ForActionAsync(action.Id)).Messages.Single();
        _db.Outreach(options, At(6)).FollowUpOn(waiting).Should().Be(new DateOnly(2026, 11, 9));

        await _db.Reminders(options, At(9)).RunDueAsync();   // the next Monday: five
        var email = _db.Email.Sent.Should().ContainSingle().Subject;
        email.To.Should().Equal("olu@example.org");
        email.Subject.Should().Be("TIDE: no reply yet on 1 action [TLP:GREEN]", "marked with its article's TLP, as every reminder is");
        email.Html.Should().Contain("5 business days").And.Contain($"A-{action.Number} to psirt@vendor.example, sent Mon Nov 2").And.Contain("Ask the vendor whether we&#39;re affected");

        await _db.Reminders(options, At(10)).RunDueAsync();
        _db.Email.Sent.Should().HaveCount(1, "once per email");
    }

    [Fact]
    public async Task TheFollowUp_IsntSent_WhenOff_WhenReplied_OrToSomeoneWhoOptedOut()
    {
        var (action, _) = await Scene();
        await Person(Owner, "olu@example.org");
        As(Owner, "Olu Owner", AppRole.ActionOwner);
        var replied = await Sent(action);
        var waiting = await Sent(action, "isac@isac.example");
        foreach (var id in new[] { replied.Id, waiting.Id }) await SentOn(id, DateTimeOffset.UtcNow.AddDays(-30));
        await _db.Outreach().RecordReplyAsync(replied.Id, null, close: false);

        (await _db.Reminders(new ReminderOptions()).RunAsync(ReminderKind.OutreachFollowUp)).Should().Be(1, "run now goes whatever the switch; only the one waiting");
        _db.Email.Sent.Single().Html.Should().Contain("isac@isac.example").And.NotContain("psirt@vendor.example,");
        _db.Outreach(new ReminderOptions()).FollowUpOn(waiting).Should().BeNull("the reminder is off");

        await using (var ctx = _db.Context())
        {
            ctx.NotificationMarks.RemoveRange(ctx.NotificationMarks);
            await ctx.SaveChangesAsync();
        }
        await _db.NotificationPrefs(new ReminderOptions { OutreachFollowUp = { Enabled = true } }).SetMineAsync(NotifyOutreachFollowUp, Off);
        var log = new ReminderLog();
        (await _db.Reminders(log: log).RunAsync(ReminderKind.OutreachFollowUp)).Should().Be(0);
        log.Last(ReminderKind.OutreachFollowUp)!.Note.Should().Be("all recipients opted out");
    }

    [Fact]
    public void TheFollowUpsWording_IsAnEmailTemplate_PreviewedWithAnOutreachSample()
    {
        var t = Tide.Application.Distribution.EmailTemplateCatalog.OutreachFollowUp;
        Tide.Application.Distribution.EmailTemplateCatalog.Problem(t, Tide.Application.Distribution.EmailPart.Body, "Follow up.")
            .Should().NotBeNull("the list of actions is what the email is for");
        var preview = _db.EmailTemplateAdmin().Preview(t.Id, t.DefaultSubject, t.DefaultHeading, t.DefaultBody);
        preview.Subject.Should().Be("TIDE: no reply yet on 2 actions");
        preview.Html.Should().Contain("5 business days").And.Contain("A-44 to psirt@vendor.example, sent ").And.Contain("Ask the VPN vendor whether our version is affected");
    }

    // --- recipients on Our stack ----------------------------------------------------------------------------------

    [Fact]
    public async Task AProductsContact_IsAnAddress_OrRefused()
    {
        var team = await Team();
        await FluentActions.Invoking(() => Product(team, contact: "psirt at vendor")).Should().ThrowAsync<ArgumentException>()
            .WithMessage("The contact's address isn't an email address: psirt at vendor");
        var p = (await _db.Stack().GetAsync(await Product(team, contact: " psirt@vendor.example ")))!.Product;
        (p.ContactName, p.ContactEmail).Should().Be(("Vendor PSIRT", "psirt@vendor.example"));
        StackForm.From(p).ContactEmail.Should().Be("psirt@vendor.example");
    }

    // --- the rename -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnActionRaisedAsAVendorInquiry_IsAnOutreachAction_ItsOutcomesAndTheApiFollow()
    {
        var team = await Team();
        var before = await Action(team, kind: ActionKind.Other, title: "Ask the vendor (raised before the rename)");
        await using (var ctx = _db.Context())
            await ctx.Database.ExecuteSqlRawAsync("UPDATE Actions SET Kind = 6 WHERE Id = {0}", before.Id);   // as a vendor inquiry was stored

        var a = (await _db.Actions().GetAsync(before.Number))!.Action;
        a.Kind.Should().Be(ActionKind.Outreach);
        (await Sent(a)).ActionId.Should().Be(a.Id, "its emails can be tracked like a new one's");
        await _db.Actions().SetStatusAsync(a.Id, ActionStatus.Done, ActionOutcome.Answered, null);
        (await _db.Api().ActionAsync(a.Number, null))!.Kind.Should().Be("Outreach");
    }

    [Fact]
    public async Task ATargetUnderTheOldName_FromTheServer_OrAnOldBundle_IsOutreachs()
    {
        var sla = new SlaSettings(_db, _db.User, Options.Create(new SlaOptions { Kind = new() { [ActionService.FormerOutreachName] = 4, ["Block"] = 1 } }));
        var targets = await sla.GetAsync();
        (targets.ByKind[ActionKind.Outreach], targets.ByKind[ActionKind.Block]).Should().Be((4, 1));

        var bundle = new ConfigBundle(ConfigBundle.FormatName, ConfigBundle.CurrentVersion, DateTimeOffset.UtcNow, "x", "1.5.4",
            [new BundleSetting("Actions:Sla:Kind:VendorInquiry", "6")], [], [], [], []);
        var preview = await new ConfigBundleService(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System).PreviewAndImportAsync(bundle);
        preview.Changes.Should().Contain(c => c.Item == "Actions:Sla:Kind:Outreach" && c.Kind == BundleChangeKind.Add);
        (await _db.Sla().GetAsync()).ByKind[ActionKind.Outreach].Should().Be(6);
    }

    [Fact]
    public async Task ATrendsLinkToTheOldKindsLine_OpensOutreachs()
    {
        var team = await Team();
        await Action(team);
        var report = await _db.Trends().ReportAsync(_db.Trends().CurrentPeriod(Tide.Application.Trends.PeriodKind.Month));
        report.Find("actions:raised-kind:Outreach")!.Name.Should().Be("Outreach");
        report.Find("actions:raised-kind:VendorInquiry")!.Key.Should().Be("actions:raised-kind:Outreach");
    }
}

/// <summary>The Outreach templates page, the action's Outreach card and the product's contact, as they render.</summary>
public class OutreachPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:viewer-val", "Val Viewer", "Viewer");

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    private async Task<Guid> Saved(string name, string body = "Hello,\n\n{{article.title}}\n{{indicators}}", string? to = null)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<OutreachTemplateService>().SaveAsync(new OutreachTemplateForm
        {
            Name = name, Subject = "About {{action.number}}", Body = body, Ceiling = TlpLevel.Green, To = to, Description = "For the vendor",
        });
    }

    private async Task<int> OutreachAction(string title)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
        return await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, Kind = ActionKind.Outreach, TeamId = team });
    }

    private async Task<ActionDetail> Read(int number)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!;
    }

    [Fact]
    public async Task ThePage_ListsTheTemplates_WithTheirCeilingAndUse_AndAViewerOnlyReads()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var asked = await Saved("Ask the PSIRT " + tag, to: "psirt@vendor.example");

        await using (var r = await RenderAsync<OutreachTemplatesPage>(app, "/library/templates?row=" + asked))
        {
            var html = await r.Html();
            html.Should().Contain("<h2>Outreach templates</h2>").And.Contain("Ask the PSIRT " + tag).And.Contain("For the vendor")
                .And.Contain("Filled up to TLP:GREEN, always to psirt@vendor.example.").And.Contain("Not used yet").And.Contain("Saved by Tester, ")
                .And.Contain("New template</button>").And.Contain($"aria-label=\"Edit Ask the PSIRT {tag}\"").And.Contain($"aria-label=\"Retire Ask the PSIRT {tag}\"")
                .And.Contain($"aria-label=\"Delete Ask the PSIRT {tag}\"").And.Contain("data-copy-done=\"Copied.\"").And.NotContain("style=");
            html.Should().NotContain("<h1", "the Library page has the title");

            await r.Click(p => { p.New(); return Task.CompletedTask; });
            var form = await r.Html();
            form.Should().Contain("New template</h2>").And.Contain("<label class=\"tide-field-label\" for=\"ot-f-ceiling\">Highest TLP it carries</label>")
                .And.Contain("<summary>Placeholders</summary>").And.Contain("{{indicators:domain}}").And.Contain("{{product.versions}}");
            r.Component.Form!.Body.Should().Contain("{{product.name}} {{product.versions}}");
            (r.Component.Form.Name, r.Component.Form.Ceiling) = ("Ask the ISAC " + tag, TlpLevel.Clear);
            await r.Click(p => p.Save());
            Toasts(r).Select(t => t.Message).Should().Contain($"Ask the ISAC {tag} saved for the team.");
            r.Component.Rows!.Should().Contain(x => x.Template.Name == "Ask the ISAC " + tag && x.Template.Ceiling == TlpLevel.Clear);
        }

        await using var viewer = await RenderAsync<OutreachTemplatesPage>(app, "/library/templates?row=" + asked, @as: Viewer);
        var read = await viewer.Html();
        read.Should().Contain("Ask the PSIRT " + tag).And.Contain(" Copy</button>")
            .And.NotContain("New template").And.NotContain("aria-label=\"Edit ").And.NotContain("aria-label=\"Retire ").And.NotContain("aria-label=\"Delete ");
    }

    [Fact]
    public async Task TheCard_WritesTheEmail_MarksItSent_ShowsItWaiting_AndRecordsTheReply()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var template = await Saved("Ask about the sighting " + tag, to: "isac@isac.example");
        var number = await OutreachAction("Ask the ISAC about the sighting " + tag);
        var detail = await Read(number);

        await using (var r = await RenderAsync<OutreachPanel>(app, "/actions/" + number, new() { ["Action"] = detail.Action, ["CanWork"] = true, ["Files"] = detail.Files }))
        {
            var html = await r.Html();
            html.Should().Contain("<h2 id=\"outreach-h\">Outreach").And.Contain("aria-controls=\"outreach-writer\"").And.Contain("Write the email")
                .And.Contain("href=\"library/templates\"", "relative, so it works under a sub-path");
            await r.Click(p => p.ToggleWriter());
            await r.Click(p => p.Choose(template));
            var written = await r.Html();
            written.Should().Contain("id=\"ow-template\"").And.Contain("id=\"ow-to\"").And.Contain("<datalist id=\"ow-to-list\"><option value=\"isac@isac.example\">the template&#x27;s address</option>")
                .And.Contain($"About A-{number}").And.Contain("[[article.title: nothing to fill]]")
                .And.Contain("Nothing to fill for {{article.title}}, {{indicators}}")
                .And.Contain("data-copy-target=\"ow-subject\"").And.Contain("data-copy-target=\"ow-body\"").And.Contain("Mark sent</button>");
            r.Component.Written!.To.Should().Be("isac@isac.example");
            r.Component.DraftEdited.Should().BeFalse("the fill alone can be written again, so leaving doesn't ask");

            await r.Click(p => p.MarkSent());
            Toasts(r).Select(t => t.Message).Should().Contain("The email still has a [[…]] marker where TIDE had nothing to fill. Fill it in or take it out first.");

            r.Component.Edit(body: "Hello,\n\nDid members see this?");
            r.Component.DraftEdited.Should().BeTrue("a changed email isn't left without asking");
            await r.Click(p => p.MarkSent());
            r.Component.DraftEdited.Should().BeFalse("marked sent, it's kept");
            Toasts(r).Select(t => t.Message).Should().Contain($"Marked sent to isac@isac.example. A-{number} is waiting for the reply.");
        }

        detail = await Read(number);
        await using (var r = await RenderAsync<OutreachPanel>(app, "/actions/" + number, new() { ["Action"] = detail.Action, ["CanWork"] = true, ["Files"] = detail.Files }))
        {
            var html = await r.Html();
            html.Should().Contain("Waiting for reply since").And.Contain("To isac@isac.example</h3>").And.Contain("template: Ask about the sighting " + tag)
                .And.Contain("Follow-up reminders are off.").And.Contain("Record the reply</button>").And.Contain("Not sent after all");
            var m = r.Component.State.Messages.Single();
            await r.Press(p => p.StartReply(m));
            (await r.HtmlOnce("Record the reply from isac@isac.example")).Should().Contain("Close the action with this reply").And.Contain("for=\"or-outcome\"");
            r.Component.Answer("No member saw it.", close: true, ActionOutcome.Answered, new ActionFileUpload("reply.txt", Samples.Text("No sightings.")));
            await r.Click(async p => (await p.RecordReply()).Should().BeTrue());
            Toasts(r).Select(t => t.Message).Should().Contain($"Reply recorded; A-{number} is done.");
        }

        detail = await Read(number);
        (detail.Action.Status, detail.Action.Outcome).Should().Be((ActionStatus.Done, ActionOutcome.Answered));
        detail.Files.Live.Should().ContainSingle().Which.Description.Should().Be("Reply from isac@isac.example");
        await using var done = await RenderAsync<OutreachPanel>(app, "/actions/" + number, new() { ["Action"] = detail.Action, ["CanWork"] = true, ["Files"] = detail.Files });
        (await done.Html()).Should().Contain("Reply recorded by Tester").And.Contain("The reply is kept as <strong>reply.txt</strong> in Files.").And.Contain("No member saw it.")
            .And.NotContain("Write the email", "the action is closed");
    }

    // Closing failed (a colleague closed the action meanwhile), so the analyst records the reply without closing it: the
    // file added on the first try is the reply's, not left on the action unnamed.
    [Fact]
    public async Task AReplyRecordedAgain_AfterARefusedClose_KeepsTheFileAddedTheFirstTime()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var template = await Saved("Ask again " + tag, body: "Hello,\n\nDid you see it?", to: "isac@isac.example");
        var number = await OutreachAction("Ask the ISAC once more " + tag);
        var detail = await Read(number);
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<OutreachService>().MarkSentAsync(detail.Action.Id,
                new OutreachSend { TemplateId = template, To = "isac@isac.example", Subject = "About it", Body = "Did you see it?" });
        detail = await Read(number);

        await using (var r = await RenderAsync<OutreachPanel>(app, "/actions/" + number, new() { ["Action"] = detail.Action, ["CanWork"] = true, ["Files"] = detail.Files }))
        {
            var m = r.Component.State.Messages.Single();
            await r.Press(p => p.StartReply(m));
            await r.HtmlOnce("Record the reply from isac@isac.example");
            // Someone else closes it while the dialog is open.
            await using (var scope = SignedIn(app, SignIn.Analyst))
                await scope.ServiceProvider.GetRequiredService<ActionService>().SetStatusAsync(detail.Action.Id, ActionStatus.Done, ActionOutcome.Answered, null);
            r.Component.Answer("Nothing seen.", close: true, ActionOutcome.Answered, new ActionFileUpload("reply.txt", Samples.Text("Nothing seen.")));
            await r.Click(async p => (await p.RecordReply()).Should().BeFalse());
            Toasts(r).Select(t => t.Message).Should().Contain($"A-{number} is already closed. Record the reply without closing it.");

            // Unticks Close and records it again; the file picked is still the one shown.
            r.Component.Answer("Nothing seen.", close: false, ActionOutcome.None);
            await r.Click(async p => (await p.RecordReply()).Should().BeTrue());
        }

        detail = await Read(number);
        var file = detail.Files.Live.Should().ContainSingle("the file went on the action once").Subject;
        detail.Action.Outreach.Single().ReplyFileId.Should().Be(file.FileId, "the reply names the file kept with it");
    }

    // A second email (a follow-up) after the first is marked sent: the writer opens on the template last used, filled, not
    // on that template's name with nothing under it (choosing it again changes nothing, so it couldn't be filled).
    [Fact]
    public async Task TheWriter_OpenedAgain_FillsTheTemplateItStillShows()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var template = await Saved("Chase " + tag, body: "Hello,\n\nAny news on {{action.number}}?", to: "isac@isac.example");
        await Saved("Another " + tag);
        var number = await OutreachAction("Chase the ISAC " + tag);
        var detail = await Read(number);

        await using var r = await RenderAsync<OutreachPanel>(app, "/actions/" + number, new() { ["Action"] = detail.Action, ["CanWork"] = true, ["Files"] = detail.Files });
        await r.Click(p => p.ToggleWriter());
        await r.Click(p => p.Choose(template));
        await r.Click(p => p.MarkSent());
        Toasts(r).Select(t => t.Message).Should().Contain($"Marked sent to isac@isac.example. A-{number} is waiting for the reply.");
        r.Component.Writing.Should().BeFalse();

        await r.Click(p => p.ToggleWriter());
        r.Component.Written.Should().NotBeNull("the picker still shows the template, so its email is under it");
        r.Component.Written!.Template.Id.Should().Be(template);
        (await r.Html()).Should().Contain($"Any news on A-{number}?").And.Contain("Mark sent</button>");
    }

    [Fact]
    public async Task TheActionPage_ShowsTheCard_OnAnOutreachAction()
    {
        var number = await OutreachAction("Ask the site owner " + Guid.NewGuid().ToString("N")[..6]);
        await using var page = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });
        (await page.Html()).Should().Contain("<h2 id=\"outreach-h\">Outreach").And.Contain("<dd>Outreach</dd>");
    }

    [Fact]
    public async Task AProductsPage_EditsAndShowsItsContact()
    {
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm
            {
                Name = "Edge VPN " + Guid.NewGuid().ToString("N")[..6], ContactName = "Vendor PSIRT", ContactEmail = "psirt@vendor.example",
            });
        await using (var r = await RenderAsync<StackPage>(app, "/stack/" + id, new() { ["Id"] = (Guid?)id }))
            (await r.Html()).Should().Contain("for=\"f-contact-email\">Contact's email</label>").And.Contain("value=\"psirt@vendor.example\"")
                .And.Contain("Offered as the To line when an Outreach action's email is about this product.");
        await using var viewer = await RenderAsync<StackPage>(app, "/stack/" + id, new() { ["Id"] = (Guid?)id }, Viewer);
        (await viewer.Html()).Should().Contain("<dt>Contact</dt><dd>Vendor PSIRT, psirt@vendor.example</dd>");
    }
}
