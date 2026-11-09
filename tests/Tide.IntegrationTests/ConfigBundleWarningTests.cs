using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Distribution;
using Tide.Application.Operations;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The bundle import flags a change that widens what leaves the organization, as it flags a role mapping that gives
/// Admin: a higher edition ceiling, more recipients, a lower source TLP, email turned on or reworded for people outside
/// TIDE, escalation steps that email outside. Each is still imported once the warnings are read; the service refuses
/// otherwise, and refuses an import whose plan differs from the preview read. A routine change isn't flagged.
/// </summary>
public sealed class ConfigBundleWarningTests : IDisposable
{
    private readonly TestDb _db = new();

    public ConfigBundleWarningTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private ConfigBundleService Service(DistributionOptions? distribution = null, ReminderOptions? reminders = null) =>
        new(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System,
            distribution: distribution is null ? null : Options.Create(distribution), reminders: reminders is null ? null : Options.Create(reminders));

    private static ConfigBundle Bundle(IReadOnlyList<BundleSetting>? settings = null, IReadOnlyList<BundleSource>? sources = null) =>
        new(ConfigBundle.FormatName, 1, DateTimeOffset.UtcNow, "Tester", "dev", settings ?? [], [], sources ?? [], [], []);

    // One change warned (a company-wide recipient), one routine (the reminder time).
    private static ConfigBundle WarnedBundle() =>
        Bundle([new(DistributionSettings.PublicRecipientsKey, "all-staff@contoso.com"), new(OperationalSettings.ReminderTime, "09:30")]);

    private static BundleSource Feed(string name, string tlp) =>
        new(name, "Feed", "https://vendor.example.org/feed", tlp, "C", null, 60, true, false, null, null, null, null);

    private async Task<BundleChange> Preview(BundleSetting setting, ConfigBundleService? service = null) =>
        (await (service ?? Service()).PreviewAsync(Bundle([setting]))).Changes.Single(c => c.Item == setting.Key);

    private async Task Save(string key, string value)
    {
        await using var ctx = _db.Context();
        ctx.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedBy = "x" });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task RaisingTheTeamCeiling_IsWarned_WithFromAndTo()
    {
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.Amber);
        var c = await Preview(new(DistributionSettings.TeamMaxTlpKey, "Red"));
        c.Kind.Should().Be(BundleChangeKind.Update);
        c.Warning.Should().BeTrue();
        c.Detail.Should().Be("Raises the team edition's ceiling from TLP:AMBER to TLP:RED.");
    }

    [Fact]
    public async Task RaisingTheCompanyWideCeiling_OverTheServersDefault_IsWarned()
    {
        // Nothing saved: the server's value is what's in force.
        var c = await Preview(new(DistributionSettings.PublicMaxTlpKey, "Amber"), Service(new DistributionOptions { PublicMaxTlp = TlpLevel.Clear }));
        c.Kind.Should().Be(BundleChangeKind.Add);
        c.Warning.Should().BeTrue();
        c.Detail.Should().Contain("company-wide edition's ceiling").And.Contain(Tlp.Label(TlpLevel.Clear)).And.Contain(Tlp.Label(TlpLevel.Amber));
    }

    [Fact]
    public async Task LoweringACeiling_IsNotWarned()
    {
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.Red);
        var c = await Preview(new(DistributionSettings.TeamMaxTlpKey, "Amber"));
        c.Kind.Should().Be(BundleChangeKind.Update);
        c.Warning.Should().BeFalse();
    }

    [Fact]
    public async Task AddingTeamEditionRecipients_IsWarned_NamingThem()
    {
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@contoso.com");
        var c = await Preview(new(DistributionSettings.TeamRecipientsKey, "cti@contoso.com; a@partner.example; b@partner.example"));
        c.Warning.Should().BeTrue();
        c.Detail.Should().Be("Adds 2 recipients to the team edition: a@partner.example, b@partner.example.");
    }

    [Fact]
    public async Task AddingCompanyWideRecipients_IsWarned()
    {
        var c = await Preview(new(DistributionSettings.PublicRecipientsKey, "all-staff@contoso.com"));
        c.Warning.Should().BeTrue();
        c.Detail.Should().Be("Adds 1 recipient to the company-wide edition: all-staff@contoso.com.");
    }

    [Fact]
    public async Task RemovingRecipients_IsNotWarned()
    {
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "a@contoso.com; b@contoso.com");
        var c = await Preview(new(DistributionSettings.PublicRecipientsKey, "A@contoso.com"));
        c.Kind.Should().Be(BundleChangeKind.Update);
        c.Warning.Should().BeFalse("the same address in other case, and one fewer, sends to no one new");
    }

    [Fact]
    public async Task AddingRecipients_OverTheServersList_CountsOnlyTheNewOnes()
    {
        var c = await Preview(new(DistributionSettings.TeamRecipientsKey, "cti@contoso.com; new@contoso.com"),
            Service(new DistributionOptions { TeamRecipients = ["cti@contoso.com"] }));
        c.Detail.Should().Be("Adds 1 recipient to the team edition: new@contoso.com.");
    }

    [Fact]
    public async Task AddingBriefNotReadyRecipients_IsWarned()
    {
        var reminders = new ReminderOptions();
        reminders.BriefNotReady.Recipients = ["lead@contoso.com"];
        var c = await Preview(new(OperationalSettings.BriefNotReadyRecipients, "lead@contoso.com; someone@elsewhere.example"), Service(reminders: reminders));
        c.Warning.Should().BeTrue();
        c.Detail.Should().Be("Adds 1 recipient to those told when there's no brief: someone@elsewhere.example.");
    }

    [Fact]
    public async Task AddingRotationManagers_IsWarned()
    {
        await Save(Tide.Application.Rotation.RotationService.ManagerEmailsKey, "boss@contoso.com");
        var c = await Preview(new(Tide.Application.Rotation.RotationService.ManagerEmailsKey, "boss@contoso.com; other@elsewhere.example"));
        c.Kind.Should().Be(BundleChangeKind.Update);
        c.Warning.Should().BeTrue();
        c.Detail.Should().Be("Adds 1 recipient to the managers told about rotation swaps: other@elsewhere.example.");
    }

    [Fact]
    public async Task ManyRecipients_AreNamedFiveAtATime()
    {
        var list = string.Join("; ", Enumerable.Range(1, 8).Select(i => $"r{i}@contoso.com"));
        var c = await Preview(new(DistributionSettings.TeamRecipientsKey, list));
        c.Detail.Should().StartWith("Adds 8 recipients to the team edition: r1@contoso.com, ").And.EndWith("r5@contoso.com and 3 more.");
    }

    [Fact]
    public async Task LoweringASourcesDefaultTlp_IsWarned_ForItLoosensTheEditionsAndTheAiGate()
    {
        await using (var ctx = _db.Context())
        {
            ctx.Sources.Add(new Source { Name = "Vendor blog", Url = "https://vendor.example.org/feed", DefaultTlp = TlpLevel.Amber });
            ctx.Sources.Add(new Source { Name = "Other blog", Url = "https://other.example.org/feed", DefaultTlp = TlpLevel.Green });
            await ctx.SaveChangesAsync();
        }
        var preview = await Service().PreviewAsync(Bundle(sources:
            [Feed("Vendor blog", "Clear"), Feed("Other blog", "Red"), Feed("New blog", "Clear")]));
        var lowered = preview.Changes.Single(c => c.Item == "Vendor blog");
        lowered.Kind.Should().Be(BundleChangeKind.Update);
        lowered.Warning.Should().BeTrue();
        lowered.Detail.Should().Contain($"from {Tlp.Label(TlpLevel.Amber)} to {Tlp.Label(TlpLevel.Clear)}").And.Contain("AI provider");
        preview.Changes.Single(c => c.Item == "Other blog").Warning.Should().BeFalse("raising a source's TLP narrows what leaves");
        preview.Changes.Single(c => c.Item == "New blog").Warning.Should().BeFalse("a new source has nothing yet to loosen");

        // Still imported once read: the warning doesn't stop it.
        await Service().PreviewAndImportAsync(Bundle(sources: [Feed("Vendor blog", "Clear")]));
        await using var check = _db.Context();
        check.Sources.Single(s => s.Name == "Vendor blog").DefaultTlp.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public async Task TheAiLimit_DoesNotTravelInTheBundle()
    {
        var c = await Preview(new("Ai:MaxTlp", "Red"));
        c.Kind.Should().Be(BundleChangeKind.Skipped, "it's the server's setting, never changed in the app");
    }

    [Theory]
    [InlineData("external-reminder")]
    [InlineData("escalation")]
    public async Task RewordingAnEmailThatGoesOutside_IsWarned(string id)
    {
        var t = EmailTemplateCatalog.ById(id)!;
        var key = EmailTemplateCatalog.Key(t.Id, EmailPart.Body);
        var c = await Preview(new(key, t.DefaultBody + "\n\nWire the money to this account."));
        c.Warning.Should().BeTrue();
        c.Detail.Should().StartWith($"Changes the body of the {t.Title} email, which goes to people outside TIDE: ");
    }

    [Fact]
    public async Task AnEmailThatGoesOutside_AtItsDefaultWording_IsNotWarned()
    {
        var t = EmailTemplateCatalog.ExternalReminder;
        var c = await Preview(new(EmailTemplateCatalog.Key(t.Id, EmailPart.Body), t.DefaultBody));
        c.Warning.Should().BeFalse("nothing in force changes");
    }

    [Fact]
    public async Task RewordingAnInternalEmail_IsNotWarned()
    {
        var t = EmailTemplateCatalog.Overdue;
        var c = await Preview(new(EmailTemplateCatalog.Key(t.Id, EmailPart.Body), t.DefaultBody + "\n\nThanks."));
        c.Kind.Should().Be(BundleChangeKind.Add);
        c.Warning.Should().BeFalse();
    }

    [Theory]
    [InlineData(OperationalSettings.ExternalReminders, "external teams")]
    [InlineData(OperationalSettings.Escalation, "escalation ladder")]
    public async Task TurningOnEmailThatGoesOutside_IsWarned_ButNotWhenItsOnAlready(string key, string named)
    {
        var c = await Preview(new(key, "true"));
        c.Warning.Should().BeTrue();
        c.Detail.Should().Contain(named).And.Contain("outside TIDE");

        (await Preview(new(key, "false"))).Warning.Should().BeFalse("turning it off sends less");

        var on = new ReminderOptions();
        on.ExternalReminders.Enabled = true;
        on.Escalation.Enabled = true;
        (await Preview(new(key, "true"), Service(reminders: on))).Warning.Should().BeFalse("the server has it on already");
    }

    [Fact]
    public async Task ARoutineChange_IsNotWarned()
    {
        await Save(OperationalSettings.ReminderTime, "08:00");
        var preview = await Service().PreviewAsync(Bundle([new(OperationalSettings.ReminderTime, "09:30"), new(OperationalSettings.TeamName, "CTI")]));
        preview.Changes.Single(c => c.Item == OperationalSettings.ReminderTime).Should()
            .Match<BundleChange>(c => c.Kind == BundleChangeKind.Update && !c.Warning && c.Detail == "09:30");
        preview.Warnings.Should().Be(0);
    }

    [Fact]
    public async Task EscalationSteps_ThatAddAContactOrRemindStep_AreWarned_ButNotOnesThatOnlyRaiseOrFlag()
    {
        await Save(OperationalSettings.EscalationSteps, "after 7: raise, flag");
        var added = await Preview(new(OperationalSettings.EscalationSteps, "before 2: remind; after 3: contact; after 7: raise, flag"));
        added.Warning.Should().BeTrue();
        added.Detail.Should().Be("Adds escalation steps that email people outside TIDE (escalation contacts, external teams): before 2: remind; after 3: contact.");

        (await Preview(new(OperationalSettings.EscalationSteps, "after 7: raise, flag; after 14: flag"))).Warning
            .Should().BeFalse("raising and flagging email no one");
        (await Preview(new(OperationalSettings.EscalationSteps, "after 7: flag"))).Warning.Should().BeFalse();
    }

    [Fact]
    public async Task EscalationSteps_AreComparedWithTheServersLadder_WhenNoneIsSaved()
    {
        // Nothing saved: the default ladder (before 2: remind; after 3: contact; ...) is in force, so those aren't new.
        var same = await Preview(new(OperationalSettings.EscalationSteps, "before 2: remind; after 3: contact"));
        same.Warning.Should().BeFalse();
        var moved = await Preview(new(OperationalSettings.EscalationSteps, "before 2: remind; after 1: contact"));
        moved.Warning.Should().BeTrue("the contact is now copied two days sooner");
        moved.Detail.Should().EndWith(": after 1: contact.");
    }

    [Fact]
    public async Task AWarnedImport_NotAcknowledged_IsRefused_AndChangesNothing()
    {
        var bundle = WarnedBundle();
        var preview = await Service().PreviewAsync(bundle);
        preview.Warnings.Should().Be(1);
        await Service().Invoking(s => s.ImportAsync(bundle, preview.Fingerprint(), acknowledged: false)).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("1 change needs a careful look: confirm you've read the warnings, then import.");
        await using var ctx = _db.Context();
        ctx.Settings.Should().BeEmpty("a refused import writes nothing, the routine change included");
    }

    [Fact]
    public async Task AnImport_WhosePlanChangedSinceThePreview_IsRefused_AndChangesNothing()
    {
        var bundle = Bundle([new(DistributionSettings.TeamRecipientsKey, "cti@contoso.com; new@contoso.com"), new(OperationalSettings.ReminderTime, "09:30")]);
        var preview = await Service().PreviewAsync(bundle);
        // Someone changes the list here after the preview: the import would now add a different set.
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@contoso.com");
        await Service().Invoking(s => s.ImportAsync(bundle, preview.Fingerprint(), acknowledged: true)).Should()
            .ThrowAsync<Tide.Application.Articles.StaleEditException>().WithMessage("The configuration changed since you previewed it: preview again.");
        await Service().Invoking(s => s.ImportAsync(bundle, "not a fingerprint", acknowledged: true)).Should()
            .ThrowAsync<Tide.Application.Articles.StaleEditException>();
        await using var ctx = _db.Context();
        ctx.Settings.Single().Value.Should().Be("cti@contoso.com", "nothing the bundle carries was written");
    }

    [Fact]
    public async Task AWarnedImport_Acknowledged_WithTheFingerprintOfThePreviewRead_Applies()
    {
        var bundle = WarnedBundle();
        var preview = await Service().PreviewAsync(bundle);
        var done = await Service().ImportAsync(bundle, preview.Fingerprint(), acknowledged: true);
        done.Fingerprint().Should().Be(preview.Fingerprint(), "what was imported is what was read");
        done.Warnings.Should().Be(1);
        await using var ctx = _db.Context();
        ctx.Settings.Single(s => s.Key == DistributionSettings.PublicRecipientsKey).Value.Should().Be("all-staff@contoso.com");
        ctx.Settings.Single(s => s.Key == OperationalSettings.ReminderTime).Value.Should().Be("09:30");
    }
}
