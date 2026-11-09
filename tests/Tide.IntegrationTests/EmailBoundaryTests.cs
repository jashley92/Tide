using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;

namespace Tide.IntegrationTests;

/// <summary>
/// Outbound email held to the organization's boundary: an email to someone outside it about a team's requests names only
/// what's marked up to the team edition's ceiling (the rest counted, and still marked as reminded); every list that sends
/// mail asks before an address outside the organization is saved; a standing copy is checked again as each email goes;
/// and the boundary itself, <c>Email:OrganizationDomains</c>, falling back to the domain of <c>Email:From</c>.
/// </summary>
public class EmailBoundaryTests : IDisposable
{
    private readonly TestDb _db = new();

    public EmailBoundaryTests() =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-TEST-1", "Test Analyst", new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin });

    public void Dispose() => _db.Dispose();

    // Nov 2026 at 14:00 UTC (past the reminder time); Thanksgiving and the day after off.
    private static OrgClock At(int day, int month = 11)
    {
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, month, day, 14, 0, 0, TimeSpan.Zero)));
        clock.UseHolidays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 26)] = "Thanksgiving", [new DateOnly(2026, 11, 27)] = "Day after" });
        return clock;
    }

    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    private async Task<ActionItem> Due(Guid team, string title, Guid? article = null)
    {
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = title, Description = title + ": the details", TeamId = team, Priority = Priority.P3, ArticleId = article });
        await using var ctx = _db.Context();
        var row = await ctx.Actions.SingleAsync(x => x.Number == number);
        row.DueDate = Tuesday;
        await ctx.SaveChangesAsync();
        return row;
    }

    private Task<Guid> Red() => _db.Articles().CreateAsync("Partner RED report", "Partner", null, TlpLevel.Red);

    // --- A1: what goes outside is held to the team edition's ceiling ----------------------------------------------

    [Fact]
    public async Task AnExternalTeamsReminder_NamesOnlyWhatsUpToTheCeiling_CountsTheRest_AndMarksThemAllReminded()
    {
        var team = await _db.Teams().AddExternalAsync(new ExternalTeamForm
        {
            Name = "Network Engineering", ContactName = "Alex", ContactEmail = "network@network.example", ConfirmOutside = true,
        });
        await Due(team, "Block the domains");
        await Due(team, "Reset the partner's keys", await Red());
        var options = new ReminderOptions { ExternalReminders = { Enabled = true } };

        await _db.Reminders(options, At(20)).RunAsync(ReminderKind.ExternalReminder);   // two business days before Tuesday

        var email = _db.Email.Sent.Should().ContainSingle().Which;
        email.To.Should().Equal("network@network.example");
        email.Html.Should().Contain("Block the domains").And.Contain("Block the domains: the details");
        email.Html.Should().NotContain("Reset the partner").And.NotContain("partner's keys", "a request above the ceiling isn't named");
        email.Subject.Should().Contain("2 requests", "it still counts");
        email.Text.Should().Contain("1 more request is marked above what we share by email: ask");
        email.Subject.Should().NotContain("RED", "the marking is of what it names");

        await _db.Reminders(options, At(23)).RunAsync(ReminderKind.ExternalReminder);
        _db.Email.Sent.Should().HaveCount(1, "the one withheld was marked reminded too, so it isn't tried again");
    }

    [Fact]
    public async Task TheEscalationContactsEmail_IsHeldToTheCeiling_ForAnExternalTeam_AndForAContactOutsideTheOrganization()
    {
        var inside = await _db.Teams().AddAsync("Identity", null, "identity@example.org");
        await _db.Teams().SetEscalationContactAsync(inside, "Pat Lee", "pat.lee@example.org");
        var outside = await _db.Teams().AddAsync("Facilities", null, "facilities@example.org");
        await _db.Teams().SetEscalationContactAsync(outside, "Sam", "sam@contractor.example", confirmOutside: true);
        var external = await _db.Teams().AddExternalAsync(new ExternalTeamForm
        {
            Name = "Network Engineering", ContactEmail = "network@network.example", EscalationName = "Morgan", EscalationEmail = "morgan@network.example",
            ConfirmOutside = true,
        });
        var red = await Red();
        foreach (var team in new[] { inside, outside, external })
        {
            await Due(team, "Open request");
            await Due(team, "Secret request", red);
        }
        var ladder = new ReminderOptions { Escalation = { Enabled = true, Steps = EscalationLadder.DefaultText } };

        await _db.Reminders(ladder, At(1, month: 12)).RunAsync(ReminderKind.Escalation);   // three business days late: the contacts are copied

        OutgoingEmail To(string address) => _db.Email.Sent.Should().ContainSingle(e => e.To.Contains(address)).Which;
        var pat = To("pat.lee@example.org");
        pat.Html.Should().Contain("Secret request", "a contact inside the organization reads what the team does").And.Contain("/actions/");
        foreach (var held in new[] { To("sam@contractor.example"), To("morgan@network.example") })
        {
            held.Html.Should().Contain("Open request").And.NotContain("Secret request");
            held.Text.Should().Contain("1 more request is marked above what we share by email");
            held.Html.Should().NotContain("/actions/", "no TIDE link for someone outside");
        }
    }

    // --- A2: an address outside the organization is asked about on every list --------------------------------------

    [Fact]
    public async Task TheEditionsRecipients_AskBeforeAnAddressOutside_BothListsAtOnce_AndNotAgainForOneAlreadyThere()
    {
        var settings = _db.Settings();
        var asked = await FluentActions.Invoking(() => settings.SaveRecipientsAsync("cti@example.org; partner@isac.example", "all@mail.example.org; news@press.example"))
            .Should().ThrowAsync<OutsideAddressesException>();
        asked.Which.Outside.Should().Equal("partner@isac.example", "news@press.example");
        asked.Which.Message.Should().Contain("outside example.org").And.Contain("team edition").And.Contain(Tlp.Label(TlpLevel.AmberStrict))
            .And.Contain("company-wide edition");
        (await settings.GetRecipientsAsync(Edition.Team)).Should().BeEmpty("nothing is saved until it's confirmed");

        await settings.SaveRecipientsAsync("cti@example.org; partner@isac.example", "all@mail.example.org", confirmOutside: true);
        (await settings.GetRecipientsAsync(Edition.Team)).Should().Equal("cti@example.org", "partner@isac.example");

        await settings.SaveRecipientsAsync(Edition.Team, "partner@isac.example; soc@example.org");   // already on it: not asked again
        await FluentActions.Invoking(() => settings.SaveRecipientsAsync(Edition.Public, "x@elsewhere.example")).Should().ThrowAsync<OutsideAddressesException>();
    }

    [Fact]
    public async Task TheManagersToldAboutSwaps_AskBeforeAnAddressOutside()
    {
        var rotation = _db.Rotation();
        await rotation.SaveManagerEmailsAsync("cti-manager@example.org");
        var asked = await FluentActions.Invoking(() => rotation.SaveManagerEmailsAsync("cti-manager@example.org; boss@gmail.example"))
            .Should().ThrowAsync<OutsideAddressesException>();
        asked.Which.Outside.Should().ContainSingle().Which.Should().Be("boss@gmail.example");
        (await rotation.ManagerEmailsAsync()).Should().ContainSingle().Which.Should().Be("cti-manager@example.org", "nothing saved until confirmed");
        await rotation.SaveManagerEmailsAsync("cti-manager@example.org; boss@gmail.example", confirmOutside: true);
        (await rotation.ManagerEmailsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task WhosToldTheresNoBrief_AsksBeforeAnAddressOutside_CheckedAgainstTheDomainsSavedWithIt()
    {
        using var rig = new SettingsRig(new() { ["Email:From"] = "tide@example.org" });
        await FluentActions.Invoking(() => rig.Save(OperationalSettings.BriefNotReadyRecipients, "lead@example.org; lead@partner.example"))
            .Should().ThrowAsync<OutsideAddressesException>().WithMessage("*lead@partner.example is outside example.org*");
        rig.Now<ReminderOptions>().BriefNotReady.Recipients.Should().BeEmpty();

        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.BriefNotReadyRecipients] = "lead@example.org; lead@partner.example",
        }, confirmOutside: true);
        rig.Now<ReminderOptions>().BriefNotReady.Recipients.Should().Equal("lead@example.org", "lead@partner.example");

        // Saved with the domains that make it inside: not asked.
        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.OrganizationDomains] = "example.org; partner2.example",
            [OperationalSettings.BriefNotReadyRecipients] = "ops@partner2.example",
        });
        rig.Now<ReminderOptions>().BriefNotReady.Recipients.Should().Equal("ops@partner2.example");
    }

    [Fact]
    public async Task AnExternalTeamsContacts_AreConfirmedWhenFirstSetOrChanged_NotOnEverySave()
    {
        var teams = _db.Teams();
        var form = new ExternalTeamForm { Name = "Hosting Provider", ContactEmail = "noc@provider.example", EscalationEmail = "duty@provider.example" };
        var asked = await FluentActions.Invoking(() => teams.AddExternalAsync(form)).Should().ThrowAsync<OutsideAddressesException>();
        asked.Which.Outside.Should().Equal("noc@provider.example", "duty@provider.example");
        asked.Which.Message.Should().Contain("counted there, not named");
        (await teams.ListAsync()).Should().NotContain(t => t.Name == "Hosting Provider");

        form.ConfirmOutside = true;
        var id = await teams.AddExternalAsync(form);
        teams.IsOutside("noc@provider.example").Should().BeTrue("the form says so beside it");
        teams.IsOutside("noc@mail.example.org").Should().BeFalse();

        var team = (await teams.ListAsync()).Single(t => t.Id == id);
        var edit = ExternalTeamForm.From(team);
        edit.Description = "Our hosting";
        await teams.UpdateExternalAsync(id, edit);   // the same addresses: not asked again

        edit = ExternalTeamForm.From((await teams.ListAsync()).Single(t => t.Id == id));
        edit.ContactEmail = "support@provider.example";
        (await FluentActions.Invoking(() => teams.UpdateExternalAsync(id, edit)).Should().ThrowAsync<OutsideAddressesException>())
            .Which.Outside.Should().ContainSingle().Which.Should().Be("support@provider.example", "only the changed one");
        edit.ContactEmail = "hosting@example.org";
        await teams.UpdateExternalAsync(id, edit);   // inside: nothing to ask

        await FluentActions.Invoking(() => teams.AddExternalAsync("Registrar", "abuse@registrar.example")).Should().ThrowAsync<OutsideAddressesException>();
        await teams.AddExternalAsync("Registrar", "abuse@registrar.example", confirmOutside: true);
    }

    [Fact]
    public async Task AnInternalTeamsEscalationContact_OutsideTheOrganization_IsConfirmed()
    {
        var teams = _db.Teams();
        var id = await teams.AddAsync("Identity", null, null);
        await teams.SetEscalationContactAsync(id, "Pat", "pat@example.org");
        await FluentActions.Invoking(() => teams.SetEscalationContactAsync(id, "Pat", "pat@home.example")).Should().ThrowAsync<OutsideAddressesException>();
        var form = TeamForm.From((await teams.ListAsync()).Single(t => t.Id == id));
        form.EscalationEmail = "pat@home.example";
        await FluentActions.Invoking(() => teams.UpdateAsync(id, form)).Should().ThrowAsync<OutsideAddressesException>();
        form.ConfirmOutside = true;
        await teams.UpdateAsync(id, form);
        (await teams.ListAsync()).Single(t => t.Id == id).EscalationEmail.Should().Be("pat@home.example");
    }

    [Fact]
    public async Task AnInternalTeamsMailbox_OutsideTheOrganization_IsConfirmedWhenSetOrChanged()
    {
        var teams = _db.Teams();
        (await FluentActions.Invoking(() => teams.AddAsync("Facilities", null, "facilities@contractor.example")).Should().ThrowAsync<OutsideAddressesException>())
            .Which.Message.Should().Contain("facilities@contractor.example is outside example.org").And.Contain("reminders and digests");
        (await teams.ListAsync()).Should().NotContain(t => t.Name == "Facilities", "nothing added until it's confirmed");
        var id = await teams.AddAsync("Facilities", null, "facilities@contractor.example", confirmOutside: true);

        await teams.UpdateAsync(id, "Facilities and estates", null, "facilities@contractor.example");   // the same mailbox: not asked again
        await FluentActions.Invoking(() => teams.UpdateAsync(id, "Facilities and estates", null, "estates@contractor.example"))
            .Should().ThrowAsync<OutsideAddressesException>();
        await teams.UpdateAsync(id, "Facilities and estates", null, "facilities@example.org");   // inside: nothing to ask

        var form = TeamForm.From((await teams.ListAsync()).Single(t => t.Id == id));
        form.Email = "estates@contractor.example";
        (await FluentActions.Invoking(() => teams.UpdateAsync(id, form)).Should().ThrowAsync<OutsideAddressesException>())
            .Which.Outside.Should().ContainSingle().Which.Should().Be("estates@contractor.example");
        form.ConfirmOutside = true;
        await teams.UpdateAsync(id, form);
        (await teams.ListAsync()).Single(t => t.Id == id).Email.Should().Be("estates@contractor.example");
    }

    // --- the standing copies, checked again as each email goes ------------------------------------------------------

    [Fact]
    public async Task ACopy_IsCheckedAgainAsItGoes_OneNowOutsideIsDropped_WithAWarning_AConfirmedOneKept()
    {
        var admin = _db.EmailTemplateAdmin();
        var dueSoon = EmailTemplateCatalog.DueSoon.Id;
        await admin.SaveCopiesAsync(dueSoon, "lead@example.org", "partner@isac.example", confirmOutside: true);
        await admin.SaveArchiveAsync("archive@example.org");
        var log = new ListLogger<CopyingEmailSender>();
        var mail = new OutgoingEmail(["robin@example.org"], "s", "<p>h</p>", "t", []) { Template = dueSoon };

        // Inside as saved: every copy goes.
        await new CopyingEmailSender(_db.Email, _db.EmailCopies, EmailBoundary.Fixed("tide@example.org"), log).SendAsync(mail);
        _db.Email.Sent.Last().Should().Match<OutgoingEmail>(e => e.Cc.SequenceEqual(new[] { "lead@example.org" })
                                                                 && e.Bcc.SequenceEqual(new[] { "partner@isac.example", "archive@example.org" }));
        log.Warnings.Should().BeEmpty();

        // The organization's domains changed since: the copy and the archive now outside are dropped; the one confirmed outside still goes.
        await new CopyingEmailSender(_db.Email, _db.EmailCopies, EmailBoundary.Fixed("tide@example.org", "example.net"), log).SendAsync(mail);
        var sent = _db.Email.Sent.Last();
        sent.Cc.Should().BeEmpty();
        sent.Bcc.Should().ContainSingle().Which.Should().Be("partner@isac.example", "confirmed outside when it was saved");
        sent.To.Should().Equal("robin@example.org");
        log.Warnings.Should().ContainSingle().Which.Should().Contain("2 copy addresses outside the organization were dropped")
            .And.Contain("check Email templates").And.NotContain("@", "the log never carries an address");
    }

    // --- the boundary: Email:OrganizationDomains, else the domain of Email:From --------------------------------------

    [Fact]
    public async Task TheBoundary_IsTheOrganizationsDomains_WithTheirSubdomains_ElseTheSendingAddresssDomain()
    {
        using var rig = new SettingsRig(new() { ["Email:From"] = "tide@example.org" });
        var boundary = rig.Get<EmailBoundary>();
        boundary.Current.Domains.Should().Equal("example.org");
        boundary.Current.FromSender.Should().BeTrue("none set: the sending address's domain");

        await rig.Save(OperationalSettings.OrganizationDomains, "Example.COM, @corp.example.net\nexample.com");
        boundary.Current.Domains.Should().Equal("example.com", "corp.example.net");
        boundary.Current.FromSender.Should().BeFalse();
        boundary.Current.IsInside("a@mail.corp.example.net").Should().BeTrue("a subdomain is inside");
        boundary.Current.IsInside("a@example.net").Should().BeFalse("its parent isn't");
        boundary.Current.IsInside("a@example.org").Should().BeFalse("the sending domain isn't added to domains set");
        boundary.Current.IsInside("a@notexample.com").Should().BeFalse();

        await FluentActions.Invoking(() => rig.Save(OperationalSettings.OrganizationDomains, "not a domain"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*isn't a domain name*");

        await rig.Save(OperationalSettings.OrganizationDomains, "");
        boundary.Current.Domains.Should().ContainSingle().Which.Should().Be("example.org", "emptied: the sending address's domain again");

        using var server = new SettingsRig(new() { ["Email:From"] = "tide@example.org", ["Email:OrganizationDomains:0"] = "example.net" });
        server.Get<EmailBoundary>().Current.Domains.Should().ContainSingle().Which.Should().Be("example.net", "set on the server");
        using var none = new SettingsRig(new() { ["Email:From"] = null });
        none.Get<EmailBoundary>().Current.Known.Should().BeFalse("no domains and no sending address: nothing is inside");
        none.Get<EmailBoundary>().Current.IsInside("a@example.org").Should().BeFalse();
    }

    [Fact]
    public async Task AnImportedAddressList_FlagsAnAddressOutside()
    {
        using var rig = new SettingsRig(new() { ["Email:From"] = "tide@example.org" });
        var bundle = new Tide.Application.Operations.ConfigBundle(Tide.Application.Operations.ConfigBundle.FormatName, Tide.Application.Operations.ConfigBundle.CurrentVersion,
            DateTimeOffset.UtcNow, "x", "1", [new(DistributionSettings.TeamRecipientsKey, "cti@example.org; partner@isac.example")], [], [], [], [], []);
        var preview = await rig.Get<Tide.Application.Operations.ConfigBundleService>().PreviewAsync(bundle);
        preview.Changes.Should().ContainSingle(c => c.Item == DistributionSettings.TeamRecipientsKey)
            .Which.Should().Match<Tide.Application.Operations.BundleChange>(c => c.Warning && c.Detail!.Contains("partner@isac.example"));
    }
}

/// <summary>A logger that keeps its warnings.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
    }
}
