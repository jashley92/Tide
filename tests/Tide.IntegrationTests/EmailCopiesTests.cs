using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Distribution;
using Tide.Domain.Enums;
using Tide.Infrastructure.Notifications;

namespace Tide.IntegrationTests;

/// <summary>
/// Standing copies on the email templates: what an administrator may save (and what's refused or asked about), how they
/// join each email on its way out, and that a copy the relay refuses never costs the email its recipients.
/// </summary>
public class EmailCopiesTests : IDisposable
{
    private readonly TestDb _db = new();

    public EmailCopiesTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private static readonly string DueSoon = EmailTemplateCatalog.DueSoon.Id, External = EmailTemplateCatalog.ExternalReminder.Id;

    [Fact]
    public async Task AdminsSaveCopies_ChecksEachAddress_AndAskBeforeCopyingOutsideTheOrganization()
    {
        var admin = _db.EmailTemplateAdmin();
        admin.HomeDomain.Should().Be("example.org", "the sending address's domain");

        await admin.SaveCopiesAsync(DueSoon, "soc-lead@example.org", "records@mail.example.org;  records@MAIL.example.org");
        admin.Copies(DueSoon).Should().BeEquivalentTo(new TemplateCopies(["soc-lead@example.org"], ["records@mail.example.org"]), "a subdomain is inside; each address once");

        await FluentActions.Invoking(() => admin.SaveCopiesAsync(DueSoon, "not an address", null)).Should().ThrowAsync<ArgumentException>().WithMessage("*isn't an email address*");
        await FluentActions.Invoking(() => admin.SaveCopiesAsync(DueSoon, null, "a@example.org b@example.org c@example.org d@example.org e@example.org f@example.org"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*at most 5*");

        // Outside the organization: asked, naming the address; saved only once confirmed.
        var asked = await FluentActions.Invoking(() => admin.SaveCopiesAsync(DueSoon, null, "partner@isac.example")).Should().ThrowAsync<OutsideCopiesException>();
        asked.Which.Outside.Should().Equal("partner@isac.example");
        asked.Which.Message.Should().Contain("outside example.org");
        admin.Copies(DueSoon).Bcc.Should().ContainSingle().Which.Should().Be("records@mail.example.org", "nothing changed until it's confirmed");
        await admin.SaveCopiesAsync(DueSoon, null, "partner@isac.example", confirmOutside: true);
        admin.Copies(DueSoon).Should().BeEquivalentTo(new TemplateCopies([], ["partner@isac.example"]));

        // An email to people outside TIDE takes blind copies only.
        await FluentActions.Invoking(() => admin.SaveCopiesAsync(External, "soc-lead@example.org", null)).Should().ThrowAsync<ArgumentException>().WithMessage("*use Blind copy*");
        await admin.SaveCopiesAsync(External, null, "soc-lead@example.org");
        admin.Copies(External).Bcc.Should().Equal("soc-lead@example.org");

        // Blank clears them.
        await admin.SaveCopiesAsync(DueSoon, " ", "");
        admin.Copies(DueSoon).Any.Should().BeFalse();
    }

    [Fact]
    public async Task TheArchiveCopy_IsOneAddressInsideTheOrganization_Only()
    {
        var admin = _db.EmailTemplateAdmin();
        await admin.SaveArchiveAsync("tide-archive@example.org");
        admin.Archive.Should().Be("tide-archive@example.org");
        await FluentActions.Invoking(() => admin.SaveArchiveAsync("records@elsewhere.example")).Should().ThrowAsync<ArgumentException>().WithMessage("*must be inside example.org*");
        await FluentActions.Invoking(() => admin.SaveArchiveAsync("a@example.org; b@example.org")).Should().ThrowAsync<ArgumentException>().WithMessage("*at most 1 address*");
        await FluentActions.Invoking(() => _db.EmailTemplateAdmin(from: null).SaveArchiveAsync("tide-archive@example.org"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*set Email:From*", "with no domain known, nothing is inside it");
        await admin.SaveArchiveAsync("");
        admin.Archive.Should().BeNull();
    }

    [Fact]
    public void ACopy_CantArriveInAConfigurationBundle()
    {
        Tide.Application.Operations.ConfigBundleService.SettingProblem(new Tide.Application.Operations.BundleSetting(EmailCopyRules.ArchiveKey, "x@example.org"))
            .Should().NotBeNull("an import can't add a copy nobody confirmed");
        Tide.Application.Operations.ConfigBundleService.SettingProblem(new Tide.Application.Operations.BundleSetting(EmailCopyRules.Key(DueSoon, bcc: true), "x@elsewhere.example"))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task OnlyAnAdministrator_ChangesCopies()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => _db.EmailTemplateAdmin().SaveCopiesAsync(DueSoon, null, "x@example.org")).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
        await FluentActions.Invoking(() => _db.EmailTemplateAdmin().SaveArchiveAsync("x@example.org")).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    [Fact]
    public async Task EachEmail_CarriesItsTemplatesCopies_AndTheArchive_BlindOnlyForOneThatGoesOutside_AndNoOneTwice()
    {
        var admin = _db.EmailTemplateAdmin();
        await admin.SaveCopiesAsync(DueSoon, "lead@example.org", "records@example.org");
        await admin.SaveArchiveAsync("archive@example.org");
        var sender = new CopyingEmailSender(_db.Email, _db.EmailCopies);

        await sender.SendAsync(new OutgoingEmail(["robin@example.org", "lead@example.org"], "s", "<p>h</p>", "t", []) { Template = DueSoon });
        var due = _db.Email.Sent.Last();
        due.Cc.Should().BeEmpty("the lead is already a recipient");
        due.Bcc.Should().Equal("records@example.org", "archive@example.org");

        // Saved as CC before the rule, or however: an email to people outside TIDE gets every copy blind.
        await sender.SendAsync(new OutgoingEmail(["noc@provider.example"], "s", "<p>h</p>", "t", []) { Template = External });
        _db.Email.Sent.Last().Should().Match<OutgoingEmail>(e => e.Cc.Count == 0 && e.Bcc.SequenceEqual(new[] { "archive@example.org" }));

        // A brief's edition names no template: the archive only.
        await sender.SendAsync(new OutgoingEmail(["all-staff@example.org"], "s", "<p>h</p>", "t", []));
        _db.Email.Sent.Last().Bcc.Should().Equal("archive@example.org");

        var (cc, bcc) = EmailCopyRules.For(new OutgoingEmail(["x@example.org"], "s", "h", "t", []) { Template = External },
            new TemplateCopies(["internal@example.org"], []), null);
        cc.Should().BeEmpty();
        bcc.Should().Equal("internal@example.org");
    }

    [Fact]
    public async Task ARefusedCopy_NeverCostsTheEmailItsRecipients_AndARefusedRecipientIsStillAFailure()
    {
        var mail = new OutgoingEmail(["soc@example.org"], "s", "<p>h</p>", "t", []) { Bcc = ["gone@example.org"] };
        using (var relay = new MisbehavingSmtpServer(MisbehavingSmtpServer.Fault.RefuseOneRecipient) { Refuse = "gone@example.org" })
        {
            var result = await Sender(relay.Port).SendAsync(mail);
            result.Should().Be(new EmailSendResult(EmailSendStatus.Sent) { RefusedCopies = 1 }, "the email went to its recipient");
            relay.DataReceived.Should().Be(1);
        }
        using (var relay = new MisbehavingSmtpServer(MisbehavingSmtpServer.Fault.RefuseOneRecipient) { Refuse = "soc@example.org" })
        {
            var result = await Sender(relay.Port).SendAsync(mail);
            result.Status.Should().Be(EmailSendStatus.Failed, "its one recipient was refused: only the copy got it");
            result.RefusedCopies.Should().Be(0);
        }
    }

    private static EmailSender Sender(int port) =>
        new(new StaticOptionsMonitor<EmailOptions>(new EmailOptions
        {
            Enabled = true, SmtpHost = "127.0.0.1", SmtpPort = port, EnableSsl = false, From = "tide@example.org", SmtpTimeoutSeconds = 5,
        }), NullLogger<EmailSender>.Instance, new OutboundCircuit());
}

/// <summary>Email templates, rendered in the real app: the archive copy above the list, each template's copies beside its editor.</summary>
public class EmailCopiesPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheEmailTemplatesPage_HasTheArchiveCopy_AndEachTemplatesCopies()
    {
        await using var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.EmailTemplatesAdmin>(app, "/admin/email-templates");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("id=\"etpl-archive\"").And.Contain("Archive copy").And.Contain("every</strong> email TIDE sends")
            .And.Contain("id=\"etpl-cc\"").And.Contain("id=\"etpl-bcc\"").And.Contain("aren't carried in a configuration bundle");
    }
}
