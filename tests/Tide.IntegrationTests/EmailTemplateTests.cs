using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Distribution;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages.Admin;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Administration › Email templates: saving, resetting and previewing the notification emails' wording, and
/// the reminders and rotation emails using it.</summary>
public class EmailTemplateServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public EmailTemplateServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private EmailTemplateView View(IReadOnlyList<EmailTemplateView> all, EmailTemplate t) => all.Single(v => v.Template == t);

    [Fact]
    public async Task ASave_KeepsOnlyWhatDiffersFromTheDefault_AndTheNextReminderUsesIt()
    {
        var svc = _db.EmailTemplateAdmin();
        var before = View(await svc.GetAllAsync(), EmailTemplateCatalog.DueSoon);
        before.IsCustomized.Should().BeFalse();

        await svc.SaveAsync("due-soon", "TIDE heads-up: {{Actions}} due", before.Heading, "Please look at these.\n\n{{ActionList}}", before.Stamp);

        await using (var ctx = _db.Context())
            ctx.Settings.Where(s => s.Key.StartsWith(EmailTemplateCatalog.KeyPrefix)).Select(s => s.Key).ToList()
                .Should().BeEquivalentTo(["EmailTemplate:due-soon:Subject", "EmailTemplate:due-soon:Body"], "the heading was left as its default");
        View(await svc.GetAllAsync(), EmailTemplateCatalog.DueSoon).IsCustomized.Should().BeTrue();

        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Rotate the <keys>", TeamId = team, DueDate = _db.Clock.Today });
        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(1);
        var mail = _db.Email.Sent.Single();
        mail.Subject.Should().Be("TIDE heads-up: 1 action due");
        mail.Html.Should().Contain("Please look at these.").And.Contain("Rotate the &lt;keys&gt;").And.Contain("1 action due soon", "the heading is still the default");
    }

    [Fact]
    public async Task ASaveFromAStalePage_IsRefused_AndAResetPutsTheDefaultBack()
    {
        var svc = _db.EmailTemplateAdmin();
        var opened = View(await svc.GetAllAsync(), EmailTemplateCatalog.Presenting);
        await svc.SaveAsync("presenting", "First: {{FirstDay}}", opened.Heading, opened.Body, opened.Stamp);

        var stale = () => svc.SaveAsync("presenting", "Second: {{FirstDay}}", opened.Heading, opened.Body, opened.Stamp);
        (await stale.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*changed*since you opened it*");

        await svc.ResetAsync("presenting");
        var reset = View(await svc.GetAllAsync(), EmailTemplateCatalog.Presenting);
        reset.IsCustomized.Should().BeFalse();
        reset.Subject.Should().Be(EmailTemplateCatalog.Presenting.DefaultSubject);
        _db.EmailTemplates.IsCustomized(EmailTemplateCatalog.Presenting).Should().BeFalse("the store was read again");
    }

    [Fact]
    public async Task ABadTemplate_IsRefused_AndOnlyAnAdministratorMayChangeThem()
    {
        var svc = _db.EmailTemplateAdmin();
        var v = View(await svc.GetAllAsync(), EmailTemplateCatalog.Overdue);
        var missing = () => svc.SaveAsync("overdue", v.Subject, v.Heading, "No list.", v.Stamp);
        (await missing.Should().ThrowAsync<ArgumentException>()).WithMessage("*needs {{ActionList}}*");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await svc.Invoking(s => s.GetAllAsync()).Should().ThrowAsync<ForbiddenException>();
        await svc.Invoking(s => s.ResetAsync("overdue")).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ThePreview_AndATestSend_UseSampleValues_AndGoOnlyToTheAdministrator()
    {
        var svc = _db.EmailTemplateAdmin();
        var t = EmailTemplateCatalog.Digest;
        var preview = svc.Preview(t.Id, t.DefaultSubject, t.DefaultHeading, t.DefaultBody);
        preview.Subject.Should().StartWith("TIDE: Detection engineering actions, week of");
        preview.Html.Should().Contain("Detection engineering: the week&#39;s actions").And.Contain("Closed in the last week").And.Contain("A-41");

        var result = await svc.SendTestAsync(t.Id, "Digest for {{Team}}", t.DefaultHeading, t.DefaultBody);
        result.Status.Should().Be(EmailSendStatus.Sent);
        var mail = _db.Email.Sent.Single();
        mail.To.Should().Equal("test@example.com");
        mail.Subject.Should().Be("[TEST] Digest for Detection engineering");
        View(await svc.GetAllAsync(), t).IsCustomized.Should().BeFalse("a test send saves nothing");
    }

    [Fact]
    public async Task TheRotationEmails_UseTheirTemplates()
    {
        var svc = _db.EmailTemplateAdmin();
        var v = View(await svc.GetAllAsync(), EmailTemplateCatalog.RotationRequest);
        await svc.SaveAsync(v.Template.Id, "Cover needed: {{Requester}} asks {{Partner}}", v.Heading, v.Body, v.Stamp);

        EmailTemplateStore.Defaults.IsCustomized(EmailTemplateCatalog.RotationRequest).Should().BeFalse("only this database's store changed");
        _db.EmailTemplates.Render(EmailTemplateCatalog.RotationRequest, new Dictionary<string, string> { ["Requester"] = "Sam", ["Partner"] = "Robin" })
            .Subject.Should().Be("Cover needed: Sam asks Robin");
    }

    [Fact]
    public async Task TheConfigurationBundle_CarriesTheTemplates_AndChecksThemOnImport()
    {
        var svc = _db.EmailTemplateAdmin();
        var v = View(await svc.GetAllAsync(), EmailTemplateCatalog.BriefNotReady);
        await svc.SaveAsync(v.Template.Id, "Brief late!", v.Heading, v.Body, v.Stamp);
        var bundles = new ConfigBundleService(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System, emailTemplates: _db.EmailTemplates);

        var bundle = await bundles.ExportAsync();
        bundle.Settings.Should().Contain(new BundleSetting("EmailTemplate:brief-not-ready:Subject", "Brief late!"));

        var incoming = bundle with
        {
            Settings =
            [
                new BundleSetting("EmailTemplate:brief-not-ready:Subject", "Imported: {{MeetingTime}}"),
                new BundleSetting("EmailTemplate:overdue:Body", "No list here."),
                new BundleSetting("EmailTemplate:overdue:Footer", "x"),
            ],
        };
        var done = await bundles.ImportAsync(incoming);
        done.Changes.Single(c => c.Item == "EmailTemplate:brief-not-ready:Subject").Kind.Should().Be(BundleChangeKind.Update);
        done.Changes.Single(c => c.Item == "EmailTemplate:overdue:Body").Kind.Should().Be(BundleChangeKind.Skipped);
        done.Changes.Single(c => c.Item == "EmailTemplate:overdue:Footer").Kind.Should().Be(BundleChangeKind.Skipped);
        _db.EmailTemplates.Text(EmailTemplateCatalog.BriefNotReady, EmailPart.Subject).Should().Be("Imported: {{MeetingTime}}", "an import takes effect at once");
    }

    [Fact]
    public async Task ASavedRowThatNoLongerPasses_IsIgnored()
    {
        await using (var ctx = _db.Context())
        {
            ctx.Settings.Add(new AppSetting { Key = "EmailTemplate:overdue:Body", Value = "Lost the list", UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }
        await _db.EmailTemplates.RefreshAsync(_db);
        _db.EmailTemplates.Text(EmailTemplateCatalog.Overdue, EmailPart.Body).Should().Be(EmailTemplateCatalog.Overdue.DefaultBody);
        View(await _db.EmailTemplateAdmin().GetAllAsync(), EmailTemplateCatalog.Overdue).Body.Should().Be(EmailTemplateCatalog.Overdue.DefaultBody);
    }
}

/// <summary>The Email templates page, drawn for an administrator.</summary>
public class EmailTemplatesPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task ThePage_ListsTheEmails_AndPreviewsTheFirst()
    {
        await using var r = await RenderAsync<EmailTemplatesAdmin>(app, "/admin/email-templates");
        var html = await r.HtmlOnce("Preview of the email");

        html.Should().Contain("<h1 class=\"tide-admin-title\">Email templates</h1>");
        foreach (var t in EmailTemplateCatalog.All) html.Should().Contain(System.Net.WebUtility.HtmlEncode(t.Title));
        html.Should().Contain("sandbox srcdoc=", "the preview runs nothing").And.Contain("{{ActionList}}");
    }
}
